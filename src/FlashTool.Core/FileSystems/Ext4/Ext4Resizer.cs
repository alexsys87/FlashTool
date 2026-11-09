using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.FileSystems.Ext4;

/// <summary>Offline growth of ext2/ext3/ext4 (see <see cref="Ext4Grow"/>).</summary>
public sealed class Ext4Resizer : IFileSystemResizer
{
    public bool RequiresOfflineDevice => true;

    public bool CanResize(FileSystemType fileSystem) => fileSystem is FileSystemType.Ext2 or FileSystemType.Ext3 or FileSystemType.Ext4;

    public bool Resize(IBlockDevice? device, PartitionInfo partition, CancellationToken cancellationToken)
        => ResizeWithOutcome(device, partition, cancellationToken).Resized;

    public FileSystemResizeOutcome ResizeWithOutcome(IBlockDevice? device, PartitionInfo partition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var result = Ext4Grow.Grow(new PartitionAccess(device, partition.Offset, partition.Length), cancellationToken);
        var reason = result.SkipReason switch
        {
            Ext4GrowSkipReason.None => result.Grown ? FileSystemResizeSkipReason.None : FileSystemResizeSkipReason.AlreadyFillsPartition,
            Ext4GrowSkipReason.AlreadyFillsPartition => FileSystemResizeSkipReason.AlreadyFillsPartition,
            Ext4GrowSkipReason.NotCleanlyUnmounted => FileSystemResizeSkipReason.NotCleanlyUnmounted,
            Ext4GrowSkipReason.HasErrors => FileSystemResizeSkipReason.HasErrors,
            Ext4GrowSkipReason.JournalNeedsRecovery => FileSystemResizeSkipReason.JournalNotReplayed,
            Ext4GrowSkipReason.OrphansPending => FileSystemResizeSkipReason.OrphansPending,
            Ext4GrowSkipReason.UnsupportedFeature => FileSystemResizeSkipReason.UnsupportedFeature,
            Ext4GrowSkipReason.NoRoomForDescriptors => FileSystemResizeSkipReason.NoRoomForDescriptors,
            Ext4GrowSkipReason.InconsistentMetadata => FileSystemResizeSkipReason.InconsistentMetadata,
            _ => FileSystemResizeSkipReason.Unknown,
        };
        return new FileSystemResizeOutcome(result.Grown, reason, result.JournalReplayed);
    }
}
