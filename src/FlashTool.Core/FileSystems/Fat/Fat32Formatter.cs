using System.Buffers.Binary;
using System.Text;
using FlashTool.Core.IO;

namespace FlashTool.Core.FileSystems.Fat;

/// <summary>Geometry of a FAT32 volume, in sectors of <see cref="BytesPerSector"/> bytes.</summary>
public sealed record Fat32Layout(
    long TotalSectors,
    int BytesPerSector,
    int SectorsPerCluster,
    int ReservedSectors,
    long FatSectors,
    long ClusterCount)
{
    public const int NumberOfFats = 2;

    public int ClusterSize => BytesPerSector * SectorsPerCluster;

    /// <summary>First sector of the cluster heap (cluster 2 = root directory).</summary>
    public long DataStartSector => ReservedSectors + (NumberOfFats * FatSectors);
}

/// <summary>
/// Creates an empty FAT32 file system (Microsoft FAT specification, "fatgen103"):
/// boot sector + FSInfo with backups at sectors 6/7, two FATs and an empty root
/// directory. The cluster heap is aligned to the requested boundary, which is
/// what flash media (SD cards) expect for good write performance.
/// </summary>
public static class Fat32Formatter
{
    /// <summary>Fewer clusters than this make a volume FAT12/16 by definition.</summary>
    public const long MinClusters = 65525;

    public const long MaxClusters = 0x0FFFFFF5;

    public const int MaxLabelLength = 11;

    private const int FsInfoSector = 1;
    private const int BackupBootSector = 6;

    /// <summary>
    /// Chooses the cluster size (Windows defaults, smaller when needed to stay
    /// above the FAT32 minimum) and computes the layout.
    /// </summary>
    /// <returns>Null when FAT32 cannot cover a volume of this size.</returns>
    public static Fat32Layout? TryPlan(long volumeBytes, int bytesPerSector, int alignmentBytes)
    {
        var totalSectors = volumeBytes / bytesPerSector;
        if (totalSectors > uint.MaxValue || bytesPerSector < 512)
        {
            return null;
        }

        for (var clusterSize = DefaultClusterSize(volumeBytes); clusterSize >= bytesPerSector; clusterSize /= 2)
        {
            var layout = Compute(totalSectors, bytesPerSector, clusterSize / bytesPerSector, Math.Max(1, alignmentBytes / bytesPerSector));
            if (layout == null || layout.ClusterCount < MinClusters)
            {
                // Too few clusters: smaller ones give more.
                continue;
            }

            return layout.ClusterCount <= MaxClusters ? layout : null;
        }

        return null;
    }

    /// <summary>Cluster size Windows picks for FAT32 volumes of this size.</summary>
    public static int DefaultClusterSize(long volumeBytes)
    {
        const long mib = 1024 * 1024;
        return volumeBytes switch
        {
            <= 64 * mib => 512,
            <= 128 * mib => 1024,
            <= 256 * mib => 2048,
            <= 8 * 1024 * mib => 4096,
            <= 16 * 1024 * mib => 8192,
            <= 32 * 1024 * mib => 16384,
            _ => 32768,
        };
    }

    private static Fat32Layout? Compute(long totalSectors, int bytesPerSector, int sectorsPerCluster, int alignSectors)
    {
        const int minReserved = 32;
        var entriesPerSector = bytesPerSector / 4;

        // Start from a FAT big enough for a heap without FATs; the real heap is smaller.
        var fatSectors = CeilDiv(((totalSectors - minReserved) / sectorsPerCluster) + 2, entriesPerSector);
        var dataStart = minReserved + (Fat32Layout.NumberOfFats * fatSectors);

        // The extra reserved sectors move the cluster heap onto the alignment boundary.
        var reserved = minReserved + (StreamExtensions.AlignUp(dataStart, alignSectors) - dataStart);
        if (reserved > ushort.MaxValue)
        {
            reserved = minReserved;
        }

        var clusters = (totalSectors - reserved - (Fat32Layout.NumberOfFats * fatSectors)) / sectorsPerCluster;
        return clusters <= 0 ? null : new Fat32Layout(totalSectors, bytesPerSector, sectorsPerCluster, (int)reserved, fatSectors, clusters);
    }

    /// <summary>Writes the file system into the partition at <paramref name="partitionOffset"/>.</summary>
    public static void Format(IBlockDevice device, long partitionOffset, Fat32Layout layout, string label, uint volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(layout);
        var bps = layout.BytesPerSector;
        if (bps != device.SectorSize || partitionOffset % bps != 0)
        {
            throw new ArgumentException("The layout does not match the device sector size.", nameof(layout));
        }

        // Reserved area, both FATs and the root directory cluster start out empty.
        var metadataEnd = (layout.DataStartSector * bps) + layout.ClusterSize;
        device.Zero(partitionOffset, metadataEnd, cancellationToken);

        var volumeLabel = EncodeLabel(label);
        var boot = BuildBootSector(layout, partitionOffset / bps, volumeLabel, volumeId);
        var fsInfo = BuildFsInfo(layout);
        var firstFatSector = new byte[bps];
        BinaryPrimitives.WriteUInt32LittleEndian(firstFatSector, 0x0FFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(firstFatSector.AsSpan(4), 0x0FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(firstFatSector.AsSpan(8), 0x0FFFFFFF); // root directory: one cluster

        for (var fat = 0; fat < Fat32Layout.NumberOfFats; fat++)
        {
            device.WriteBytes(partitionOffset + ((layout.ReservedSectors + (fat * layout.FatSectors)) * bps), firstFatSector);
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            var root = new byte[bps];
            volumeLabel.CopyTo(root, 0);
            root[11] = 0x08; // ATTR_VOLUME_ID
            device.WriteBytes(partitionOffset + (layout.DataStartSector * bps), root);
        }

        device.WriteBytes(partitionOffset + (BackupBootSector * bps), boot);
        device.WriteBytes(partitionOffset + ((BackupBootSector + FsInfoSector) * bps), fsInfo);
        device.WriteBytes(partitionOffset + (FsInfoSector * bps), fsInfo);

        // The boot sector goes last: until then the volume is not recognized as FAT.
        device.WriteBytes(partitionOffset, boot);
    }

    /// <summary>Upper-cases and pads a label to the 11 bytes of a FAT label; "NO NAME" when empty.</summary>
    public static byte[] EncodeLabel(string? label)
    {
        var text = SanitizeLabel(label);
        if (text.Length == 0)
        {
            text = "NO NAME";
        }

        return Encoding.ASCII.GetBytes(text.PadRight(MaxLabelLength));
    }

    /// <summary>Keeps the characters a FAT label may contain (ASCII, upper case).</summary>
    public static string SanitizeLabel(string? label)
    {
        var builder = new StringBuilder();
        foreach (var c in (label ?? string.Empty).Trim().ToUpperInvariant())
        {
            if (builder.Length == MaxLabelLength)
            {
                break;
            }

            builder.Append(c is >= ' ' and < (char)0x7F && "\"*+,./:;<=>?[\\]|".IndexOf(c, StringComparison.Ordinal) < 0 ? c : '_');
        }

        return builder.ToString().TrimEnd();
    }

    private static byte[] BuildBootSector(Fat32Layout layout, long hiddenSectors, byte[] label, uint volumeId)
    {
        var sector = new byte[layout.BytesPerSector];
        var s = sector.AsSpan();
        s[0] = 0xEB;
        s[1] = 0x58;
        s[2] = 0x90;
        "MSWIN4.1"u8.CopyTo(s[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[11..], (ushort)layout.BytesPerSector);
        s[13] = (byte)layout.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)layout.ReservedSectors);
        s[16] = Fat32Layout.NumberOfFats;
        s[21] = 0xF8; // fixed disk
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], 63); // sectors per track
        BinaryPrimitives.WriteUInt16LittleEndian(s[26..], 255); // heads
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)Math.Min(hiddenSectors, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], (uint)layout.TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)layout.FatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 2); // root directory cluster
        BinaryPrimitives.WriteUInt16LittleEndian(s[48..], FsInfoSector);
        BinaryPrimitives.WriteUInt16LittleEndian(s[50..], BackupBootSector);
        s[64] = 0x80; // drive number
        s[66] = 0x29; // extended boot signature
        BinaryPrimitives.WriteUInt32LittleEndian(s[67..], volumeId);
        label.CopyTo(s[71..]);
        "FAT32   "u8.CopyTo(s[82..]);

        // Not bootable: "cli; hlt; jmp $-1".
        s[90] = 0xFA;
        s[91] = 0xF4;
        s[92] = 0xEB;
        s[93] = 0xFD;
        s[510] = 0x55;
        s[511] = 0xAA;
        return sector;
    }

    private static byte[] BuildFsInfo(Fat32Layout layout)
    {
        var sector = new byte[layout.BytesPerSector];
        var s = sector.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(s[484..], 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(s[488..], (uint)(layout.ClusterCount - 1)); // free clusters
        BinaryPrimitives.WriteUInt32LittleEndian(s[492..], 3); // next free cluster hint
        BinaryPrimitives.WriteUInt32LittleEndian(s[508..], 0xAA550000);
        return sector;
    }

    private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;
}
