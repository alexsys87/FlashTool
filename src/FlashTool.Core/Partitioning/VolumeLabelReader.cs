using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FlashTool.Core.Partitioning;

/// <summary>
/// Reads the volume label of a file system, the name Explorer shows for a drive. A label is a hint
/// for people ("bootfs", "BACKUP"), so anything unexpected simply yields an empty string.
/// </summary>
/// <remarks>
/// FAT labels come from the boot sector, as <c>fatlabel</c> and <c>mkfs.fat -n</c> write them; a label
/// that was only set through the root directory entry is not seen. FAT labels are stored in an OEM code
/// page: ASCII is shown as is, other bytes are decoded with the OEM page of the current culture.
/// </remarks>
public static class VolumeLabelReader
{
    private const int FatLabelLength = 11;
    private const int NtfsMftRecordNumberOfVolume = 3;
    private const int NtfsFixupStride = 512;
    private const uint NtfsVolumeNameAttribute = 0x60;
    private const uint NtfsEndOfAttributes = 0xFFFFFFFF;
    private const byte ExFatVolumeLabelEntry = 0x83;
    private const int ExFatMaxLabelCharacters = 11;

    /// <summary>
    /// Reads the label of the file system that starts at <paramref name="offset"/>.
    /// </summary>
    /// <param name="fileSystem">What <see cref="FileSystemProbe"/> found there.</param>
    /// <param name="data">The first bytes of the file system, as seen by the probe (may be shorter than needed).</param>
    /// <param name="offset">Absolute start of the file system on the device or image.</param>
    /// <param name="read">Reads (absolute offset, count) for labels that live further in (NTFS, exFAT); null when unreadable.</param>
    public static string Read(FileSystemType fileSystem, ReadOnlySpan<byte> data, long offset, Func<long, int, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        try
        {
            return fileSystem switch
            {
                FileSystemType.Fat12 or FileSystemType.Fat16 => Fat(data, 43),
                FileSystemType.Fat32 => Fat(data, 71),
                FileSystemType.Ext2 or FileSystemType.Ext3 or FileSystemType.Ext4 => Ext(data),
                FileSystemType.Ntfs => Ntfs(data, offset, read),
                FileSystemType.ExFat => ExFat(data, offset, read),
                _ => string.Empty,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException)
        {
            // Damaged or unusual structures: no label, never a failure of the whole inspection.
            return string.Empty;
        }
    }

    private static string Fat(ReadOnlySpan<byte> boot, int labelOffset)
    {
        if (boot.Length < labelOffset + FatLabelLength)
        {
            return string.Empty;
        }

        var label = DecodeOem(boot.Slice(labelOffset, FatLabelLength)).TrimEnd(' ', '\0');

        // What formatting tools write when no label was given.
        return label.Equals("NO NAME", StringComparison.OrdinalIgnoreCase) ? string.Empty : Clean(label);
    }

    private static string Ext(ReadOnlySpan<byte> data)
    {
        // The label sits at offset 120 of the superblock, which starts at 1024: 16 bytes of UTF-8.
        if (data.Length < 1024 + 120 + 16)
        {
            return string.Empty;
        }

        var raw = data.Slice(1024 + 120, 16);
        var end = raw.IndexOf((byte)0);
        return Clean(Encoding.UTF8.GetString(end >= 0 ? raw[..end] : raw));
    }

    private static string Ntfs(ReadOnlySpan<byte> boot, long offset, Func<long, int, byte[]?> read)
    {
        if (boot.Length < 0x48)
        {
            return string.Empty;
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[0x0B..]);
        long sectorsPerCluster = boot[0x0D];
        if (sectorsPerCluster > 0x80)
        {
            // Clusters above 64 KiB are written as 2^(256 - n) sectors.
            sectorsPerCluster = 1L << (256 - (int)sectorsPerCluster);
        }

        var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot[0x30..]);
        var recordCode = unchecked((sbyte)boot[0x40]);
        if (bytesPerSector is < 512 or > 4096 || (bytesPerSector & (bytesPerSector - 1)) != 0 || sectorsPerCluster < 1 || mftCluster <= 0)
        {
            return string.Empty;
        }

        var clusterSize = bytesPerSector * sectorsPerCluster;
        var recordSize = recordCode > 0 ? recordCode * clusterSize : 1L << -recordCode;
        if (recordSize is < 256 or > 64 * 1024)
        {
            return string.Empty;
        }

        // The first MFT records are always contiguous at its start, and number 3 is $Volume.
        var record = read(checked(offset + (mftCluster * clusterSize) + (NtfsMftRecordNumberOfVolume * recordSize)), (int)recordSize);
        if (record is null || !ApplyNtfsFixups(record))
        {
            return string.Empty;
        }

        var position = (int)BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        while (position + 8 <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
            if (type == NtfsEndOfAttributes)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 4));
            if (length < 16 || position + (long)length > record.Length)
            {
                break;
            }

            if (type == NtfsVolumeNameAttribute)
            {
                // The name is always resident.
                if (record[position + 8] != 0)
                {
                    return string.Empty;
                }

                var contentLength = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 0x10));
                var contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 0x14));
                return contentOffset + (long)contentLength <= length
                    ? Clean(Encoding.Unicode.GetString(record, position + contentOffset, (int)contentLength))
                    : string.Empty;
            }

            position += (int)length;
        }

        return string.Empty;
    }

    /// <summary>
    /// Puts back the last two bytes of every 512 byte block, which NTFS replaces by a sequence number
    /// to detect torn writes. Returns false for a record that does not look like one.
    /// </summary>
    private static bool ApplyNtfsFixups(byte[] record)
    {
        if (record.Length < 48 || !record.AsSpan(0, 4).SequenceEqual("FILE"u8))
        {
            return false;
        }

        var arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
        var arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
        if (arrayCount < 1 || arrayOffset + (arrayCount * 2) > record.Length || arrayCount - 1 > record.Length / NtfsFixupStride)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(arrayOffset));
        for (var i = 1; i < arrayCount; i++)
        {
            var end = (i * NtfsFixupStride) - 2;
            if (end + 2 > record.Length || BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(end)) != sequence)
            {
                return false;
            }

            record[end] = record[arrayOffset + (i * 2)];
            record[end + 1] = record[arrayOffset + (i * 2) + 1];
        }

        return true;
    }

    private static string ExFat(ReadOnlySpan<byte> boot, long offset, Func<long, int, byte[]?> read)
    {
        if (boot.Length < 0x78)
        {
            return string.Empty;
        }

        int bytesShift = boot[0x6C];
        int clusterShift = boot[0x6D];
        var heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot[0x58..]);
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot[0x60..]);
        if (bytesShift is < 9 or > 12 || clusterShift > 25 - bytesShift || rootCluster < 2)
        {
            return string.Empty;
        }

        var clusterSize = 1L << (bytesShift + clusterShift);
        var rootOffset = checked(offset + ((long)heapOffset << bytesShift) + ((rootCluster - 2) * clusterSize));

        // The label entry is among the first entries of the root directory; looking at its first cluster is enough.
        var directory = read(rootOffset, (int)Math.Min(clusterSize, 64 * 1024));
        if (directory is null)
        {
            return string.Empty;
        }

        for (var entry = 0; entry + 32 <= directory.Length; entry += 32)
        {
            var type = directory[entry];
            if (type == 0)
            {
                break;
            }

            if (type == ExFatVolumeLabelEntry)
            {
                int characters = directory[entry + 1];
                return characters <= ExFatMaxLabelCharacters ? Clean(Encoding.Unicode.GetString(directory, entry + 2, characters * 2)) : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string DecodeOem(ReadOnlySpan<byte> raw)
    {
        var ascii = true;
        foreach (var b in raw)
        {
            if (b >= 0x80)
            {
                ascii = false;
                break;
            }
        }

        if (!ascii)
        {
            try
            {
                var codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                var encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage);
                if (encoding != null)
                {
                    return encoding.GetString(raw);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Fall through to the lossy rendering below.
            }
        }

        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            chars[i] = raw[i] is >= 0x20 and < 0x7F ? (char)raw[i] : '?';
        }

        return new string(chars);
    }

    /// <summary>Drops control characters and surrounding blanks so a label is safe to put into a sentence.</summary>
    private static string Clean(string label)
    {
        var builder = new StringBuilder(label.Length);
        foreach (var c in label)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }
}
