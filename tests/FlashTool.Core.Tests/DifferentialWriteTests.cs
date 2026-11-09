using FlashTool.Core.Bmap;
using FlashTool.Core.Containers;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;
using Xunit.Abstractions;

namespace FlashTool.Core.Tests;

/// <summary>Writing only what differs from the device, against devices simulated in memory.</summary>
public class DifferentialWriteTests(ITestOutputHelper output)
{
    private const int MiB = 1024 * 1024;
    private const int KiB = 1024;

    [Fact]
    public async Task Only_changed_blocks_are_written_and_the_result_is_the_image()
    {
        using var dir = new TempDir();
        var image = Image(64 * MiB);
        var device = new MemoryDevice(image.Length) { Content = (byte[])image.Clone() };
        foreach (var offset in new[] { 10 * MiB, (30 * MiB) + 1000, (50 * MiB) + 70_000 })
        {
            new Random(offset).NextBytes(image.AsSpan(offset, 10 * KiB));
        }

        var result = await WriteAsync(dir, image, device);

        Assert.Equal(image, device.Content);
        Assert.True(result.Verified);
        var stats = result.Differential!;
        output.WriteLine($"compared {stats.BytesCompared:N0}, matched {stats.MatchedFraction:P2}, written {stats.BytesWritten:N0}, device {result.DeviceBytesWritten:N0}");
        Assert.True(stats.BytesWritten <= 3 * 2 * DifferentialWriter.CompareBlock, stats.BytesWritten.ToString());
        Assert.True(stats.MatchedFraction > 0.99);
        Assert.Equal(0, stats.TimesComparingStopped);

        // Three runs past the first chunk, which is always blanked and written last.
        Assert.Equal(3, device.WriteLog.Count(w => w.Offset >= 4 * MiB));
    }

    [Fact]
    public async Task Close_differences_are_written_as_one_request()
    {
        using var dir = new TempDir();
        var image = Image(64 * MiB);
        var device = new MemoryDevice(image.Length) { Content = (byte[])image.Clone() };
        image[20 * MiB] ^= 1;
        image[(20 * MiB) + (192 * KiB)] ^= 1;
        image[40 * MiB] ^= 1;
        image[41 * MiB] ^= 1;

        await WriteAsync(dir, image, device);

        var writes = device.WriteLog.Where(w => w.Offset >= 4 * MiB).ToList();
        Assert.Equal(
            [(20L * MiB, (192 * KiB) + (64 * KiB)), (40L * MiB, 64 * KiB), (41L * MiB, 64 * KiB)],
            writes);
        Assert.Equal(image, device.Content);
    }

    [Fact]
    public async Task Comparing_stops_where_nothing_matches()
    {
        using var dir = new TempDir();
        var image = Image(192 * MiB);
        var device = new MemoryDevice(image.Length) { Content = Image(192 * MiB, seed: 99) };

        var result = await WriteAsync(dir, image, device, windowMiB: 32);

        var stats = result.Differential!;
        output.WriteLine($"compared {stats.BytesCompared / MiB} MiB, without compare {stats.BytesWrittenWithoutCompare / MiB} MiB, stops {stats.TimesComparingStopped}");
        Assert.Equal(image, device.Content);
        Assert.True(stats.TimesComparingStopped >= 1);
        Assert.True(stats.BytesCompared < image.Length / 2);
        Assert.True(stats.BytesWrittenWithoutCompare > image.Length / 2);
        Assert.True(result.Verified);
    }

    [Fact]
    public async Task Comparing_comes_back_where_data_matches_again()
    {
        using var dir = new TempDir();
        var image = Image(256 * MiB);
        var old = (byte[])image.Clone();
        new Random(5).NextBytes(old.AsSpan(64 * MiB, 64 * MiB));
        var device = new MemoryDevice(image.Length) { Content = old };

        var result = await WriteAsync(dir, image, device, windowMiB: 16, chunk: MiB);

        var stats = result.Differential!;
        output.WriteLine($"matched {stats.BytesMatched / MiB} MiB, stops {stats.TimesComparingStopped}");
        Assert.Equal(image, device.Content);
        Assert.True(stats.TimesComparingStopped >= 1);

        // The first 64 MiB and most of the last 128 MiB were already there.
        Assert.True(stats.BytesMatched > 150L * MiB, stats.BytesMatched.ToString());
    }

    [Fact]
    public async Task A_failing_compare_read_just_writes_that_piece()
    {
        using var dir = new TempDir();
        var image = Image(32 * MiB);
        // Fails the compare read and its one retry; reading it back for verification works.
        var device = new MemoryDevice(image.Length) { Content = (byte[])image.Clone(), FailingReads = (12 * MiB, (12 * MiB) + 512, 2) };
        image[(12 * MiB) + 100] ^= 0xFF;

        // No question to the user about it: the policy would throw if it were asked.
        var result = await WriteAsync(dir, image, device, retry: new IoRetryPolicy
        {
            AutomaticAttempts = 1,
            AskUser = (_, _) => throw new InvalidOperationException("The user was asked."),
        });

        Assert.Equal(image, device.Content);
        Assert.True(result.Verified);
        Assert.Contains(device.WriteLog, w => w.Offset == 12 * MiB && w.Length == 4 * MiB);
    }

    [Fact]
    public async Task Counterfeit_device_is_still_caught_by_the_full_verification()
    {
        using var dir = new TempDir();
        var image = Image(128 * MiB);

        // Half the size it claims: the second half lands on the first. Its first half already holds the image.
        var device = new MemoryDevice(image.Length, realLength: 64 * MiB) { Content = image[..(64 * MiB)] };

        var error = await Assert.ThrowsAsync<VerificationFailedException>(() => WriteAsync(dir, image, device));
        output.WriteLine(error.Message);
    }

    [Fact]
    public async Task With_a_bmap_only_its_plan_is_compared()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 40000, trailingSectors: 2000);

        // A hole in the middle of the second partition: zeros in the image, old data on the device.
        image.AsSpan(8 * MiB, 4 * MiB).Clear();
        var bmap = BuildBmap(image);
        var device = new MemoryDevice(image.Length) { Content = (byte[])image.Clone() };
        device.Content.AsSpan(8 * MiB, 4 * MiB).Fill(0xEE);
        device.Content[6 * MiB] ^= 1;

        var result = await WriteAsync(dir, image, device, bmap: bmap);

        Assert.True(result.Verified);
        Assert.True(result.Differential!.BytesWritten <= DifferentialWriter.CompareBlock);
        Assert.True(device.Content.AsSpan(8 * MiB, 4 * MiB).IndexOfAnyExcept((byte)0xEE) < 0, "A hole of the .bmap was written.");
        Assert.Equal(image.AsSpan(0, 8 * MiB).ToArray(), device.Content.AsSpan(0, 8 * MiB).ToArray());
    }

    [Fact]
    public void Shown_speed_follows_the_device_work_not_the_progress()
    {
        var reports = new List<OperationProgress>();
        long deviceBytes = 0;
        var reporter = new ProgressReporter(new SyncProgress(reports.Add), OperationPhase.Writing, 1000L * MiB, null, () => deviceBytes);

        reporter.Report(0, force: true);
        Thread.Sleep(300);

        // Half the image went by, but only 1 MiB of it had to be written.
        deviceBytes = MiB;
        reporter.Report(500L * MiB, force: true);

        var last = reports[^1];
        Assert.InRange(last.BytesPerSecond, 0.5 * MiB, 4.0 * MiB);
        Assert.NotNull(last.Remaining);
        Assert.True(last.Remaining < TimeSpan.FromSeconds(5));
    }

    private static async Task<WriteImageResult> WriteAsync(TempDir dir, byte[] image, MemoryDevice device, int windowMiB = 128, int chunk = 4 * MiB, IoRetryPolicy? retry = null, BmapFile? bmap = null)
    {
        File.WriteAllBytes(dir.File("image.img"), image);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("image.img")));
        return await WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => device,
            new WriteImageOptions
            {
                DifferentialWrite = true,
                DifferentialWindowBytes = (long)windowMiB * MiB,
                ChunkSize = chunk,
                Verify = true,
                RequireRecognizedImage = false,
                IoRetry = retry ?? new IoRetryPolicy { AutomaticAttempts = 1 },
                Bmap = bmap,
            },
            null,
            CancellationToken.None);
    }

    /// <summary>Random data with a valid MBR, so it passes as a disk image.</summary>
    private static byte[] Image(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        data.AsSpan(446, 64).Clear();
        TestImages.WriteMbrEntry(data, 0, 0x83, 2048, (uint)((length / 512) - 2048));
        data[510] = 0x55;
        data[511] = 0xAA;
        return data;
    }

    private static BmapFile BuildBmap(byte[] image)
    {
        var holes = new List<(long, long)>();
        for (long offset = 0; offset < image.Length; offset += BmapBuilder.BlockSize)
        {
            if (image.AsSpan((int)offset, BmapBuilder.BlockSize).IndexOfAnyExcept((byte)0) < 0)
            {
                holes.Add((offset, offset + BmapBuilder.BlockSize));
            }
        }

        using var builder = new BmapBuilder(image.Length, holes);
        builder.Append(image);
        return BmapFile.Parse(builder.ToBytes());
    }

    private sealed class SyncProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }

    /// <summary>A device in memory that logs its writes; it can fail one read, or wrap addresses past its real size.</summary>
    private sealed class MemoryDevice(long length, long? realLength = null) : IBlockDevice
    {
        private int _failures;

        public byte[] Content { get; set; } = new byte[realLength ?? length];

        public List<(long Offset, int Length)> WriteLog { get; } = [];

        /// <summary>Reads touching [Start, End) fail this many times, then work.</summary>
        public (long Start, long End, int Times)? FailingReads { get; init; }

        public long Length => length;

        public int SectorSize => 512;

        public bool CanWrite => true;

        public void Read(long offset, Span<byte> buffer)
        {
            var end = offset + buffer.Length;
            if (FailingReads is { } fail && _failures < fail.Times && fail.Start < end && offset < fail.End)
            {
                _failures++;
                throw new IOException("The request could not be performed because of an I/O device error.");
            }

            for (var done = 0; done < buffer.Length;)
            {
                var (at, count) = Map(offset + done, buffer.Length - done);
                Content.AsSpan((int)at, count).CopyTo(buffer[done..]);
                done += count;
            }
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            WriteLog.Add((offset, buffer.Length));
            for (var done = 0; done < buffer.Length;)
            {
                var (at, count) = Map(offset + done, buffer.Length - done);
                buffer.Slice(done, count).CopyTo(Content.AsSpan((int)at, count));
                done += count;
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }

        private (long At, int Count) Map(long offset, int count)
        {
            var real = Content.LongLength;
            var at = offset % real;
            return (at, (int)Math.Min(count, real - at));
        }
    }
}
