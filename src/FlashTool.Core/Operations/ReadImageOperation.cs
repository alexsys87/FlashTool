using System.Diagnostics;
using FlashTool.Core.Checksums;
using FlashTool.Core.Containers;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Operations;

public sealed record ReadImageOptions
{
    /// <summary>Decompress the created file afterwards and compare with the device data.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Stop at the end of the last partition instead of copying the whole device.</summary>
    public bool OnlyAllocatedPartitions { get; init; }

    /// <summary>
    /// Store the free blocks of ext2/3/4 file systems as zeros without reading them. Faster, and the
    /// zeros compress to almost nothing, but the image is no longer a byte-for-byte copy of the device:
    /// whatever lay in the free space (deleted files and the like) is not kept. File systems that are
    /// not cleanly unmounted, or whose metadata does not check out, are copied in full anyway.
    /// </summary>
    public bool SkipFreeBlocks { get; init; }

    /// <summary>Container format; null picks it from the destination file extension.</summary>
    public ContainerFormat? Format { get; init; }

    public CompressionSettings Compression { get; init; } = new();

    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    public int PipelineDepth { get; init; } = 8;

    /// <summary>Retries of failed device reads/writes (bad sectors, timeouts).</summary>
    public IoRetryPolicy IoRetry { get; init; } = IoRetryPolicy.Default;

    /// <summary>
    /// Save the SHA-256 of the created file next to it as <c>&lt;image&gt;.sha256</c> in <c>sha256sum</c> format, so the
    /// image can be checked later (writing it back picks that file up by itself). Whatever the setting, a checksum
    /// file left over from an earlier image with the same name is removed first: it no longer describes the file
    /// that is being replaced.
    /// </summary>
    public bool WriteChecksumFile { get; init; }
}

public sealed record ReadImageResult(
    string Path,
    ContainerFormat Format,
    long BytesRead,
    long FileSize,
    bool Verified,
    ImageInfo Image,
    TimeSpan Elapsed)
{
    /// <summary>SHA-256 of the created file; set when a checksum file was asked for.</summary>
    public byte[]? Sha256 { get; init; }

    /// <summary>The checksum file that was written, if any.</summary>
    public string? ChecksumFilePath { get; init; }

    /// <summary>Why the checksum file could not be saved. The image itself was created all the same.</summary>
    public string? ChecksumFileError { get; init; }

    /// <summary>One entry per ext2/3/4 file system when free blocks were to be skipped; empty otherwise.</summary>
    public IReadOnlyList<PartitionFreeSpace> FreeSpace { get; init; } = [];

    /// <summary>Bytes stored as zeros without being read.</summary>
    public long SkippedBytes => FreeSpace.Sum(f => f.SkippedBytes);
}

/// <summary>
/// Creates an image file from a device, compressing it on the fly straight
/// into the destination archive.
/// </summary>
public static class ReadImageOperation
{
    public static async Task<ReadImageResult> RunAsync(
        Func<IBlockDevice> openSource,
        string destinationPath,
        ContainerRegistry registry,
        ReadImageOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openSource);
        ArgumentNullException.ThrowIfNull(registry);
        var clock = Stopwatch.StartNew();
        progress?.Report(new OperationProgress(OperationPhase.Preparing, 0, null, 0, 0, null));

        var handler = options.Format.HasValue ? registry.Get(options.Format.Value) : registry.FromFileName(destinationPath);
        if (!handler.IsAvailable || !handler.CanCreate)
        {
            throw new FlashToolException(FlashErrorCode.ContainerBackendUnavailable, $"Cannot create {handler.Format} files.", null, handler.Format.ToString());
        }

        using var device = new ResilientBlockDevice(
            await Task.Run(openSource, cancellationToken).ConfigureAwait(false),
            options.IoRetry,
            cancellationToken);
        var headLength = (int)Math.Min(ImageInspector.HeadSize, StreamExtensions.AlignDown(device.Length, device.SectorSize));
        var head = device.ReadBytes(0, headLength) ?? [];
        var image = ImageInspector.Inspect(head, (o, c) => device.ReadBytes(o, c));

        var length = device.Length;
        if (options.OnlyAllocatedPartitions && image.UsedLength is { } used && used > 0)
        {
            length = Math.Min(device.Length, StreamExtensions.AlignUp(used, device.SectorSize));
        }

        FreeBlockMap? freeBlocks = null;
        IReadOnlyList<PartitionFreeSpace> freeSpace = [];
        if (options.SkipFreeBlocks)
        {
            (freeBlocks, freeSpace) = await Task.Run(() => FreeBlockMap.Build(device, image, length, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        var chunk = WriteImageOperation.AlignChunk(options.ChunkSize, device.SectorSize);
        var hashes = new BlockHashList(chunk);
        var entryName = ContainerNaming.GetInnerName(destinationPath);
        var completed = false;
        byte[]? sha256 = null;

        try
        {
            var reporter = new ProgressReporter(progress, OperationPhase.Reading, length);
            TryDelete(ChecksumFile.SidecarPath(destinationPath));
            var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024);
            Stream sink;
            try
            {
                sink = handler.Create(output, entryName, length, options.Compression);
            }
            catch
            {
                // Close the file so the cleanup below can delete it.
                output.Dispose();
                throw;
            }

            try
            {
                long readPosition = 0;
                await TransferPipeline.RunAsync(
                    chunk,
                    options.PipelineDepth,
                    buffer =>
                    {
                        var count = (int)Math.Min(chunk, length - readPosition);
                        if (count <= 0)
                        {
                            return 0;
                        }

                        if (freeBlocks != null)
                        {
                            freeBlocks.Read(device, readPosition, buffer.Span[..count]);
                        }
                        else
                        {
                            device.Read(readPosition, buffer.Span[..count]);
                        }

                        readPosition += count;
                        return count;
                    },
                    (buffer, count, offset) =>
                    {
                        var data = buffer.Span[..count];
                        hashes.Append(data);
                        sink.Write(data);
                        reporter.Report(offset + count);
                    },
                    cancellationToken).ConfigureAwait(false);

                reporter.Report(length, force: true);
                progress?.Report(new OperationProgress(OperationPhase.Finalizing, 0, null, 0, 0, null));
            }
            finally
            {
                // Finalizes the archive (writes headers/central directory) and closes the file.
                await Task.Run(sink.Dispose, CancellationToken.None).ConfigureAwait(false);
            }

            if (options.Verify)
            {
                await VerifyFileAsync(destinationPath, registry, hashes, chunk, options.PipelineDepth, progress, cancellationToken).ConfigureAwait(false);
            }

            if (options.WriteChecksumFile)
            {
                sha256 = await Task.Run(() => HashFile(destinationPath, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDelete(destinationPath);
            }
        }

        string? checksumPath = null;
        string? checksumError = null;
        if (sha256 != null)
        {
            try
            {
                checksumPath = ChecksumFile.WriteSidecar(destinationPath, sha256);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                checksumError = ex.Message;
            }
        }

        var fileSize = new FileInfo(destinationPath).Length;
        progress?.Report(new OperationProgress(OperationPhase.Completed, length, length, 1, 0, TimeSpan.Zero));
        return new ReadImageResult(destinationPath, handler.Format, length, fileSize, options.Verify, image, clock.Elapsed)
        {
            Sha256 = sha256,
            ChecksumFilePath = checksumPath,
            ChecksumFileError = checksumError,
            FreeSpace = freeSpace,
        };
    }

    /// <summary>SHA-256 of the finished image file, as it lies on disk (the archive, not the data inside).</summary>
    private static byte[] HashFile(string path, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return StreamHasher.Sha256(stream, new ProgressReporter(progress, OperationPhase.Hashing, stream.Length), cancellationToken);
    }

    /// <summary>Re-opens the created file (decompressing it) and compares with the recorded hashes.</summary>
    private static async Task VerifyFileAsync(
        string path,
        ContainerRegistry registry,
        BlockHashList hashes,
        int chunk,
        int depth,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var container = registry.Open(new FileStreamSource(path), cancellationToken);
        if (container.Entries.Count != 1)
        {
            throw new VerificationFailedException(0);
        }

        using var reader = container.OpenEntry(container.Entries[0], cancellationToken);
        var reporter = new ProgressReporter(progress, OperationPhase.Verifying, hashes.TotalLength);

        var total = await TransferPipeline.RunAsync(
            chunk,
            depth,
            buffer => reader.Data.ReadUpTo(buffer.Span),
            (buffer, count, offset) =>
            {
                if (!hashes.Matches(offset, buffer.Span[..count]))
                {
                    throw new VerificationFailedException(offset);
                }

                reporter.Report(offset + count);
            },
            cancellationToken).ConfigureAwait(false);

        if (total != hashes.TotalLength)
        {
            throw new VerificationFailedException(total);
        }

        reporter.Report(total, force: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
