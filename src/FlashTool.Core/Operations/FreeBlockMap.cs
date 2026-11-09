using FlashTool.Core.FileSystems;
using FlashTool.Core.FileSystems.Ext4;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Operations;

/// <summary>What happened to the free space of one file system while creating an image.</summary>
public enum FreeSpaceStatus
{
    /// <summary>Free blocks were stored as zeros without being read.</summary>
    Skipped,

    /// <summary>Not cleanly unmounted or marked as having errors: copied in full.</summary>
    NotCleanlyUnmounted,

    /// <summary>The journal still has to be replayed, so the bitmaps may be out of date: copied in full.</summary>
    JournalNeedsRecovery,

    /// <summary>A layout this reader does not handle (bigalloc, an external journal device, ...): copied in full.</summary>
    UnsupportedFeature,

    /// <summary>Checksums do not match or block numbers make no sense: copied in full.</summary>
    InconsistentMetadata,

    /// <summary>The partition overlaps another one, so its free blocks may hold other data: copied in full.</summary>
    OverlapsOtherPartition,
}

/// <summary>Free space handling of one ext2/3/4 file system.</summary>
/// <param name="PartitionNumber">Partition number, or 0 for a file system without a partition table.</param>
/// <param name="SkippedBytes">Bytes stored as zeros without being read.</param>
public sealed record PartitionFreeSpace(int PartitionNumber, FileSystemType FileSystem, FreeSpaceStatus Status, long SkippedBytes);

/// <summary>
/// Device ranges that hold free file system blocks. Reading through the map
/// fills those ranges with zeros and reads only the rest from the device.
/// </summary>
internal sealed class FreeBlockMap
{
    /// <summary>
    /// Shorter free runs are read anyway: skipping them would split reads into
    /// many small requests, which costs more than it saves.
    /// </summary>
    public const long MinimumRun = 256 * 1024;

    private readonly long[] _starts;
    private readonly long[] _ends;

    /// <param name="ranges">Sector-aligned, non-overlapping device ranges [Start, End).</param>
    internal FreeBlockMap(IEnumerable<(long Start, long End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        _starts = sorted.Select(r => r.Start).ToArray();
        _ends = sorted.Select(r => r.End).ToArray();
        FreeBytes = sorted.Sum(r => r.End - r.Start);
    }

    public long FreeBytes { get; }

    /// <summary>
    /// Scans every ext2/3/4 file system that lies within the first <paramref name="length"/>
    /// bytes. Everything else (other file systems, gaps between partitions, partition
    /// tables, boot loaders) is read as usual.
    /// </summary>
    public static (FreeBlockMap Map, IReadOnlyList<PartitionFreeSpace> Report) Build(IBlockDevice device, ImageInfo image, long length, CancellationToken cancellationToken)
    {
        var volumes = new List<(int Number, FileSystemType FileSystem, long Offset, long Length, bool Overlaps)>();
        if (image.Kind is ImageKind.MbrDisk or ImageKind.GptDisk)
        {
            var partitions = image.Partitions.Where(p => !p.IsExtendedContainer).ToList();
            foreach (var p in partitions.Where(p => IsExt(p.FileSystem)))
            {
                var overlaps = partitions.Any(o => o != p && o.Offset < p.End && p.Offset < o.End);
                volumes.Add((p.Number, p.FileSystem, p.Offset, p.Length, overlaps));
            }
        }
        else if (image.Kind == ImageKind.Superfloppy && IsExt(image.RootFileSystem))
        {
            volumes.Add((0, image.RootFileSystem, 0, device.Length, false));
        }

        // Hybrid ISOs overlap on purpose and are not touched at all.
        var ranges = new List<(long, long)>();
        var report = new List<PartitionFreeSpace>();
        foreach (var volume in volumes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(volume.Offset + volume.Length, device.Length);
            if (volume.Offset >= length || end <= volume.Offset)
            {
                continue;
            }

            if (volume.Overlaps)
            {
                report.Add(new PartitionFreeSpace(volume.Number, volume.FileSystem, FreeSpaceStatus.OverlapsOtherPartition, 0));
                continue;
            }

            var access = new PartitionAccess(device, volume.Offset, end - volume.Offset);
            var (status, runs) = Ext4FreeSpace.Scan(access, MinimumRun, cancellationToken);
            long skipped = 0;
            foreach (var (offset, runLength) in runs)
            {
                // Whole sectors only, and nothing past what is going to be read.
                var start = StreamExtensions.AlignUp(volume.Offset + offset, device.SectorSize);
                var stop = Math.Min(StreamExtensions.AlignDown(volume.Offset + offset + runLength, device.SectorSize), length);
                if (stop > start)
                {
                    ranges.Add((start, stop));
                    skipped += stop - start;
                }
            }

            report.Add(new PartitionFreeSpace(volume.Number, volume.FileSystem, status, skipped));
        }

        return (new FreeBlockMap(ranges), report);
    }

    /// <summary>Like <see cref="IBlockDevice.Read"/>, but free ranges are zero-filled instead of read.</summary>
    public void Read(IBlockDevice device, long offset, Span<byte> buffer)
    {
        var end = offset + buffer.Length;
        var index = FirstEndingAfter(offset);
        var position = offset;
        while (position < end)
        {
            if (index < _starts.Length && _starts[index] <= position)
            {
                var stop = Math.Min(_ends[index], end);
                buffer[(int)(position - offset)..(int)(stop - offset)].Clear();
                position = stop;
                index++;
                continue;
            }

            var next = index < _starts.Length ? Math.Min(_starts[index], end) : end;
            device.Read(position, buffer[(int)(position - offset)..(int)(next - offset)]);
            position = next;
        }
    }

    private static bool IsExt(FileSystemType fs) => fs is FileSystemType.Ext2 or FileSystemType.Ext3 or FileSystemType.Ext4;

    private int FirstEndingAfter(long offset)
    {
        int lo = 0, hi = _ends.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_ends[mid] <= offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
