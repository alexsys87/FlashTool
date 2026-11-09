using System.Diagnostics;
using FlashTool.Core.Bmap;
using FlashTool.Core.Checksums;
using FlashTool.Core.Containers;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Operations;

public sealed record WriteImageOptions
{
    /// <summary>Read the device back and compare with what was written.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Grow the last partition to the end of the device.</summary>
    public bool ExpandLastPartition { get; init; }

    /// <summary>Refuse content that is not recognized as a disk image.</summary>
    public bool RequireRecognizedImage { get; init; } = true;

    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    public int PipelineDepth { get; init; } = 8;

    /// <summary>Retries of failed device reads/writes (bad sectors, timeouts).</summary>
    public IoRetryPolicy IoRetry { get; init; } = IoRetryPolicy.Default;

    public IReadOnlyList<IFileSystemResizer> FileSystemResizers { get; init; } = [];

    /// <summary>
    /// SHA-256 of the source file itself, as published: the .xz/.gz/.zst/.bz2/.zip/.7z archive, or the plain image.
    /// A local file is checked before the device is opened. A download is hashed while it streams (the source
    /// must be a <see cref="HashingStreamSource"/>) and judged before the first chunk, which holds the partition
    /// table, is committed. Null skips the check.
    /// </summary>
    public byte[]? ExpectedSourceSha256 { get; init; }

    /// <summary>
    /// Block map of the image. Only its mapped blocks are written, plus everything outside partitions and
    /// the first and last mebibyte of each partition (see <see cref="BmapWritePlan"/>); verification reads
    /// back just what was written. The image is checked against the map while it streams: a range whose
    /// checksum differs, or data in a hole, stops the write before the partition table is committed.
    /// </summary>
    public BmapFile? Bmap { get; init; }

    /// <summary>
    /// Experimental: read each piece of the device first and write only the 64 KiB blocks that differ
    /// (see <see cref="DifferentialWriter"/>). The result is the same device as a full write, with less
    /// wear and, when little has changed, in less time. The first chunk is still blanked first and
    /// written last, and verification still reads back everything, compared or not.
    /// </summary>
    public bool DifferentialWrite { get; init; }

    /// <summary>How much compared data decides whether comparing still pays off.</summary>
    public long DifferentialWindowBytes { get; init; } = 128L * 1024 * 1024;
}

public sealed record WriteImageResult(
    long BytesWritten,
    bool Verified,
    ImageInfo Image,
    PartitionFixupResult? Fixups,
    TimeSpan Elapsed)
{
    /// <summary>Outcome of the check against <see cref="WriteImageOptions.ExpectedSourceSha256"/>.</summary>
    public SourceChecksumStatus SourceChecksum { get; init; }

    /// <summary>With a .bmap or differential writing: bytes that actually went to the device.</summary>
    public long? DeviceBytesWritten { get; init; }

    /// <summary>What differential writing compared, skipped and wrote; null when it was off.</summary>
    public DifferentialWriteStats? Differential { get; init; }
}

/// <summary>
/// Writes one image entry (decompressed on the fly) to a block device.
/// </summary>
public static class WriteImageOperation
{
    public static async Task<WriteImageResult> RunAsync(
        IImageContainer container,
        ImageEntry entry,
        Func<IBlockDevice> openTarget,
        WriteImageOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(openTarget);
        var clock = Stopwatch.StartNew();
        progress?.Report(new OperationProgress(OperationPhase.Preparing, 0, entry.Length, 0, 0, null));

        // A local file is hashed up front, so a damaged one never reaches the device.
        var checksum = options.ExpectedSourceSha256 is { } expected ? new SourceChecksumCheck(container.Source, expected) : null;
        if (checksum != null)
        {
            await checksum.VerifyUpFrontAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        // Open the source and validate it before touching the device.
        using var reader = await Task.Run(() => container.OpenEntry(entry, cancellationToken), cancellationToken).ConfigureAwait(false);
        var head = new byte[ImageInspector.HeadSize];
        var headLength = await Task.Run(() => reader.Data.ReadUpTo(head), cancellationToken).ConfigureAwait(false);
        Array.Resize(ref head, headLength);

        var image = ImageInspector.Inspect(head);
        if (options.RequireRecognizedImage && !image.IsDiskImage)
        {
            throw new FlashToolException(FlashErrorCode.NotADiskImage, $"'{entry.Name}' is not a recognized disk image.", null, entry.Name);
        }

        var bmap = options.Bmap;
        if (bmap != null && entry.Length is { } knownLength && knownLength != bmap.ImageSize)
        {
            throw new FlashToolException(FlashErrorCode.BmapSizeMismatch, "The .bmap file is for an image of another size.", null, bmap.ImageSize, knownLength);
        }

        // A .bmap also tells the size of a gzip or bzip2 image, which the stream itself does not.
        var expectedLength = entry.Length ?? bmap?.ImageSize;

        var raw = await Task.Run(openTarget, cancellationToken).ConfigureAwait(false);
        var device = new ResilientBlockDevice(raw, options.IoRetry, cancellationToken);

        // Compare reads that fail are not worth a question to the user: that piece is simply written.
        var compareDevice = new ResilientBlockDevice(raw, new IoRetryPolicy { AutomaticAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(100) }, cancellationToken);
        DifferentialWriter? differential = null;
        long written;
        long? deviceBytesWritten = null;
        PartitionFixupResult? fixups = null;
        try
        {
            if (expectedLength > device.Length)
            {
                throw new FlashToolException(FlashErrorCode.ImageTooLarge, "The image is larger than the device.", null, expectedLength, device.Length);
            }

            var chunk = AlignChunk(options.ChunkSize, device.SectorSize);
            var hashes = new BlockHashList(chunk);
            var planned = bmap != null ? new PlannedWriter(BmapWritePlan.Build(bmap, image, device.SectorSize)) : null;
            differential = options.DifferentialWrite ? new DifferentialWriter(device, compareDevice, chunk, options.DifferentialWindowBytes) : null;
            using var bmapCheck = bmap != null ? new BmapImageCheck(bmap) : null;
            var source = new PrefixedStream(head, reader.Data);

            // Blank the first chunk so the OS never sees a half-written partition
            // table; the real first chunk is written last.
            var firstChunk = new AlignedBuffer(chunk);
            try
            {
                var firstLength = 0;
                var zeroLength = (int)Math.Min(chunk, StreamExtensions.AlignDown(device.Length, device.SectorSize));
                device.Write(0, firstChunk.Span[..zeroLength]);

                var writeProgress = new ProgressReporter(
                    progress,
                    OperationPhase.Writing,
                    expectedLength,
                    reader.CompressedLength is > 0 ? () => (double)reader.CompressedPosition / reader.CompressedLength.Value : null,
                    differential != null ? () => differential.DeviceBytes : null);

                written = await TransferPipeline.RunAsync(
                    chunk,
                    options.PipelineDepth,
                    buffer => source.ReadUpTo(buffer.Span),
                    (buffer, count, offset) =>
                    {
                        if (offset + count > device.Length)
                        {
                            throw new FlashToolException(FlashErrorCode.ImageTooLarge, "The image is larger than the device.", null, offset + count, device.Length);
                        }

                        var data = buffer.Span[..count];
                        bmapCheck?.Process(data);
                        var aligned = PadToSector(buffer.Span, count, device.SectorSize);
                        if (planned == null)
                        {
                            hashes.Append(data);
                        }

                        if (offset == 0)
                        {
                            buffer.Span[..aligned].CopyTo(firstChunk.Span);
                            firstLength = aligned;
                            planned?.Record(0, data, aligned);
                        }
                        else if (planned != null)
                        {
                            planned.Write(device, offset, buffer.Span[..aligned], count, differential);
                        }
                        else if (differential != null)
                        {
                            differential.Write(offset, buffer.Span[..aligned]);
                        }
                        else
                        {
                            device.Write(offset, buffer.Span[..aligned]);
                        }

                        writeProgress.Report(offset + count);
                    },
                    cancellationToken).ConfigureAwait(false);

                if (written == 0)
                {
                    throw new FlashToolException(FlashErrorCode.UnexpectedEndOfImage, "The image is empty.");
                }

                if (entry.Length.HasValue && written != entry.Length.Value)
                {
                    throw new FlashToolException(FlashErrorCode.UnexpectedEndOfImage, "The image ended before its declared size.", null, written, entry.Length.Value);
                }

                if (bmap != null && written != bmap.ImageSize)
                {
                    throw new FlashToolException(FlashErrorCode.BmapSizeMismatch, "The .bmap file is for an image of another size.", null, bmap.ImageSize, written);
                }

                // A streamed source has been read to its end by now. Judging it here, before the real first chunk
                // goes out, leaves the device with a blank partition table if the download turns out to be damaged.
                checksum?.VerifyStreamed();

                device.Write(0, firstChunk.Span[..firstLength]);
                device.Flush();
                writeProgress.Report(written, force: true);
            }
            finally
            {
                firstChunk.Free();
            }

            if (options.Verify && planned != null)
            {
                await planned.VerifyAsync(device, chunk, options.PipelineDepth, progress, cancellationToken).ConfigureAwait(false);
            }
            else if (options.Verify)
            {
                await VerifyDeviceAsync(device, hashes, chunk, options.PipelineDepth, progress, cancellationToken).ConfigureAwait(false);
            }

            // With differential writing, what really went out: the compared pieces plus the first chunk.
            deviceBytesWritten = differential != null
                ? differential.Stats.BytesWritten + Math.Min(chunk, StreamExtensions.AlignUp(written, device.SectorSize))
                : planned?.BytesWritten;

            // Partition table changes come after verification so they don't count as mismatches.
            progress?.Report(new OperationProgress(OperationPhase.Finalizing, 0, null, 0, 0, null));
            if (written < device.Length)
            {
                fixups = await Task.Run(
                    () => PartitionTableEditor.Apply(device, image, options.ExpandLastPartition, options.FileSystemResizers, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            differential?.Dispose();

            // Releasing the device lets the OS re-read the partition table and mount the volumes.
            // compareDevice wraps the same handle and is not disposed separately.
            device.Dispose();
        }

        if (fixups is { PartitionExtended: true, FileSystemResized: false, FileSystemResizeError: null, Partition: { } grown })
        {
            fixups = await Task.Run(() => ResizeOnline(fixups, grown, options.FileSystemResizers, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new OperationProgress(OperationPhase.Completed, written, written, 1, 0, TimeSpan.Zero));
        return new WriteImageResult(written, options.Verify, image, fixups, clock.Elapsed)
        {
            SourceChecksum = checksum?.Status ?? SourceChecksumStatus.NotChecked,
            DeviceBytesWritten = deviceBytesWritten,
            Differential = differential?.Stats,
        };
    }

    /// <summary>Runs resizers that need the OS to have mounted the volume (NTFS).</summary>
    private static PartitionFixupResult ResizeOnline(
        PartitionFixupResult fixups,
        PartitionInfo partition,
        IReadOnlyList<IFileSystemResizer> resizers,
        CancellationToken cancellationToken)
    {
        var resizer = resizers.FirstOrDefault(r => !r.RequiresOfflineDevice && r.CanResize(partition.FileSystem));
        if (resizer == null)
        {
            return fixups;
        }

        try
        {
            return fixups with { FileSystemResized = resizer.Resize(null, partition, cancellationToken) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fixups with { FileSystemResizeError = ex.Message };
        }
    }

    /// <summary>Reads the device back and compares it with the recorded block hashes.</summary>
    internal static async Task VerifyDeviceAsync(
        IBlockDevice device,
        BlockHashList hashes,
        int chunk,
        int depth,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = hashes.TotalLength;
        var reporter = new ProgressReporter(progress, OperationPhase.Verifying, total);
        long readPosition = 0;

        await TransferPipeline.RunAsync(
            chunk,
            depth,
            buffer =>
            {
                var count = (int)Math.Min(chunk, total - readPosition);
                if (count <= 0)
                {
                    return 0;
                }

                var aligned = (int)StreamExtensions.AlignUp(count, device.SectorSize);
                device.Read(readPosition, buffer.Span[..aligned]);
                readPosition += count;
                return count;
            },
            (buffer, count, offset) =>
            {
                if (!hashes.Matches(offset, buffer.Span[..count]))
                {
                    throw new VerificationFailedException(offset);
                }

                reporter.Report(offset + count);
            },
            cancellationToken).ConfigureAwait(false);

        reporter.Report(total, force: true);
    }

    internal static int AlignChunk(int chunk, int sectorSize)
        => (int)Math.Max(sectorSize, StreamExtensions.AlignDown(chunk, Math.Max(sectorSize, AlignedBuffer.DefaultAlignment)));

    /// <summary>Zero-pads a partial last chunk up to a whole sector.</summary>
    private static int PadToSector(Span<byte> buffer, int count, int sectorSize)
    {
        var aligned = (int)StreamExtensions.AlignUp(count, sectorSize);
        buffer[count..aligned].Clear();
        return aligned;
    }
}
