using FlashTool.Core.IO;

namespace FlashTool.Core.Partitioning;

/// <summary>
/// Extension point for growing a file system after its partition was enlarged.
/// </summary>
public interface IFileSystemResizer
{
    /// <summary>
    /// True: works on the raw device while it is still exclusively open (e.g. ext4).
    /// False: runs after the device is released, when the OS has mounted the volume (e.g. NTFS).
    /// </summary>
    bool RequiresOfflineDevice { get; }

    bool CanResize(FileSystemType fileSystem);

    /// <summary>Grows the file system to fill <paramref name="partition"/>; returns false when nothing was changed.</summary>
    /// <param name="device">The open device for offline resizers, null otherwise.</param>
    bool Resize(IBlockDevice? device, PartitionInfo partition, CancellationToken cancellationToken);
}
