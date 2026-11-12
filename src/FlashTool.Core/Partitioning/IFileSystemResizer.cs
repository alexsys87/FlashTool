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

    /// <summary>Like <see cref="Resize"/>, and says why nothing was changed when that is known.</summary>
    FileSystemResizeOutcome ResizeWithOutcome(IBlockDevice? device, PartitionInfo partition, CancellationToken cancellationToken)
        => new(Resize(device, partition, cancellationToken));
}

/// <summary>Why a file system was not grown to fill its enlarged partition.</summary>
public enum FileSystemResizeSkipReason
{
    None,

    /// <summary>Nothing to do, or no reason was given.</summary>
    Unknown,

    AlreadyFillsPartition,

    /// <summary>Not cleanly unmounted (and no journal that could be replayed): needs e2fsck.</summary>
    NotCleanlyUnmounted,

    /// <summary>The file system recorded errors: needs e2fsck.</summary>
    HasErrors,

    /// <summary>The journal needs recovery and could not be replayed here (unsupported or damaged).</summary>
    JournalNotReplayed,

    /// <summary>Deletions of open files are pending (orphan file): mount it once or run e2fsck.</summary>
    OrphansPending,

    /// <summary>A layout that growing would need to rearrange (meta_bg, bigalloc, sparse_super2...).</summary>
    UnsupportedFeature,

    /// <summary>The descriptor table has no room left for more block groups.</summary>
    NoRoomForDescriptors,

    InconsistentMetadata,

    /// <summary>FAT12/FAT16: growing would mean converting to FAT32.</summary>
    Fat12Or16,

    /// <summary>FAT32/exFAT marked as not cleanly removed or with errors: needs chkdsk.</summary>
    FatVolumeDirty,

    /// <summary>A FAT32/exFAT layout that is not rearranged here (inactive FAT mirror, TexFAT, vendor entries).</summary>
    FatUnsupportedLayout,

    /// <summary>FAT32/exFAT structures that do not check out: needs chkdsk.</summary>
    FatInconsistent,

    /// <summary>No free clusters to move the data that is in the way of the larger FAT.</summary>
    FatNoFreeSpace,

    /// <summary>The volume already has as many clusters as its cluster size allows.</summary>
    FatClusterLimit,
}

/// <param name="JournalReplayed">The journal had to be replayed first (a file system taken from a running system).</param>
public sealed record FileSystemResizeOutcome(bool Resized, FileSystemResizeSkipReason SkipReason = FileSystemResizeSkipReason.None, bool JournalReplayed = false)
{
    /// <summary>The file system grew, but its cluster size did not allow filling the whole partition.</summary>
    public bool Limited { get; init; }

    /// <summary>Size of the file system after growing, when the resizer knows it.</summary>
    public long? NewSize { get; init; }
}
