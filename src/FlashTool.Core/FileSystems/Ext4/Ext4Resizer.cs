using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.FileSystems.Ext4;

/// <summary>Offline growth of ext2/ext3/ext4 (see <see cref="Ext4Grow"/>).</summary>
public sealed class Ext4Resizer : IFileSystemResizer
{
    public bool RequiresOfflineDevice => true;

    public bool CanResize(FileSystemType fileSystem) => fileSystem is FileSystemType.Ext2 or FileSystemType.Ext3 or FileSystemType.Ext4;

    public bool Resize(IBlockDevice? device, PartitionInfo partition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Ext4Grow.Grow(new PartitionAccess(device, partition.Offset, partition.Length), cancellationToken).Grown;
    }
}
