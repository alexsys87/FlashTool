using System.Diagnostics;
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
    public bool ExpandLastPartition { get; init; } = true;

    /// <summary>Refuse content that is not recognized as a disk image.</summary>
    public bool RequireRecognizedImage { get; init; } = true;

    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    public int PipelineDepth { get; init; } = 8;

    public IReadOnlyList<IFileSystemResizer> FileSystemResizers { get; init; } = [];
}

public sealed record WriteImageResult(
    long BytesWritten,
    bool Verified,
    ImageInfo Image,
    PartitionFixupResult? Fixups,
    TimeSpan Elapsed);

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

        using var device = await Task.Run(openTarget, cancellationToken).ConfigureAwait(false);
        if (entry.Length > device.Length)
        {
            throw new FlashToolException(FlashErrorCode.ImageTooLarge, "The image is larger than the device.", null, entry.Length, device.Length);
        }

        var chunk = AlignChunk(options.ChunkSize, device.SectorSize);
        var hashes = new BlockHashList(chunk);
        var source = new PrefixedStream(head, reader.Data);

        // Blank the first chunk so the OS never sees a half-written partition
        // table; the real first chunk is written last.
        var firstChunk = new AlignedBuffer(chunk);
        long written;
        try
        {
            var firstLength = 0;
            var zeroLength = (int)Math.Min(chunk, StreamExtensions.AlignDown(device.Length, device.SectorSize));
            device.Write(0, firstChunk.Span[..zeroLength]);

            var writeProgress = new ProgressReporter(
                progress,
                OperationPhase.Writing,
                entry.Length,
                reader.CompressedLength is > 0 ? () => (double)reader.CompressedPosition / reader.CompressedLength.Value : null);

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
                    hashes.Append(data);
                    var aligned = PadToSector(buffer.Span, count, device.SectorSize);
                    if (offset == 0)
                    {
                        buffer.Span[..aligned].CopyTo(firstChunk.Span);
                        firstLength = aligned;
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

            device.Write(0, firstChunk.Span[..firstLength]);
            device.Flush();
            writeProgress.Report(written, force: true);
        }
        finally
        {
            firstChunk.Free();
        }

        if (options.Verify)
        {
            await VerifyDeviceAsync(device, hashes, chunk, options.PipelineDepth, progress, cancellationToken).ConfigureAwait(false);
        }

        // Partition table changes come after verification so they don't count as mismatches.
        progress?.Report(new OperationProgress(OperationPhase.Finalizing, 0, null, 0, 0, null));
        PartitionFixupResult? fixups = null;
        if (written < device.Length)
        {
            fixups = await Task.Run(
                () => PartitionTableEditor.Apply(device, image, options.ExpandLastPartition, options.FileSystemResizers, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new OperationProgress(OperationPhase.Completed, written, written, 1, 0, TimeSpan.Zero));
        return new WriteImageResult(written, options.Verify, image, fixups, clock.Elapsed);
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
