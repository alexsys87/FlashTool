using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FlashTool.Core.FileSystems.ExFat;
using FlashTool.Core.FileSystems.Fat;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Operations;

public enum EraseMode
{
    /// <summary>Clear the partition tables and file system signatures only (seconds; the old data stays on the media).</summary>
    Quick,

    /// <summary>Write zeros over the whole device once.</summary>
    Full,

    /// <summary>Overwrite the whole device with a pattern and read it back to check every sector.</summary>
    Secure,
}

/// <summary>What <see cref="EraseMode.Secure"/> writes.</summary>
public enum ErasePattern
{
    /// <summary>
    /// Pseudo-random data that is different in every sector. Reading it back also
    /// catches counterfeit media that wraps addresses around, which zeros cannot.
    /// </summary>
    Random,

    Zeros,

    /// <summary>0xFF bytes (the erased state of NAND flash).</summary>
    Ones,
}

public enum EraseFileSystem
{
    /// <summary>FAT32 up to 32 GiB, exFAT above (what the SD card specification prescribes).</summary>
    Auto,
    Fat32,
    ExFat,

    /// <summary>Leave the device empty, without a partition table.</summary>
    None,
}

public enum ErasePartitionScheme
{
    Mbr,
    Gpt,
}

public sealed record EraseOptions
{
    public EraseMode Mode { get; init; } = EraseMode.Quick;

    /// <summary>Data written by <see cref="EraseMode.Secure"/>.</summary>
    public ErasePattern Pattern { get; init; } = ErasePattern.Random;

    /// <summary>Read the device back after <see cref="EraseMode.Secure"/> overwriting.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>File system created after erasing.</summary>
    public EraseFileSystem FileSystem { get; init; } = EraseFileSystem.Auto;

    public ErasePartitionScheme PartitionScheme { get; init; } = ErasePartitionScheme.Mbr;

    public string Label { get; init; } = string.Empty;

    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    public int PipelineDepth { get; init; } = 4;

    /// <summary>Retries of failed device reads/writes (bad sectors, timeouts).</summary>
    public IoRetryPolicy IoRetry { get; init; } = IoRetryPolicy.Default;

    /// <summary>Seed of the random pattern; a fresh one is used when null.</summary>
    public ulong? PatternSeed { get; init; }
}

/// <summary>The partition and file system created after erasing.</summary>
public sealed record EraseLayout(
    PartitionTableType Table,
    FileSystemType FileSystem,
    long PartitionOffset,
    long PartitionLength)
{
    public Fat32Layout? Fat32 { get; init; }

    public ExFatLayout? ExFat { get; init; }

    public int ClusterSize => Fat32?.ClusterSize ?? ExFat?.ClusterSize ?? 0;
}

public sealed record EraseResult(long BytesOverwritten, bool Verified, EraseLayout? Layout, TimeSpan Elapsed);

/// <summary>
/// Erases a block device and optionally creates a new partition table with
/// one FAT32 or exFAT partition covering the device.
/// </summary>
public static class EraseDeviceOperation
{
    /// <summary>Volumes up to this size get FAT32 in <see cref="EraseFileSystem.Auto"/> mode.</summary>
    public const long Fat32AutoLimit = 32L * 1024 * 1024 * 1024;

    /// <summary>How much is cleared at the start of every old partition and at the end of the device in quick mode.</summary>
    internal const int SignatureWipeSize = 1024 * 1024;

    /// <summary>
    /// Computes the partition and file system that will be created, without touching the device.
    /// </summary>
    /// <returns>Null when no file system is to be created.</returns>
    /// <exception cref="FlashToolException">The device is too small or too large for the chosen layout.</exception>
    public static EraseLayout? Plan(long deviceLength, int sectorSize, EraseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FileSystem == EraseFileSystem.None)
        {
            return null;
        }

        var totalSectors = deviceLength / sectorSize;
        const long mib = 1024 * 1024;
        var alignment = Math.Max(sectorSize, deviceLength >= 1024 * mib ? 4 * mib : mib);
        var firstLba = alignment / sectorSize;
        long lastLba;
        PartitionTableType table;
        if (options.PartitionScheme == ErasePartitionScheme.Gpt)
        {
            var (firstUsable, lastUsable) = PartitionTableWriter.GetGptUsableRange(deviceLength, sectorSize);
            firstLba = Math.Max(firstLba, firstUsable);
            lastLba = lastUsable;
            table = PartitionTableType.Gpt;
        }
        else
        {
            if (totalSectors > PartitionTableWriter.MaxMbrSectors)
            {
                throw new FlashToolException(FlashErrorCode.DeviceTooLargeForMbr, "The device is too large for an MBR partition table.", null, deviceLength);
            }

            lastLba = totalSectors - 1;
            table = PartitionTableType.Mbr;
        }

        var partitionLength = Math.Max(0, lastLba - firstLba + 1) * sectorSize;
        var fileSystem = options.FileSystem switch
        {
            EraseFileSystem.Fat32 => FileSystemType.Fat32,
            EraseFileSystem.ExFat => FileSystemType.ExFat,
            _ => partitionLength <= Fat32AutoLimit ? FileSystemType.Fat32 : FileSystemType.ExFat,
        };

        var layout = new EraseLayout(table, fileSystem, firstLba * sectorSize, partitionLength);
        if (fileSystem == FileSystemType.Fat32 && Fat32Formatter.TryPlan(partitionLength, sectorSize, (int)alignment) is { } fat)
        {
            return layout with { Fat32 = fat };
        }

        // Auto falls back to exFAT for volumes too small for FAT32.
        if ((fileSystem == FileSystemType.ExFat || options.FileSystem == EraseFileSystem.Auto)
            && ExFatFormatter.TryPlan(partitionLength, sectorSize, (int)alignment) is { } exFat)
        {
            return layout with { FileSystem = FileSystemType.ExFat, ExFat = exFat };
        }

        throw new FlashToolException(
            FlashErrorCode.FileSystemSizeNotSupported,
            $"{fileSystem} cannot be created on a volume of {partitionLength} bytes.",
            null,
            fileSystem,
            partitionLength);
    }

    public static async Task<EraseResult> RunAsync(
        Func<IBlockDevice> openTarget,
        EraseOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openTarget);
        ArgumentNullException.ThrowIfNull(options);
        var clock = Stopwatch.StartNew();
        progress?.Report(new OperationProgress(OperationPhase.Preparing, 0, null, 0, 0, null));

        using var device = new ResilientBlockDevice(
            await Task.Run(openTarget, cancellationToken).ConfigureAwait(false),
            options.IoRetry,
            cancellationToken);
        var length = StreamExtensions.AlignDown(device.Length, device.SectorSize);

        // Fails for unsupported sizes before anything is written.
        var layout = Plan(length, device.SectorSize, options);

        long overwritten;
        var verified = false;
        switch (options.Mode)
        {
            case EraseMode.Quick:
                overwritten = await Task.Run(() => WipeSignatures(device, length, layout, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;

            case EraseMode.Full:
                overwritten = await OverwriteAsync(device, length, ErasePattern.Zeros, 0, options, progress, cancellationToken).ConfigureAwait(false);
                break;

            default:
                var seed = options.PatternSeed ?? BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
                overwritten = await OverwriteAsync(device, length, options.Pattern, seed, options, progress, cancellationToken).ConfigureAwait(false);
                if (options.Verify)
                {
                    await VerifyAsync(device, length, options.Pattern, seed, options, progress, cancellationToken).ConfigureAwait(false);
                    verified = true;
                }

                break;
        }

        if (layout != null)
        {
            progress?.Report(new OperationProgress(OperationPhase.Formatting, 0, null, 0, 0, null));
            await Task.Run(() => Format(device, layout, options.Label, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        device.Flush();
        progress?.Report(new OperationProgress(OperationPhase.Completed, overwritten, overwritten, 1, 0, TimeSpan.Zero));
        return new EraseResult(overwritten, verified, layout, clock.Elapsed);
    }

    /// <summary>
    /// Quick erase: clears the partition tables at both ends of the device and
    /// the start of every old partition, where file systems keep their signatures.
    /// </summary>
    private static long WipeSignatures(ResilientBlockDevice device, long length, EraseLayout? layout, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var ranges = new List<(long Start, long End)>
        {
            (0, Math.Max(SignatureWipeSize, layout?.PartitionOffset ?? 0)),
            (length - SignatureWipeSize, length),
        };

        foreach (var partition in ReadOldPartitions(device, length))
        {
            ranges.Add((partition.Offset, partition.Offset + SignatureWipeSize));
        }

        // Clamp to the device, align to sectors and merge overlaps.
        var merged = new List<(long Start, long End)>();
        foreach (var (start, end) in ranges
            .Select(r => (Start: StreamExtensions.AlignDown(Math.Clamp(r.Start, 0, length), device.SectorSize), End: StreamExtensions.AlignUp(Math.Clamp(r.End, 0, length), device.SectorSize)))
            .Where(r => r.End > r.Start)
            .OrderBy(r => r.Start))
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

        var total = merged.Sum(r => r.End - r.Start);
        var reporter = new ProgressReporter(progress, OperationPhase.Erasing, total);
        long done = 0;
        foreach (var (start, end) in merged)
        {
            device.Zero(start, end - start, cancellationToken);
            done += end - start;
            reporter.Report(done);
        }

        device.Flush();
        reporter.Report(total, force: true);
        return total;
    }

    /// <summary>Partitions of the current layout; none when it cannot be read.</summary>
    private static IReadOnlyList<PartitionInfo> ReadOldPartitions(IBlockDevice device, long length)
    {
        try
        {
            var head = device.ReadBytes(0, (int)Math.Min(ImageInspector.HeadSize, length)) ?? [];
            return ImageInspector.Inspect(head, (o, c) => device.ReadBytes(o, c)).Partitions;
        }
        catch (Exception ex) when (ex is FlashToolException or IOException or ArgumentException)
        {
            return [];
        }
    }

    private static async Task<long> OverwriteAsync(
        ResilientBlockDevice device,
        long length,
        ErasePattern pattern,
        ulong seed,
        EraseOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunk = WriteImageOperation.AlignChunk(options.ChunkSize, device.SectorSize);
        var reporter = new ProgressReporter(progress, OperationPhase.Erasing, length);
        long position = 0;

        var written = await TransferPipeline.RunAsync(
            chunk,
            options.PipelineDepth,
            buffer =>
            {
                var count = (int)Math.Min(chunk, length - position);
                if (count <= 0)
                {
                    return 0;
                }

                FillPattern(buffer.Span[..count], position, pattern, seed);
                position += count;
                return count;
            },
            (buffer, count, offset) =>
            {
                device.Write(offset, buffer.Span[..count]);
                reporter.Report(offset + count);
            },
            cancellationToken).ConfigureAwait(false);

        device.Flush();
        reporter.Report(written, force: true);
        return written;
    }

    /// <summary>Reads the whole device back and compares it with the regenerated pattern.</summary>
    private static async Task VerifyAsync(
        ResilientBlockDevice device,
        long length,
        ErasePattern pattern,
        ulong seed,
        EraseOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunk = WriteImageOperation.AlignChunk(options.ChunkSize, device.SectorSize);
        var reporter = new ProgressReporter(progress, OperationPhase.Verifying, length);
        using var expected = new AlignedBuffer(chunk);
        long position = 0;

        await TransferPipeline.RunAsync(
            chunk,
            options.PipelineDepth,
            buffer =>
            {
                var count = (int)Math.Min(chunk, length - position);
                if (count <= 0)
                {
                    return 0;
                }

                device.Read(position, buffer.Span[..count]);
                position += count;
                return count;
            },
            (buffer, count, offset) =>
            {
                var mismatch = FindMismatch(buffer.Span[..count], offset, pattern, seed, expected.Span);
                if (mismatch >= 0)
                {
                    throw new VerificationFailedException(offset + mismatch);
                }

                reporter.Report(offset + count);
            },
            cancellationToken).ConfigureAwait(false);

        reporter.Report(length, force: true);
    }

    /// <summary>
    /// Fills a buffer with the pattern as it appears at <paramref name="offset"/> of the device.
    /// Random data depends only on the seed and the position, so it can be regenerated for checking.
    /// </summary>
    internal static void FillPattern(Span<byte> buffer, long offset, ErasePattern pattern, ulong seed)
    {
        switch (pattern)
        {
            case ErasePattern.Zeros:
                buffer.Clear();
                break;

            case ErasePattern.Ones:
                buffer.Fill(0xFF);
                break;

            default:
                if (offset % sizeof(ulong) != 0 || buffer.Length % sizeof(ulong) != 0)
                {
                    throw new ArgumentException("Random pattern ranges must be 8-byte aligned.", nameof(offset));
                }

                var words = MemoryMarshal.Cast<byte, ulong>(buffer);
                var index = (ulong)offset / sizeof(ulong);
                for (var i = 0; i < words.Length; i++)
                {
                    words[i] = SplitMix64(seed + ((index + (ulong)i) * 0x9E3779B97F4A7C15));
                }

                break;
        }
    }

    /// <summary>Index of the first byte that differs from the pattern, or -1.</summary>
    internal static int FindMismatch(ReadOnlySpan<byte> actual, long offset, ErasePattern pattern, ulong seed, Span<byte> scratch)
    {
        switch (pattern)
        {
            case ErasePattern.Zeros:
                return actual.IndexOfAnyExcept((byte)0);

            case ErasePattern.Ones:
                return actual.IndexOfAnyExcept((byte)0xFF);

            default:
                var expected = scratch[..actual.Length];
                FillPattern(expected, offset, pattern, seed);
                var common = actual.CommonPrefixLength(expected);
                return common == actual.Length ? -1 : common;
        }
    }

    private static void Format(ResilientBlockDevice device, EraseLayout layout, string label, CancellationToken cancellationToken)
    {
        var sectorSize = device.SectorSize;
        var firstLba = layout.PartitionOffset / sectorSize;
        var sectors = layout.PartitionLength / sectorSize;
        var ids = RandomNumberGenerator.GetBytes(8);
        var serial = BitConverter.ToUInt32(ids, 0);

        // The file system first, the partition table that points to it last.
        if (layout.Fat32 is { } fat)
        {
            Fat32Formatter.Format(device, layout.PartitionOffset, fat, label, serial, cancellationToken);
        }
        else if (layout.ExFat is { } exFat)
        {
            ExFatFormatter.Format(device, layout.PartitionOffset, exFat, label, serial, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (layout.Table == PartitionTableType.Gpt)
        {
            PartitionTableWriter.WriteGpt(
                device,
                firstLba,
                firstLba + sectors - 1,
                PartitionTableWriter.BasicDataPartitionType,
                "Basic data partition",
                Guid.NewGuid(),
                Guid.NewGuid());
        }
        else
        {
            const byte fat32Lba = 0x0C;
            const byte exFatNtfs = 0x07;
            PartitionTableWriter.WriteMbr(device, firstLba, sectors, layout.FileSystem == FileSystemType.Fat32 ? fat32Lba : exFatNtfs, BitConverter.ToUInt32(ids, 4));
        }
    }

    private static ulong SplitMix64(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
