using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Imaging;

public enum ImageKind
{
    /// <summary>Content was not recognized as a disk image.</summary>
    Unknown,

    /// <summary>Disk with an MBR partition table.</summary>
    MbrDisk,

    /// <summary>Disk with a GUID partition table.</summary>
    GptDisk,

    /// <summary>ISO 9660 image that also carries a partition table (isohybrid), bootable from USB.</summary>
    HybridIso,

    /// <summary>Pure ISO 9660 image without a partition table (usually not bootable when written raw).</summary>
    Iso9660,

    /// <summary>A file system without a partition table ("superfloppy").</summary>
    Superfloppy,
}

/// <summary>
/// Result of inspecting the beginning of an image or a device.
/// </summary>
public sealed record ImageInfo(
    ImageKind Kind,
    PartitionTableType TableType,
    int SectorSize,
    IReadOnlyList<PartitionInfo> Partitions,
    long? UsedLength,
    FileSystemType RootFileSystem = FileSystemType.Unknown)
{
    public static ImageInfo Unknown { get; } = new(ImageKind.Unknown, PartitionTableType.None, 512, [], null);

    /// <summary>True when the data looks like something that can be written to a disk.</summary>
    public bool IsDiskImage => Kind != ImageKind.Unknown;

    /// <summary>True for a pure ISO that most likely won't boot when written byte-by-byte.</summary>
    public bool IsNonHybridIso => Kind == ImageKind.Iso9660;

    /// <summary>The partition that ends last (the one that can be grown).</summary>
    public PartitionInfo? LastPartition => Partitions.Where(p => !p.IsExtendedContainer).MaxBy(p => p.End);
}
