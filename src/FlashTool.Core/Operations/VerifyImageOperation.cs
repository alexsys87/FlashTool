using System.Diagnostics;
using System.Security.Cryptography;
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
}

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

        using var device = new ResilientBlockDevice(
            await Task.Run(openDevice, cancellationToken).ConfigureAwait(false),
            options.IoRetry,
            cancellationToken);
        if (entry.Length > device.Length)
        {
            throw new FlashToolException(FlashErrorCode.ImageTooLarge, "The image is larger than the device.", null, entry.Length, device.Length);
        }

        var chunk = WriteImageOperation.AlignChunk(options.ChunkSize, device.SectorSize);
        var collector = new DifferenceCollector(image, options.MaxReportedRanges);
        using var imageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var deviceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var deviceBuffer = new AlignedBuffer(chunk);
        var reporter = new ProgressReporter(
            progress,
            OperationPhase.Verifying,
            entry.Length,
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
                var aligned = (int)StreamExtensions.AlignUp(count, device.SectorSize);
                device.Read(offset, deviceBuffer.Span[..aligned]);
                var actual = deviceBuffer.Span[..count];

                imageHash.AppendData(expected);
                deviceHash.AppendData(actual);
                if (!expected.SequenceEqual(actual))
                {
                    collector.AddDifferences(offset, expected, actual);
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

        collector.Complete();
        reporter.Report(compared, force: true);
        progress?.Report(new OperationProgress(OperationPhase.Completed, compared, compared, 1, 0, TimeSpan.Zero));

        return new VerifyImageResult(
            compared,
            collector.DifferentBytes,
            collector.RangeCount,
            collector.Ranges,
            Convert.ToHexString(imageHash.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(deviceHash.GetHashAndReset()).ToLowerInvariant(),
            image,
            clock.Elapsed);
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
                var (area, number) = Locate(_openStart);
                _ranges.Add(new DifferenceRange(_openStart, _openEnd - _openStart, area, number));
            }

            _openStart = -1;
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
