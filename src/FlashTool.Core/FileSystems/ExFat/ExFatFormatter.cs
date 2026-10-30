using System.Buffers.Binary;
using System.Text;
using FlashTool.Core.IO;

namespace FlashTool.Core.FileSystems.ExFat;

/// <summary>Geometry of an exFAT volume, in sectors of <see cref="BytesPerSector"/> bytes.</summary>
public sealed record ExFatLayout(
    long TotalSectors,
    int BytesPerSector,
    int SectorsPerCluster,
    long FatOffset,
    long FatLength,
    long ClusterHeapOffset,
    long ClusterCount)
{
    public int ClusterSize => BytesPerSector * SectorsPerCluster;
}

/// <summary>
/// Creates an empty exFAT file system (Microsoft exFAT specification):
/// main and backup boot regions, one FAT, and a root directory holding the
/// allocation bitmap, the up-case table and the optional volume label.
/// </summary>
public static class ExFatFormatter
{
    public const long MaxClusters = 0xFFFFFFF5;

    public const int MaxLabelLength = 11;

    private const int BootRegionSectors = 12;
    private const int FirstCluster = 2;
    private const int DirectoryEntrySize = 32;

    private static readonly Lazy<byte[]> UpcaseTable = new(BuildUpcaseTable);

    /// <summary>Computes the layout with the cluster size Windows would pick.</summary>
    /// <returns>Null when exFAT cannot cover a volume of this size.</returns>
    public static ExFatLayout? TryPlan(long volumeBytes, int bytesPerSector, int alignmentBytes)
    {
        if (bytesPerSector is < 512 or > 4096 || volumeBytes < 1024 * 1024)
        {
            return null;
        }

        var totalSectors = volumeBytes / bytesPerSector;
        var sectorsPerCluster = Math.Max(1, DefaultClusterSize(volumeBytes) / bytesPerSector);
        var alignSectors = Math.Max(1, alignmentBytes / bytesPerSector);

        // FAT halfway to the first boundary and cluster heap on it, like the SD card formatter does.
        var fatOffset = Math.Max(BootRegionSectors * 2, alignSectors / 2);
        var fatLength = CeilDiv((((totalSectors - fatOffset) / sectorsPerCluster) + 2) * 4, bytesPerSector);
        var heapOffset = StreamExtensions.AlignUp(fatOffset + fatLength, alignSectors);
        if (heapOffset >= totalSectors)
        {
            // Small volume: give up the alignment rather than the file system.
            fatOffset = BootRegionSectors * 2;
            heapOffset = fatOffset + fatLength;
        }

        var clusters = (totalSectors - heapOffset) / sectorsPerCluster;
        if (clusters <= 0 || clusters > MaxClusters || heapOffset > uint.MaxValue)
        {
            return null;
        }

        var layout = new ExFatLayout(totalSectors, bytesPerSector, sectorsPerCluster, fatOffset, fatLength, heapOffset, clusters);
        return SystemClusters(layout).Total <= clusters ? layout : null;
    }

    /// <summary>Cluster size Windows picks for exFAT volumes of this size.</summary>
    public static int DefaultClusterSize(long volumeBytes)
    {
        const long mib = 1024 * 1024;
        return volumeBytes switch
        {
            <= 256 * mib => 4096,
            <= 32 * 1024 * mib => 32 * 1024,
            _ => 128 * 1024,
        };
    }

    /// <summary>Writes the file system into the partition at <paramref name="partitionOffset"/>.</summary>
    public static void Format(IBlockDevice device, long partitionOffset, ExFatLayout layout, string label, uint volumeSerial, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(layout);
        var bps = layout.BytesPerSector;
        if (bps != device.SectorSize || partitionOffset % bps != 0)
        {
            throw new ArgumentException("The layout does not match the device sector size.", nameof(layout));
        }

        var system = SystemClusters(layout);
        var clusterSize = layout.ClusterSize;
        long ClusterOffset(long cluster) => partitionOffset + (layout.ClusterHeapOffset * bps) + ((cluster - FirstCluster) * clusterSize);

        // Boot regions and the FAT start out empty, as do the clusters used below.
        device.Zero(partitionOffset, layout.ClusterHeapOffset * bps, cancellationToken);
        device.Zero(ClusterOffset(FirstCluster), system.Total * clusterSize, cancellationToken);

        // FAT: media descriptor, then one chain per system structure.
        var fat = new byte[StreamExtensions.AlignUp((FirstCluster + system.Total) * 4, bps)];
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(4), 0xFFFFFFFF);
        WriteChain(fat, SystemLayout.BitmapCluster, system.BitmapClusters);
        WriteChain(fat, system.UpcaseCluster, system.UpcaseClusters);
        WriteChain(fat, system.RootCluster, 1);
        device.WriteBytes(partitionOffset + (layout.FatOffset * bps), fat);

        // Allocation bitmap: the system clusters are the only used ones.
        var bitmap = new byte[StreamExtensions.AlignUp(system.BitmapBytes, bps)];
        for (var i = 0; i < system.Total; i++)
        {
            bitmap[i / 8] |= (byte)(1 << (i % 8));
        }

        device.WriteBytes(ClusterOffset(SystemLayout.BitmapCluster), bitmap);

        var upcase = UpcaseTable.Value;
        var upcaseData = new byte[StreamExtensions.AlignUp(upcase.Length, bps)];
        upcase.CopyTo(upcaseData, 0);
        device.WriteBytes(ClusterOffset(system.UpcaseCluster), upcaseData);

        var root = new byte[clusterSize];
        var entry = 0;
        var labelChars = SanitizeLabel(label);
        if (labelChars.Length > 0)
        {
            var e = root.AsSpan(entry++ * DirectoryEntrySize, DirectoryEntrySize);
            e[0] = 0x83;
            e[1] = (byte)labelChars.Length;
            Encoding.Unicode.GetBytes(labelChars).CopyTo(e[2..]);
        }

        var bitmapEntry = root.AsSpan(entry++ * DirectoryEntrySize, DirectoryEntrySize);
        bitmapEntry[0] = 0x81;
        BinaryPrimitives.WriteUInt32LittleEndian(bitmapEntry[20..], (uint)SystemLayout.BitmapCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(bitmapEntry[24..], (ulong)system.BitmapBytes);

        var upcaseEntry = root.AsSpan(entry * DirectoryEntrySize, DirectoryEntrySize);
        upcaseEntry[0] = 0x82;
        BinaryPrimitives.WriteUInt32LittleEndian(upcaseEntry[4..], Checksum(upcase));
        BinaryPrimitives.WriteUInt32LittleEndian(upcaseEntry[20..], (uint)system.UpcaseCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(upcaseEntry[24..], (ulong)upcase.Length);
        device.WriteBytes(ClusterOffset(system.RootCluster), root);

        // Backup boot region first; the main one last makes the volume recognizable.
        var bootRegion = BuildBootRegion(layout, partitionOffset / bps, system, volumeSerial);
        device.WriteBytes(partitionOffset + (BootRegionSectors * bps), bootRegion);
        device.WriteBytes(partitionOffset, bootRegion);
    }

    /// <summary>Keeps the characters an exFAT label may contain (at most 11 UTF-16 units).</summary>
    public static string SanitizeLabel(string? label)
    {
        var builder = new StringBuilder();
        foreach (var c in (label ?? string.Empty).Trim())
        {
            if (builder.Length == MaxLabelLength)
            {
                break;
            }

            builder.Append(c < ' ' || "\"*/:<>?\\|".Contains(c, StringComparison.Ordinal) ? '_' : c);
        }

        // Never end with half of a surrogate pair.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>The exFAT checksum (rotate right by one, add the byte).</summary>
    internal static uint Checksum(ReadOnlySpan<byte> data, uint checksum = 0)
    {
        foreach (var b in data)
        {
            checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + b;
        }

        return checksum;
    }

    /// <summary>Clusters taken by the allocation bitmap, the up-case table and the root directory, in this order.</summary>
    private sealed record SystemLayout(long BitmapBytes, long BitmapClusters, long UpcaseClusters)
    {
        public const long BitmapCluster = FirstCluster;

        public long UpcaseCluster => BitmapCluster + BitmapClusters;

        public long RootCluster => UpcaseCluster + UpcaseClusters;

        public long Total => BitmapClusters + UpcaseClusters + 1;
    }

    private static SystemLayout SystemClusters(ExFatLayout layout)
    {
        var bitmapBytes = CeilDiv(layout.ClusterCount, 8);
        return new SystemLayout(
            bitmapBytes,
            CeilDiv(bitmapBytes, layout.ClusterSize),
            CeilDiv(UpcaseTable.Value.Length, layout.ClusterSize));
    }

    private static void WriteChain(byte[] fat, long first, long count)
    {
        for (var cluster = first; cluster < first + count; cluster++)
        {
            var next = cluster == first + count - 1 ? 0xFFFFFFFF : (uint)(cluster + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan((int)(cluster * 4)), next);
        }
    }

    private static byte[] BuildBootRegion(ExFatLayout layout, long partitionOffsetSectors, SystemLayout system, uint volumeSerial)
    {
        var bps = layout.BytesPerSector;
        var region = new byte[BootRegionSectors * bps];
        var s = region.AsSpan(0, bps);
        s[0] = 0xEB;
        s[1] = 0x76;
        s[2] = 0x90;
        "EXFAT   "u8.CopyTo(s[3..]);
        BinaryPrimitives.WriteUInt64LittleEndian(s[64..], (ulong)partitionOffsetSectors);
        BinaryPrimitives.WriteUInt64LittleEndian(s[72..], (ulong)layout.TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], (uint)layout.FatOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], (uint)layout.FatLength);
        BinaryPrimitives.WriteUInt32LittleEndian(s[88..], (uint)layout.ClusterHeapOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[92..], (uint)layout.ClusterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(s[96..], (uint)system.RootCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(s[100..], volumeSerial);
        BinaryPrimitives.WriteUInt16LittleEndian(s[104..], 0x0100); // revision 1.00
        s[108] = (byte)Log2(bps);
        s[109] = (byte)Log2(layout.SectorsPerCluster);
        s[110] = 1; // number of FATs
        s[111] = 0x80; // drive select
        s[112] = (byte)(system.Total * 100 / layout.ClusterCount); // percent in use
        s[120..510].Fill(0xF4); // boot code: hlt
        s[510] = 0x55;
        s[511] = 0xAA;

        // Extended boot sectors 1-8 carry only their signature.
        for (var i = 1; i <= 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(((i + 1) * bps) - 4), 0xAA550000);
        }

        // Sector 11: checksum of sectors 0-10, without VolumeFlags and PercentInUse.
        uint checksum = 0;
        for (var i = 0; i < 11 * bps; i++)
        {
            if (i is 106 or 107 or 112)
            {
                continue;
            }

            checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + region[i];
        }

        for (var i = 11 * bps; i < 12 * bps; i += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(i), checksum);
        }

        return region;
    }

    /// <summary>
    /// Up-case table in the compressed form of the specification: identity
    /// runs are stored as 0xFFFF followed by the run length.
    /// </summary>
    private static byte[] BuildUpcaseTable()
    {
        var table = new List<ushort>();
        var c = 0;
        while (c <= 0xFFFF)
        {
            var run = 0;
            while (c + run <= 0xFFFF && Upcase(c + run) == c + run)
            {
                run++;
            }

            // A literal 0xFFFF would read as a run marker, so the last run is always compressed.
            if (run > 1 || (run == 1 && c == 0xFFFF))
            {
                table.Add(0xFFFF);
                table.Add((ushort)run);
                c += run;
            }
            else
            {
                table.Add(Upcase(c));
                c++;
            }
        }

        var bytes = new byte[table.Count * 2];
        for (var i = 0; i < table.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), table[i]);
        }

        return bytes;
    }

    private static ushort Upcase(int c) => c is >= 0xD800 and <= 0xDFFF ? (ushort)c : char.ToUpperInvariant((char)c);

    private static int Log2(int value) => System.Numerics.BitOperations.Log2((uint)value);

    private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;
}
