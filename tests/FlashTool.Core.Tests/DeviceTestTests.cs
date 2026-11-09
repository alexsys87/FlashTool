using System.Runtime.InteropServices;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using Xunit.Abstractions;

namespace FlashTool.Core.Tests;

/// <summary>The device test against simulated media: healthy, counterfeit, slow and with unreadable sectors.</summary>
public class DeviceTestTests(ITestOutputHelper output)
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void Pattern_words_tell_their_offset()
    {
        var buffer = new byte[64];
        var random = new Random(1);
        for (var i = 0; i < 1000; i++)
        {
            var offset = random.NextInt64(1L << 37) & ~7L;
            EraseDeviceOperation.FillPattern(buffer, offset, ErasePattern.Random, 0x1234_5678_9ABC_DEF0);
            Assert.True(PatternDecoder.TryDecode(MemoryMarshal.Read<ulong>(buffer), 0x1234_5678_9ABC_DEF0, 1L << 40, out var decoded));
            Assert.Equal(offset, decoded);
        }

        // Zeros or erased flash are not the pattern of any offset.
        Assert.False(PatternDecoder.TryDecode(0, 42, 1L << 40, out _));
        Assert.False(PatternDecoder.TryDecode(ulong.MaxValue, 42, 1L << 40, out _));
    }

    [Fact]
    public async Task Healthy_device_passes_both_passes_with_chart_points()
    {
        var device = new MemoryDevice(64 * MiB);
        var points = new List<DeviceTestSample>();
        var result = await RunAsync(device, DeviceTestMode.WriteVerify, o => o with { SampleReported = new SyncProgress<DeviceTestSample>(points.Add) });

        Assert.Equal(DeviceTestVerdict.Healthy, result.Verdict);
        Assert.Equal(0, result.BadAreaCount);
        Assert.Equal([DeviceTestPhase.Write, DeviceTestPhase.Read], result.Passes.Select(p => p.Phase));
        Assert.All(result.Passes, p => Assert.Equal(64 * MiB, p.Bytes));
        Assert.Equal(16, result.Samples.Count(s => s.Phase == DeviceTestPhase.Write));
        Assert.Equal(16, result.Samples.Count(s => s.Phase == DeviceTestPhase.Read));
        Assert.Equal(result.Samples.Count, points.Count);
    }

    [Fact]
    public async Task Read_test_changes_nothing()
    {
        var device = new MemoryDevice(32 * MiB);
        new Random(2).NextBytes(device.Physical);
        var before = (byte[])device.Physical.Clone();

        var result = await RunAsync(device, DeviceTestMode.Read);

        Assert.Equal(DeviceTestVerdict.Healthy, result.Verdict);
        Assert.Equal(DeviceTestPhase.Read, Assert.Single(result.Passes).Phase);
        Assert.Equal(before, device.Physical);
        Assert.Equal(0, device.Writes);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(96)]
    public async Task Full_test_finds_wrapping_counterfeit_and_its_real_size(int realMiB)
    {
        var device = new MemoryDevice(256 * MiB, realMiB * MiB, FakeKind.Wraps);
        var result = await RunAsync(device, DeviceTestMode.WriteVerify);

        output.WriteLine($"{result.Verdict}, real {result.RealCapacity / MiB} MiB, bad {result.BadBytes / MiB} MiB");
        Assert.Equal(DeviceTestVerdict.FakeCapacity, result.Verdict);
        Assert.True(result.AddressesWrap);
        Assert.Equal(realMiB * MiB, result.RealCapacity);
    }

    [Fact]
    public async Task Full_test_finds_counterfeit_that_drops_data_past_its_size()
    {
        var device = new MemoryDevice(256 * MiB, 100 * MiB, FakeKind.Drops);
        var result = await RunAsync(device, DeviceTestMode.WriteVerify);

        Assert.Equal(DeviceTestVerdict.FakeCapacity, result.Verdict);
        Assert.False(result.AddressesWrap);
        Assert.Equal(100 * MiB, result.RealCapacity);
    }

    [Theory]
    [InlineData(64, FakeKind.Wraps)]
    [InlineData(96, FakeKind.Wraps)]
    [InlineData(100, FakeKind.Drops)]
    public async Task Quick_probe_finds_counterfeits_without_writing_everything(int realMiB, FakeKind kind)
    {
        var device = new MemoryDevice(256 * MiB, realMiB * MiB, kind);
        var result = await RunAsync(device, DeviceTestMode.QuickProbe);

        output.WriteLine($"{kind} {realMiB} MiB: {result.Verdict}, real {result.RealCapacity / (double)MiB:0.#} MiB, wrote {device.BytesWritten / MiB} MiB");
        Assert.Equal(DeviceTestVerdict.FakeCapacity, result.Verdict);
        Assert.InRange(result.RealCapacity!.Value, (realMiB - 1) * MiB, (realMiB + 1) * MiB);
        Assert.True(device.BytesWritten < 256 * MiB / 2);
    }

    [Fact]
    public async Task Quick_probe_accepts_a_genuine_device()
    {
        var result = await RunAsync(new MemoryDevice(256 * MiB), DeviceTestMode.QuickProbe);
        Assert.Equal(DeviceTestVerdict.Healthy, result.Verdict);
        Assert.Null(result.RealCapacity);
    }

    [Theory]
    [InlineData(DeviceTestMode.Read)]
    [InlineData(DeviceTestMode.WriteVerify)]
    public async Task Unreadable_sectors_are_listed_and_the_test_goes_on(DeviceTestMode mode)
    {
        var device = new MemoryDevice(64 * MiB) { Unreadable = [(10 * MiB, (10 * MiB) + (128 * 1024)), (40 * MiB, 41 * MiB)] };
        var result = await RunAsync(device, mode);

        Assert.Equal(DeviceTestVerdict.BadAreas, result.Verdict);
        Assert.Equal(
            [new DeviceBadArea(10 * MiB, 128 * 1024, DeviceBadAreaKind.ReadError), new DeviceBadArea(40 * MiB, MiB, DeviceBadAreaKind.ReadError)],
            result.BadAreas);
        Assert.Equal(64 * MiB, result.Passes.Last().Bytes);
    }

    [Fact]
    public async Task Slow_area_is_found()
    {
        var device = new MemoryDevice(64 * MiB) { Slow = (32 * MiB, 48 * MiB) };
        var result = await RunAsync(device, DeviceTestMode.Read);

        // Memory is fast enough that a pause of the test host elsewhere can show up too; the real one must be exact.
        var slow = Assert.Single(result.SlowAreas, a => a.Offset < 48 * MiB && 32 * MiB < a.Offset + a.Length);
        Assert.Equal(DeviceTestPhase.Read, slow.Phase);
        Assert.Equal(32 * MiB, slow.Offset);
        Assert.Equal(16 * MiB, slow.Length);
        Assert.Equal(DeviceTestVerdict.Healthy, result.Verdict);
    }

    [Fact]
    public async Task Random_access_measures_reads_and_writes()
    {
        var device = new MemoryDevice(64 * MiB);
        var result = await RunAsync(device, DeviceTestMode.RandomAccess, o => o with { RandomDuration = TimeSpan.FromMilliseconds(300) });

        Assert.Equal([DeviceTestPhase.Read, DeviceTestPhase.Write], result.RandomAccess.Select(r => r.Phase));
        Assert.All(result.RandomAccess, r =>
        {
            Assert.True(r.Operations > 0);
            Assert.True(r.Iops > 0);
            Assert.Equal(0, r.Errors);
        });
        Assert.True(device.Writes > 0);
    }

    [Fact]
    public async Task Destructive_test_refuses_a_read_only_device()
    {
        var error = await Assert.ThrowsAsync<FlashToolException>(() => RunAsync(new MemoryDevice(16 * MiB) { ReadOnly = true }, DeviceTestMode.WriteVerify));
        Assert.Equal(FlashErrorCode.DeviceWriteProtected, error.Code);
    }

    [Fact]
    public async Task Report_is_csv_with_every_table()
    {
        var device = new MemoryDevice(64 * MiB, 32 * MiB, FakeKind.Wraps);
        var result = await RunAsync(device, DeviceTestMode.WriteVerify);
        var csv = DeviceTestReport.ToCsv(result, "Card, \"test\"");
        var lines = csv.Split('\n');

        Assert.Contains("# device,\"Card, \"\"test\"\"\"", lines);
        Assert.Contains("# verdict,FakeCapacity", lines);
        Assert.Contains($"# real_capacity_bytes,{32 * MiB}", lines);
        Assert.Contains("phase,offset_bytes,length_bytes,bytes_per_second,max_latency_ms", lines);
        // The chart points table runs from its header to the next blank line.
        var header = Array.IndexOf(lines, "phase,offset_bytes,length_bytes,bytes_per_second,max_latency_ms");
        var points = lines.Skip(header + 1).TakeWhile(l => l.Length > 0).ToList();
        Assert.Equal(result.Samples.Count, points.Count);
        Assert.All(points, l => Assert.Matches("^(write|read),[0-9]+,[0-9]+,[0-9.]+,[0-9.]+$", l));
        Assert.Contains(lines, l => l.StartsWith("wrong_data,", StringComparison.Ordinal));
    }

    private static Task<DeviceTestResult> RunAsync(MemoryDevice device, DeviceTestMode mode, Func<DeviceTestOptions, DeviceTestOptions>? configure = null)
    {
        var options = new DeviceTestOptions
        {
            Mode = mode,
            SampleCount = 16,
            IoRetry = new IoRetryPolicy { AutomaticAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(1) },
        };
        return DeviceTestOperation.RunAsync(() => device, configure?.Invoke(options) ?? options, null, CancellationToken.None);
    }

    public enum FakeKind
    {
        Genuine,

        /// <summary>Addresses past the real size wrap around to the start.</summary>
        Wraps,

        /// <summary>Writes past the real size vanish, reads there return zeros.</summary>
        Drops,
    }

    /// <summary>A device in memory that can pretend to be larger than it is, be slow somewhere, or have unreadable sectors.</summary>
    private sealed class MemoryDevice : IBlockDevice
    {
        private readonly long _length;
        private readonly FakeKind _kind;

        public MemoryDevice(long length, long? real = null, FakeKind kind = FakeKind.Genuine)
        {
            _length = length;
            _kind = kind;
            Physical = new byte[real ?? length];
        }

        public byte[] Physical { get; }

        public List<(long Start, long End)> Unreadable { get; init; } = [];

        public (long Start, long End)? Slow { get; init; }

        public bool ReadOnly { get; init; }

        public int Writes { get; private set; }

        public long BytesWritten { get; private set; }

        public long Length => _length;

        public int SectorSize => 512;

        public bool CanWrite => !ReadOnly;

        public void Read(long offset, Span<byte> buffer)
        {
            var end = offset + buffer.Length;
            if (Unreadable.Any(u => u.Start < end && offset < u.End))
            {
                throw new IOException("The request could not be performed because of an I/O device error.");
            }

            Delay(offset, buffer.Length);
            for (var done = 0; done < buffer.Length;)
            {
                var (physical, count) = Map(offset + done, buffer.Length - done);
                if (physical < 0)
                {
                    buffer.Slice(done, count).Clear();
                }
                else
                {
                    Physical.AsSpan((int)physical, count).CopyTo(buffer.Slice(done, count));
                }

                done += count;
            }
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            Writes++;
            BytesWritten += buffer.Length;
            Delay(offset, buffer.Length);
            for (var done = 0; done < buffer.Length;)
            {
                var (physical, count) = Map(offset + done, buffer.Length - done);
                if (physical >= 0)
                {
                    buffer.Slice(done, count).CopyTo(Physical.AsSpan((int)physical, count));
                }

                done += count;
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }

        /// <summary>Where a logical offset lives (-1: nowhere) and how many bytes stay contiguous from there.</summary>
        private (long Physical, int Count) Map(long offset, int count)
        {
            var real = Physical.LongLength;
            if (offset < real || _kind == FakeKind.Genuine)
            {
                return (offset, (int)Math.Min(count, real - offset));
            }

            return _kind == FakeKind.Wraps
                ? (offset % real, (int)Math.Min(count, real - (offset % real)))
                : (-1, count);
        }

        private void Delay(long offset, int count)
        {
            if (Slow is { } slow && slow.Start < offset + count && offset < slow.End)
            {
                // About 100 MB/s there, against memory speed everywhere else.
                Thread.Sleep(Math.Max(1, count / (100 * 1024 * 1024 / 1000)));
            }
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
