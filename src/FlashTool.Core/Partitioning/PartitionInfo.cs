namespace FlashTool.Core.Partitioning;

public enum PartitionTableType
{
    None,
    Mbr,
    Gpt,
}

/// <summary>Detected file system inside a partition (best effort, by magic numbers).</summary>
public enum FileSystemType
{
    Unknown,
    Fat12,
    Fat16,
    Fat32,
    ExFat,
    Ntfs,
    Ext2,
    Ext3,
    Ext4,
    Btrfs,
    Xfs,
    F2fs,
    Iso9660,
    Swap,
    Squashfs,
}

/// <summary>A partition described in a partition table.</summary>
/// <param name="Number">1-based partition number as an OS would show it.</param>
/// <param name="Offset">Start offset in bytes.</param>
/// <param name="Length">Length in bytes.</param>
/// <param name="TypeDescription">Human readable type ("FAT32 LBA", "Linux filesystem", ...).</param>
/// <param name="Name">GPT partition name (empty for MBR).</param>
public sealed record PartitionInfo(
    int Number,
    long Offset,
    long Length,
    string TypeDescription,
    string Name = "",
    bool IsExtendedContainer = false,
    bool IsLogical = false)
{
    public long End => Offset + Length;

    /// <summary>Raw MBR type byte (0 for GPT).</summary>
    public byte MbrType { get; init; }

    /// <summary>GPT partition type GUID (empty for MBR).</summary>
    public Guid GptType { get; init; }

    public FileSystemType FileSystem { get; init; }
}
