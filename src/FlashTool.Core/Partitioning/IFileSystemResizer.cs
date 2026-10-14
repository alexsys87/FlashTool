using FlashTool.Core.IO;

namespace FlashTool.Core.Partitioning;

/// <summary>
/// Extension point for growing a file system after its partition was
/// enlarged (e.g. NTFS through the OS, ext4 through resize2fs).
/// </summary>
public interface IFileSystemResizer
{
    bool CanResize(FileSystemType fileSystem);

    /// <summary>Grows the file system to fill <paramref name="partition"/>.</summary>
    void Resize(IBlockDevice device, PartitionInfo partition, CancellationToken cancellationToken);
}
