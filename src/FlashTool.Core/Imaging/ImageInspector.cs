using System.Buffers.Binary;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Imaging;

/// <summary>
/// Recognizes disk images by their structures (MBR, GPT, ISO 9660, boot
/// sectors) rather than by file extension, so random files are rejected
/// before anything is written to a device.
/// </summary>
public static class ImageInspector
{
    /// <summary>Amount of leading data that is enough for the inspection.</summary>
    public const int HeadSize = 1024 * 1024;

    /// <summary>
    /// Inspects the beginning of an image.
    /// </summary>
    /// <param name="head">The first bytes of the image (ideally <see cref="HeadSize"/>).</param>
    /// <param name="randomRead">
    /// Optional random-access reader (byte offset, count) used for data beyond
    /// <paramref name="head"/>: logical partitions and file system probing.
    /// </param>
    public static ImageInfo Inspect(ReadOnlySpan<byte> head, Func<long, int, byte[]?>? randomRead = null)
    {
        if (head.Length < 512)
        {
            return ImageInfo.Unknown;
        }

        var headCopy = head.ToArray();
        byte[]? Read(long offset, int count)
        {
            if (offset >= 0 && offset + count <= headCopy.Length)
            {
                return headCopy.AsSpan((int)offset, count).ToArray();
            }

            return randomRead?.Invoke(offset, count);
        }

        var iso = TryReadIsoVolumeSize(head);

        // 1. GPT (checked first: a GPT disk also has a protective MBR).
        var gpt = GptPartitionTable.TryRead(Read);
        if (gpt != null)
        {
            var parts = ProbeFileSystems(gpt.GetPartitions(), Read);
            var used = parts.Count > 0 ? parts.Max(p => p.End) : (long?)null;
            return new ImageInfo(
                iso.HasValue ? ImageKind.HybridIso : ImageKind.GptDisk,
                PartitionTableType.Gpt,
                gpt.SectorSize,
                parts,
                Max(used, iso));
        }

        // 2. MBR.
        var mbr = MbrPartitionTable.TryParse(head[..512], lenient: iso.HasValue);
        if (mbr != null && !LooksLikeVolumeBootRecord(head))
        {
            var parts = ProbeFileSystems(
                mbr.GetPartitions(lba => Read(lba * MbrPartitionTable.SectorSize, MbrPartitionTable.SectorSize)),
                Read);
            var used = parts.Count > 0 ? parts.Max(p => p.End) : (long?)null;
            return new ImageInfo(
                iso.HasValue ? ImageKind.HybridIso : ImageKind.MbrDisk,
                PartitionTableType.Mbr,
                512,
                parts,
                Max(used, iso));
        }

        // 3. Pure ISO 9660.
        if (iso.HasValue)
        {
            return new ImageInfo(ImageKind.Iso9660, PartitionTableType.None, 2048, [], iso, FileSystemType.Iso9660);
        }

        // 4. A file system directly at sector 0.
        var fs = FileSystemProbe.Detect(head);
        if (fs != FileSystemType.Unknown)
        {
            return new ImageInfo(ImageKind.Superfloppy, PartitionTableType.None, 512, [], TryReadVolumeSize(head, fs), fs);
        }

        return ImageInfo.Unknown;
    }

    private static List<PartitionInfo> ProbeFileSystems(IReadOnlyList<PartitionInfo> partitions, Func<long, int, byte[]?> read)
    {
        var result = new List<PartitionInfo>(partitions.Count);
        foreach (var p in partitions)
        {
            if (p.IsExtendedContainer)
            {
                result.Add(p);
                continue;
            }

            var data = read(p.Offset, (int)Math.Min(FileSystemProbe.RequiredBytes, p.Length));
            result.Add(data == null ? p : p with { FileSystem = FileSystemProbe.Detect(data) });
        }

        return result;
    }

    /// <summary>
    /// A FAT/NTFS/exFAT boot sector also ends with 55 AA. Distinguish it from an MBR
    /// by the jump instruction and the OEM/file-system identifiers.
    /// </summary>
    private static bool LooksLikeVolumeBootRecord(ReadOnlySpan<byte> sector)
    {
        var jump = sector[0] == 0xEB || sector[0] == 0xE9;
        return jump && FileSystemProbe.Detect(sector) is FileSystemType.Fat12 or FileSystemType.Fat16 or FileSystemType.Fat32
            or FileSystemType.Ntfs or FileSystemType.ExFat;
    }

    private static long? TryReadIsoVolumeSize(ReadOnlySpan<byte> head)
    {
        const int pvdOffset = 0x8000;
        if (head.Length < pvdOffset + 2048 || head[pvdOffset] != 1 || !head.Slice(pvdOffset + 1, 5).SequenceEqual("CD001"u8))
        {
            return null;
        }

        var blocks = BinaryPrimitives.ReadUInt32LittleEndian(head[(pvdOffset + 80)..]);
        var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(head[(pvdOffset + 128)..]);
        return blocks > 0 && blockSize is 512 or 1024 or 2048 or 4096 ? (long)blocks * blockSize : null;
    }

    private static long? TryReadVolumeSize(ReadOnlySpan<byte> bootSector, FileSystemType fs)
    {
        switch (fs)
        {
            case FileSystemType.Fat12 or FileSystemType.Fat16 or FileSystemType.Fat32:
            {
                var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[11..]);
                long total = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[19..]);
                if (total == 0)
                {
                    total = BinaryPrimitives.ReadUInt32LittleEndian(bootSector[32..]);
                }

                return bytesPerSector > 0 && total > 0 ? total * bytesPerSector : null;
            }

            case FileSystemType.Ntfs:
            {
                var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[11..]);
                var total = BinaryPrimitives.ReadInt64LittleEndian(bootSector[40..]);

                // NTFS keeps a backup boot sector right after the last volume sector.
                return bytesPerSector > 0 && total > 0 ? (total + 1) * bytesPerSector : null;
            }

            case FileSystemType.ExFat:
            {
                var length = BinaryPrimitives.ReadInt64LittleEndian(bootSector[72..]);
                var shift = bootSector[108];
                return length > 0 && shift is >= 9 and <= 12 ? length << shift : null;
            }

            default:
                return null;
        }
    }

    private static long? Max(long? a, long? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => Math.Max(a.Value, b.Value),
    };
}
