using FlashTool.Core.Imaging;
using FlashTool.Core.IO;

namespace FlashTool.Core.Bmap;

/// <summary>
/// What gets written to the device when an image comes with a .bmap. bmaptool writes only the
/// mapped blocks; a hole leaves whatever the device held before. Inside a file system that is
/// harmless, its free blocks are never read. Outside file systems it is not: a boot loader's
/// environment in the raw area before the first partition, or an old file system in a partition
/// that the image leaves empty, would survive and be picked up. So in addition to the mapped data
/// everything outside partitions and the first and last mebibyte of every partition (where file
/// system and RAID signatures live) are written from the image, holes included.
/// </summary>
internal static class BmapWritePlan
{
    public const long GuardBytes = 1024 * 1024;

    /// <summary>Sorted, merged, sector-aligned ranges [Start, End) to write.</summary>
    public static List<(long Start, long End)> Build(BmapFile bmap, ImageInfo image, int sectorSize)
    {
        var size = bmap.ImageSize;
        var ranges = bmap.MappedByteRanges().ToList();
        ranges.AddRange(Guards(image, size));

        var limit = StreamExtensions.AlignUp(size, sectorSize);
        return Merge(ranges.Select(r => (StreamExtensions.AlignDown(r.Start, sectorSize), Math.Min(StreamExtensions.AlignUp(r.End, sectorSize), limit))));
    }

    private static IEnumerable<(long Start, long End)> Guards(ImageInfo image, long size)
    {
        switch (image.Kind)
        {
            case ImageKind.MbrDisk or ImageKind.GptDisk:
            {
                var partitions = image.Partitions
                    .Where(p => !p.IsExtendedContainer && p.Offset < size && p.Length > 0)
                    .Select(p => (Start: p.Offset, End: Math.Min(p.End, size)))
                    .ToList();

                // Everything that no partition covers: partition tables, boot loader areas, gaps, the tail.
                var position = 0L;
                foreach (var (start, end) in Merge(partitions))
                {
                    if (start > position)
                    {
                        yield return (position, start);
                    }

                    position = Math.Max(position, end);
                }

                if (position < size)
                {
                    yield return (position, size);
                }

                foreach (var (start, end) in partitions)
                {
                    yield return (start, Math.Min(end, start + GuardBytes));
                    yield return (Math.Max(start, end - GuardBytes), end);
                }

                break;
            }

            case ImageKind.Superfloppy:
                yield return (0, Math.Min(size, GuardBytes));
                yield return (Math.Max(0, size - GuardBytes), size);
                break;

            default:
                // ISO images (partitions overlap on purpose) and unrecognized data: everything is written.
                yield return (0, size);
                break;
        }
    }

    internal static List<(long Start, long End)> Merge(IEnumerable<(long Start, long End)> ranges)
    {
        var merged = new List<(long Start, long End)>();
        foreach (var (start, end) in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        return merged;
    }
}
