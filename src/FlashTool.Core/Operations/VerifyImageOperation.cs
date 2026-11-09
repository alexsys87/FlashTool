using System.Diagnostics;
using System.Security.Cryptography;
using FlashTool.Core.Bmap;
using FlashTool.Core.Containers;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Operations;

public sealed record VerifyImageOptions
{
    /// <summary>At most this many difference ranges are kept (all are counted).</summary>
    public int MaxReportedRanges { get; init; } = 1000;

    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    public int PipelineDepth { get; init; } = 8;

    /// <summary>Retries of failed device reads/writes (bad sectors, timeouts).</summary>
    public IoRetryPolicy IoRetry { get; init; } = IoRetryPolicy.Default;

    /// <summary>
    /// Receives the differences found so far while the comparison is still running:
    /// right after the first mismatch, then at most every <see cref="DifferenceReportInterval"/>.
    /// </summary>
    public IProgress<VerifyDifferences>? DifferencesFound { get; init; }

    public TimeSpan DifferenceReportInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Block map of the image: only its mapped ranges are compared (and hashed), as bmaptool writes only
    /// those. The image itself is checked against the map too.
    /// </summary>
    public BmapFile? Bmap { get; init; }
}

/// <summary>Differences found up to <see cref="ComparedUpTo"/>; the last range may still grow.</summary>
public sealed record VerifyDifferences(
    long DifferentBytes,
    long RangeCount,
    IReadOnlyList<DifferenceRange> Ranges,
    long ComparedUpTo);

/// <summary>Where a differing range lies on the disk.</summary>
public enum DifferenceArea
{
    /// <summary>MBR/GPT structures at the start of the disk.</summary>
    PartitionTable,

    /// <summary>Inside a partition (see <see cref="DifferenceRange.PartitionNumber"/>).</summary>
    Partition,

    /// <summary>Not covered by any partition (gaps, backup GPT, raw data).</summary>
    Unpartitioned,
}

/// <summary>A contiguous run of bytes that differs between the image and the device.</summary>
public sealed record DifferenceRange(long Offset, long Length, DifferenceArea Area, int? PartitionNumber);

public sealed record VerifyImageResult(
    long BytesCompared,
    long DifferentBytes,
    long DifferenceRangeCount,
    IReadOnlyList<DifferenceRange> Ranges,
    string ImageSha256,
    string DeviceSha256,
    ImageInfo Image,
    TimeSpan Elapsed)
{
    public bool Identical => DifferentBytes == 0;

    /// <summary>Only the ranges of a .bmap were compared; the SHA-256 values cover just those.</summary>
    public bool UsedBmap { get; init; }
}

/// <summary>
/// Compares an image (decompressed on the fly) with a device byte by byte.
/// The device is read with OS caching disabled, so the data really comes from
/// the media. Every differing range is located and attributed to the
/// partition table or a partition; SHA-256 of both sides is reported as well.
/// </summary>
public static class VerifyImageOperation
{
    public static async Task<VerifyImageResult> RunAsync(
        IImageContainer container,
        ImageEntry entry,
        Func<IBlockDevice> openDevice,
        VerifyImageOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(openDevice);
        var clock = Stopwatch.StartNew();
        progress?.Report(new OperationProgress(OperationPhase.Preparing, 0, entry.Length, 0, 0, null));

        using var reader = await Task.Run(() => container.OpenEntry(entry, cancellationToken), cancellationToken).ConfigureAwait(false);
        var head = new byte[ImageInspector.HeadSize];
        var headLength = await Task.Run(() => reader.Data.ReadUpTo(head), cancellationToken).ConfigureAwait(false);
        Array.Resize(ref head, headLength);
        var image = ImageInspector.Inspect(head);
        var source = new PrefixedStream(head, reader.Data);

        var bmap = options.Bmap;
        if (bmap != null && entry.Length is { } knownLength && knownLength != bmap.ImageSize)
        {
            throw new FlashToolException(FlashErrorCode.BmapSizeMismatch, "The .bmap file is for an image of another size.", null, bmap.ImageSize, knownLength);
        }

        var expectedLength = entry.Length ?? bmap?.ImageSize;
        using var device = new ResilientBlockDevice(
            await Task.Run(openDevice, cancellationToken).ConfigureAwait(false),
            options.IoRetry,
            cancellationToken);
        if (expectedLength > device.Length)
        {
            throw new FlashToolException(FlashErrorCode.ImageTooLarge, "The image is larger than the device.", null, expectedLength, device.Length);
        }

        using var bmapCheck = bmap != null ? new BmapImageCheck(bmap) : null;
        var mapped = bmap?.MappedByteRanges().ToList();
        var mappedIndex = 0;
        long mappedCompared = 0;

        var chunk = WriteImageOperation.AlignChunk(options.ChunkSize, device.SectorSize);
        var collector = new DifferenceCollector(image, options.MaxReportedRanges);
        var lastDifferenceReport = Stopwatch.StartNew();
        var differencesReported = false;
        using var imageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var deviceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var deviceBuffer = new AlignedBuffer(chunk);
        var reporter = new ProgressReporter(
            progress,
            OperationPhase.Verifying,
            expectedLength,
            reader.CompressedLength is > 0 ? () => (double)reader.CompressedPosition / reader.CompressedLength.Value : null);

        var compared = await TransferPipeline.RunAsync(
            chunk,
            options.PipelineDepth,
            buffer => source.ReadUpTo(buffer.Span),
            (buffer, count, offset) =>
            {
                if (offset + count > device.Length)
                {
                    throw new FlashToolException(FlashErrorCode.ImageTooLarge, "The image is larger than the device.", null, offset + count, device.Length);
                }

                var expected = buffer.Span[..count];
                if (mapped == null)
                {
                    var aligned = (int)StreamExtensions.AlignUp(count, device.SectorSize);
                    device.Read(offset, deviceBuffer.Span[..aligned]);
                    Compare(offset, expected, deviceBuffer.Span[..count]);
                }
                else
                {
                    bmapCheck!.Process(expected);

                    // Only the mapped parts of this chunk are read from the device.
                    var end = offset + count;
                    while (mappedIndex < mapped.Count && mapped[mappedIndex].End <= offset)
                    {
                        mappedIndex++;
                    }

                    for (var i = mappedIndex; i < mapped.Count && mapped[i].Start < end; i++)
                    {
                        var start = Math.Max(mapped[i].Start, offset);
                        var stop = Math.Min(mapped[i].End, end);
                        var readStart = StreamExtensions.AlignDown(start, device.SectorSize);
                        var readStop = StreamExtensions.AlignUp(stop, device.SectorSize);
                        var piece = deviceBuffer.Span[(int)(readStart - offset)..(int)(readStop - offset)];
                        device.Read(readStart, piece);
                        Compare(start, expected[(int)(start - offset)..(int)(stop - offset)], piece[(int)(start - readStart)..(int)(stop - readStart)]);
                        mappedCompared += stop - start;
                    }
                }

                // The first mismatch is reported at once, later changes are batched.
                if (options.DifferencesFound != null && collector.Changed
                    && (!differencesReported || lastDifferenceReport.Elapsed >= options.DifferenceReportInterval))
                {
                    options.DifferencesFound.Report(collector.Snapshot(offset + count));
                    differencesReported = true;
                    lastDifferenceReport.Restart();
                }

                reporter.Report(offset + count);
            },
            cancellationToken).ConfigureAwait(false);

        if (compared == 0)
        {
            throw new FlashToolException(FlashErrorCode.UnexpectedEndOfImage, "The image is empty.");
        }

        if (entry.Length.HasValue && compared != entry.Length.Value)
        {
            throw new FlashToolException(FlashErrorCode.UnexpectedEndOfImage, "The image ended before its declared size.", null, compared, entry.Length.Value);
        }

        if (bmap != null && compared != bmap.ImageSize)
        {
            throw new FlashToolException(FlashErrorCode.BmapSizeMismatch, "The .bmap file is for an image of another size.", null, bmap.ImageSize, compared);
        }

        collector.Complete();
        reporter.Report(compared, force: true);
        progress?.Report(new OperationProgress(OperationPhase.Completed, compared, compared, 1, 0, TimeSpan.Zero));

        return new VerifyImageResult(
            bmap != null ? mappedCompared : compared,
            collector.DifferentBytes,
            collector.RangeCount,
            collector.Ranges,
            Convert.ToHexString(imageHash.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(deviceHash.GetHashAndReset()).ToLowerInvariant(),
            image,
            clock.Elapsed)
        {
            UsedBmap = bmap != null,
        };

        void Compare(long at, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
        {
            imageHash.AppendData(expected);
            deviceHash.AppendData(actual);
            if (!expected.SequenceEqual(actual))
            {
                collector.AddDifferences(at, expected, actual);
            }
        }
    }

    /// <summary>
    /// Accumulates differing byte runs, merging runs that touch across chunk
    /// boundaries, and attributes them to disk areas.
    /// </summary>
    internal sealed class DifferenceCollector
    {
        private readonly ImageInfo _image;
        private readonly int _maxRanges;
        private readonly List<DifferenceRange> _ranges = [];
        private long _openStart = -1;
        private long _openEnd = -1;

        public DifferenceCollector(ImageInfo image, int maxRanges)
        {
            _image = image;
            _maxRanges = maxRanges;
        }

        public long DifferentBytes { get; private set; }

        public long RangeCount { get; private set; }

        public IReadOnlyList<DifferenceRange> Ranges => _ranges;

        /// <summary>True when differences were added since the last <see cref="Snapshot"/>.</summary>
        public bool Changed { get; private set; }

        /// <summary>Copy of the state, the still open range included.</summary>
        public VerifyDifferences Snapshot(long comparedUpTo)
        {
            Changed = false;
            var ranges = new List<DifferenceRange>(_ranges);
            var count = RangeCount;
            if (_openStart >= 0)
            {
                count++;
                if (ranges.Count < _maxRanges)
                {
                    ranges.Add(Describe(_openStart, _openEnd));
                }
            }

            return new VerifyDifferences(DifferentBytes, count, ranges, comparedUpTo);
        }

        /// <summary>Closes the last open range; call once after all data was added.</summary>
        public void Complete() => Flush();

        public void AddDifferences(long baseOffset, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
        {
            var pos = 0;
            while (pos < expected.Length)
            {
                pos += expected[pos..].CommonPrefixLength(actual[pos..]);
                if (pos >= expected.Length)
                {
                    break;
                }

                var start = pos;
                while (pos < expected.Length && expected[pos] != actual[pos])
                {
                    pos++;
                }

                Add(baseOffset + start, baseOffset + pos);
            }
        }

        private void Add(long start, long end)
        {
            DifferentBytes += end - start;
            Changed = true;

            // Runs separated by a few equal bytes are reported as one range.
            const int mergeGap = 512;
            if (_openStart >= 0 && start - _openEnd <= mergeGap && SameArea(_openStart, start))
            {
                _openEnd = end;
                return;
            }

            Flush();
            _openStart = start;
            _openEnd = end;
        }

        private void Flush()
        {
            if (_openStart < 0)
            {
                return;
            }

            RangeCount++;
            if (_ranges.Count < _maxRanges)
            {
                _ranges.Add(Describe(_openStart, _openEnd));
            }

            _openStart = -1;
        }

        private DifferenceRange Describe(long start, long end)
        {
            var (area, number) = Locate(start);
            return new DifferenceRange(start, end - start, area, number);
        }

        private bool SameArea(long a, long b) => Locate(a) == Locate(b);

        private (DifferenceArea Area, int? Number) Locate(long offset)
        {
            var partition = _image.Partitions
                .Where(p => !p.IsExtendedContainer && offset >= p.Offset && offset < p.End)
                .OrderBy(p => p.Length)
                .FirstOrDefault();
            if (partition != null)
            {
                return (DifferenceArea.Partition, partition.Number);
            }

            var tableEnd = _image.TableType switch
            {
                PartitionTableType.Mbr => 512L,
                PartitionTableType.Gpt => 34L * _image.SectorSize,
                _ => 0L,
            };
            return offset < tableEnd ? (DifferenceArea.PartitionTable, null) : (DifferenceArea.Unpartitioned, null);
        }
    }
}
