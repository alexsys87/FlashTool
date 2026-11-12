using FlashTool.Core.FileSystems.ExFat;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.FileSystems.Fat;

/// <summary>
/// Offline growth of FAT32 (see <see cref="Fat32Grow"/>) and exFAT (see <see cref="ExFatGrow"/>).
/// Unlike growing ext4, this moves data and rewrites the allocation tables in place: an interruption
/// in the last step leaves the volume damaged, so callers should warn before using it.
/// FAT12/FAT16 are recognized only to say why they stay as they are.
/// </summary>
public sealed class FatResizer : IFileSystemResizer
{
    public bool RequiresOfflineDevice => true;

    public bool CanResize(FileSystemType fileSystem)
        => fileSystem is FileSystemType.Fat12 or FileSystemType.Fat16 or FileSystemType.Fat32 or FileSystemType.ExFat;

    /// <summary>File systems whose growth carries the risk described above.</summary>
    public static bool IsRisky(FileSystemType fileSystem) => fileSystem is FileSystemType.Fat32 or FileSystemType.ExFat;

    public bool Resize(IBlockDevice? device, PartitionInfo partition, CancellationToken cancellationToken)
        => ResizeWithOutcome(device, partition, cancellationToken).Resized;

    public FileSystemResizeOutcome ResizeWithOutcome(IBlockDevice? device, PartitionInfo partition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(partition);
        if (partition.FileSystem is FileSystemType.Fat12 or FileSystemType.Fat16)
        {
            return new FileSystemResizeOutcome(false, FileSystemResizeSkipReason.Fat12Or16);
        }

        var volume = new PartitionAccess(device, partition.Offset, partition.Length);
        var result = partition.FileSystem == FileSystemType.ExFat
            ? ExFatGrow.Grow(volume, cancellationToken)
            : Fat32Grow.Grow(volume, cancellationToken);
        var reason = result.SkipReason switch
        {
            FatGrowSkipReason.None => result.Grown ? FileSystemResizeSkipReason.None : FileSystemResizeSkipReason.AlreadyFillsPartition,
            FatGrowSkipReason.AlreadyFillsPartition => FileSystemResizeSkipReason.AlreadyFillsPartition,
            FatGrowSkipReason.Fat12Or16 => FileSystemResizeSkipReason.Fat12Or16,
            FatGrowSkipReason.VolumeDirty => FileSystemResizeSkipReason.FatVolumeDirty,
            FatGrowSkipReason.UnsupportedLayout => FileSystemResizeSkipReason.FatUnsupportedLayout,
            FatGrowSkipReason.InconsistentMetadata => FileSystemResizeSkipReason.FatInconsistent,
            FatGrowSkipReason.NoFreeSpace => FileSystemResizeSkipReason.FatNoFreeSpace,
            FatGrowSkipReason.ClusterLimit => FileSystemResizeSkipReason.FatClusterLimit,
            _ => FileSystemResizeSkipReason.Unknown,
        };
        return new FileSystemResizeOutcome(result.Grown, reason)
        {
            Limited = result.Grown && result.Limited,
            NewSize = result.Grown ? result.NewSectors * result.BytesPerSector : null,
        };
    }
}
