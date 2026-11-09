using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FlashTool.Core.IO;

namespace FlashTool.Core.Operations;

/// <summary>
/// Measures a device and checks that it keeps what is written to it: sequential speed by offset,
/// request times, unreadable areas, counterfeit capacity, random 4 KiB access. Errors do not stop
/// the test; they become part of the result.
/// </summary>
/// <remarks>
/// The destructive modes write the pattern of <see cref="EraseDeviceOperation"/>: every 8-byte word
/// depends on its offset. The pattern can be inverted, so data read from the wrong place reveals which
/// offset it was written for. That tells a counterfeit device whose addresses wrap around (and its real
/// size) from one that drops what is written past its real size, and both from scattered bad areas.
/// </remarks>
public static class DeviceTestOperation
{
    /// <summary>Granularity of error and data checks inside a chunk.</summary>
    internal const int SubBlock = 64 * 1024;

    /// <summary>Size of every probe of the quick test.</summary>
    internal const int ProbeSize = 1024 * 1024;

    /// <summary>Written between the probes and reading them back, so they no longer sit in the device's cache.</summary>
    internal const int CacheFlushSize = 64 * 1024 * 1024;

    public static async Task<DeviceTestResult> RunAsync(
        Func<IBlockDevice> openDevice,
        DeviceTestOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openDevice);
        ArgumentNullException.ThrowIfNull(options);
        var clock = Stopwatch.StartNew();
        progress?.Report(new OperationProgress(OperationPhase.Preparing, 0, null, 0, 0, null));

        using var device = new ResilientBlockDevice(await Task.Run(openDevice, cancellationToken).ConfigureAwait(false), options.IoRetry, cancellationToken);
        if (options.IsDestructive && !device.CanWrite)
        {
            throw new FlashToolException(FlashErrorCode.DeviceWriteProtected, "The device cannot be written.");
        }

        var seed = options.PatternSeed ?? BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var run = new TestRun(device, options, seed, progress, cancellationToken);
        switch (options.Mode)
        {
            case DeviceTestMode.Read:
                await run.ReadPassAsync(verify: false).ConfigureAwait(false);
                break;
            case DeviceTestMode.WriteVerify:
                await run.WritePassAsync().ConfigureAwait(false);
                await run.ReadPassAsync(verify: true).ConfigureAwait(false);
                break;
            case DeviceTestMode.QuickProbe:
                await Task.Run(run.QuickProbe, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await Task.Run(run.RandomAccess, cancellationToken).ConfigureAwait(false);
                break;
        }

        progress?.Report(new OperationProgress(OperationPhase.Completed, 0, null, 1, 0, TimeSpan.Zero));
        return run.Result(clock.Elapsed);
    }


    private sealed class TestRun
    {
        private readonly ResilientBlockDevice _device;
        private readonly DeviceTestOptions _options;
        private readonly ulong _seed;
        private readonly IProgress<OperationProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly long _length;
        private readonly int _chunk;
        private readonly BadAreaList _bad;
        private readonly List<DeviceTestSample> _samples = [];
        private readonly List<DeviceTestPassStats> _passes = [];
        private readonly List<RandomAccessStats> _random = [];
        private readonly ConcurrentBag<long> _aliasShifts = [];
        private readonly List<(long Start, long End)> _writeFailures = [];
        private long? _realCapacity;
        private bool _probeVerdictFake;

        public TestRun(ResilientBlockDevice device, DeviceTestOptions options, ulong seed, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            _device = device;
            _options = options;
            _seed = seed;
            _progress = progress;
            _ct = ct;
            _length = StreamExtensions.AlignDown(device.Length, device.SectorSize);
            _chunk = WriteImageOperation.AlignChunk(options.ChunkSize, device.SectorSize);
            _bad = new BadAreaList(options.MaxReportedAreas);
        }

        private double Seconds(long start) => (_options.Clock() - start) / _options.ClockFrequency;

        private long SampleBucket => Math.Max(_chunk, StreamExtensions.AlignUp(_length / Math.Max(1, _options.SampleCount), _chunk));

        // ---- Sequential passes ----

        public async Task WritePassAsync()
        {
            var sampler = new Sampler(DeviceTestPhase.Write, SampleBucket, _options.SampleReported);
            var reporter = new ProgressReporter(_progress, OperationPhase.Writing, _length);
            long position = 0;
            var previousFailed = false;

            await TransferPipeline.RunAsync(
                _chunk,
                _options.PipelineDepth,
                buffer =>
                {
                    var count = (int)Math.Min(_chunk, _length - position);
                    if (count <= 0)
                    {
                        return 0;
                    }

                    EraseDeviceOperation.FillPattern(buffer.Span[..count], position, ErasePattern.Random, _seed);
                    position += count;
                    return count;
                },
                (buffer, count, offset) =>
                {
                    var start = _options.Clock();
                    previousFailed = WriteChunk(offset, buffer.Span[..count], previousFailed);
                    sampler.Add(offset, count, Seconds(start));
                    reporter.Report(offset + count);
                },
                _ct).ConfigureAwait(false);

            _device.Flush();
            reporter.Report(_length, force: true);
            Finish(sampler);
        }

        public async Task ReadPassAsync(bool verify)
        {
            var sampler = new Sampler(DeviceTestPhase.Read, SampleBucket, _options.SampleReported);
            var reporter = new ProgressReporter(_progress, verify ? OperationPhase.Verifying : OperationPhase.Reading, _length);
            var unreadable = new ConcurrentDictionary<long, List<(int Start, int End)>>();
            using var expected = new AlignedBuffer(_chunk);
            long position = 0;
            var previousFailed = false;
            var writeFailureIndex = 0;

            await TransferPipeline.RunAsync(
                _chunk,
                _options.PipelineDepth,
                buffer =>
                {
                    var count = (int)Math.Min(_chunk, _length - position);
                    if (count <= 0)
                    {
                        return 0;
                    }

                    var start = _options.Clock();
                    previousFailed = ReadChunk(position, buffer.Span[..count], previousFailed, unreadable);
                    sampler.Add(position, count, Seconds(start));
                    position += count;
                    return count;
                },
                (buffer, count, offset) =>
                {
                    unreadable.TryRemove(offset, out var skip);
                    if (verify)
                    {
                        CheckPattern(offset, buffer.Span[..count], skip, expected.Span, ref writeFailureIndex);
                    }

                    reporter.Report(offset + count);
                },
                _ct).ConfigureAwait(false);

            reporter.Report(_length, force: true);
            Finish(sampler);
        }

        /// <summary>Reads a chunk; on errors, a sub-block at a time. Returns true when nothing of it could be read.</summary>
        private bool ReadChunk(long offset, Span<byte> data, bool previousFailed, ConcurrentDictionary<long, List<(int, int)>> unreadable)
        {
            if (TryRead(offset, data))
            {
                return false;
            }

            var failed = new List<(int, int)>();

            // Inside a long unreadable stretch, splitting every chunk would take ages.
            if (previousFailed)
            {
                data.Clear();
                failed.Add((0, data.Length));
                _bad.Add(offset, data.Length, DeviceBadAreaKind.ReadError);
                unreadable[offset] = failed;
                return true;
            }

            for (var at = 0; at < data.Length; at += SubBlock)
            {
                var length = Math.Min(SubBlock, data.Length - at);
                var part = data.Slice(at, length);
                var partOffset = offset + at;
                if (!TryRead(partOffset, part))
                {
                    part.Clear();
                    failed.Add((at, at + length));
                    _bad.Add(partOffset, length, DeviceBadAreaKind.ReadError);
                }
            }

            unreadable[offset] = failed;
            return failed.Count > 0 && failed.Sum(f => f.Item2 - f.Item1) == data.Length;
        }

        private bool WriteChunk(long offset, ReadOnlySpan<byte> data, bool previousFailed)
        {
            if (TryWrite(offset, data))
            {
                return false;
            }

            if (previousFailed)
            {
                RecordWriteFailure(offset, data.Length);
                return true;
            }

            var allFailed = true;
            for (var at = 0; at < data.Length; at += SubBlock)
            {
                var length = Math.Min(SubBlock, data.Length - at);
                var partOffset = offset + at;
                if (TryWrite(partOffset, data.Slice(at, length)))
                {
                    allFailed = false;
                }
                else
                {
                    RecordWriteFailure(partOffset, length);
                }
            }

            return allFailed;
        }

        private void RecordWriteFailure(long offset, int length)
        {
            _bad.Add(offset, length, DeviceBadAreaKind.WriteError);
            lock (_writeFailures)
            {
                _writeFailures.Add((offset, offset + length));
            }
        }

        /// <summary>Compares a chunk with the pattern, a sub-block at a time, and works out where wrong data came from.</summary>
        private void CheckPattern(long offset, ReadOnlySpan<byte> actual, List<(int Start, int End)>? unreadable, Span<byte> scratch, ref int writeFailureIndex)
        {
            for (var at = 0; at < actual.Length; at += SubBlock)
            {
                var length = Math.Min(SubBlock, actual.Length - at);
                var subOffset = offset + at;
                if (unreadable?.Any(u => u.Start < at + length && at < u.End) == true || OverlapsWriteFailure(subOffset, length, ref writeFailureIndex))
                {
                    continue;
                }

                var part = actual.Slice(at, length);
                var expected = scratch[..length];
                EraseDeviceOperation.FillPattern(expected, subOffset, ErasePattern.Random, _seed);
                var same = part.CommonPrefixLength(expected);
                if (same == length)
                {
                    continue;
                }

                _bad.Add(subOffset, length, DeviceBadAreaKind.WrongData);
                var wordAt = same & ~7;
                if (wordAt + 8 <= length
                    && PatternDecoder.TryDecode(MemoryMarshal.Read<ulong>(part[wordAt..]), _seed, _length, out var origin)
                    && origin != subOffset + wordAt)
                {
                    // Make sure the whole sub-block is that other offset's data, not a coincidence.
                    var shift = origin - (subOffset + wordAt);
                    var source = subOffset + shift;
                    if (source >= 0 && source + length <= _length)
                    {
                        EraseDeviceOperation.FillPattern(expected, source, ErasePattern.Random, _seed);
                        if (part.SequenceEqual(expected))
                        {
                            _aliasShifts.Add(shift);
                        }
                    }
                }
            }
        }

        private bool OverlapsWriteFailure(long offset, int length, ref int index)
        {
            lock (_writeFailures)
            {
                while (index < _writeFailures.Count && _writeFailures[index].End <= offset)
                {
                    index++;
                }

                return index < _writeFailures.Count && _writeFailures[index].Start < offset + length;
            }
        }

        // ---- Quick probe ----

        public void QuickProbe()
        {
            var block = (int)StreamExtensions.AlignUp(ProbeSize, _device.SectorSize);
            if (_length < 4L * block)
            {
                throw new FlashToolException(FlashErrorCode.DeviceIoError, "The device is too small for the quick test.");
            }

            var flush = _length >= 1024L * 1024 * 1024 ? (Start: (long)block, End: (long)block + CacheFlushSize) : (Start: 0L, End: 0L);
            var positions = ProbePositions(block).Where(p => p + block <= flush.Start || p >= flush.End).ToList();

            // Ascending, so that on a device whose addresses wrap the last write to a place is from the highest offset.
            var reporter = new ProgressReporter(_progress, OperationPhase.Writing, positions.Count);
            using var buffer = new AlignedBuffer(block);
            var written = new List<long>();
            for (var i = 0; i < positions.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                if (WriteProbe(positions[i], buffer))
                {
                    written.Add(positions[i]);
                }

                reporter.Report(i + 1);
            }

            WriteCacheFlush(flush, buffer);

            var verifier = new ProgressReporter(_progress, OperationPhase.Verifying, written.Count);
            var states = new List<(long Offset, bool Good)>();
            for (var i = 0; i < written.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                states.Add((written[i], CheckProbe(written[i], buffer)));
                verifier.Report(i + 1);
            }

            if (!_aliasShifts.IsEmpty)
            {
                return;
            }

            var firstBad = states.FindIndex(s => !s.Good);
            if (firstBad <= 0)
            {
                // Nothing lost; or even the first probe was, which says more about a broken device than a fake one.
                return;
            }

            var tail = states.Skip(firstBad).ToList();
            if (tail.Count(s => !s.Good) < tail.Count * 0.9)
            {
                return;
            }

            // Everything from some point on is lost: narrow down where.
            var good = states[firstBad - 1].Offset;
            var lost = states[firstBad].Offset;
            for (var round = 0; round < 16 && lost - good > block; round++)
            {
                var middle = StreamExtensions.AlignDown(good + ((lost - good) / 2), block);
                if (middle <= good)
                {
                    break;
                }

                var kept = WriteProbe(middle, buffer);
                WriteCacheFlush(flush, buffer);
                if (kept && CheckProbe(middle, buffer))
                {
                    good = middle;
                }
                else
                {
                    lost = middle;
                }
            }

            _realCapacity = good + block;
            _probeVerdictFake = true;
        }

        /// <summary>The start, every power of two from 16 MiB (address masking shows there), an even spread, the end.</summary>
        private SortedSet<long> ProbePositions(int block)
        {
            var positions = new SortedSet<long> { 0, StreamExtensions.AlignDown(_length - block, block) };
            for (var p = 16L * 1024 * 1024; p + block <= _length; p *= 2)
            {
                positions.Add(p);
            }

            var count = Math.Max(2, _options.ProbeCount);
            for (var i = 1; i < count; i++)
            {
                positions.Add(StreamExtensions.AlignDown((long)(_length * (double)i / count), block));
            }

            return positions;
        }

        private bool WriteProbe(long offset, AlignedBuffer buffer)
        {
            EraseDeviceOperation.FillPattern(buffer.Span, offset, ErasePattern.Random, _seed);
            if (TryWrite(offset, buffer.Span))
            {
                return true;
            }

            _bad.Add(offset, buffer.Span.Length, DeviceBadAreaKind.WriteError);
            return false;
        }

        private void WriteCacheFlush((long Start, long End) flush, AlignedBuffer buffer)
        {
            for (var offset = flush.Start; offset < flush.End; offset += buffer.Span.Length)
            {
                _ct.ThrowIfCancellationRequested();
                EraseDeviceOperation.FillPattern(buffer.Span, offset, ErasePattern.Random, _seed);
                TryWrite(offset, buffer.Span);
            }

            TryFlush();
        }

        private bool CheckProbe(long offset, AlignedBuffer buffer)
        {
            var data = buffer.Span;
            if (!TryRead(offset, data))
            {
                _bad.Add(offset, data.Length, DeviceBadAreaKind.ReadError);
                return false;
            }

            using var expected = new AlignedBuffer(data.Length);
            var index = 0;
            var before = _bad.Count;
            CheckPattern(offset, data, null, expected.Span, ref index);
            return _bad.Count == before;
        }

        // ---- Random access ----

        public void RandomAccess()
        {
            var block = Math.Max(RandomAccessStats.BlockSize, _device.SectorSize);
            var blocks = _length / block;
            if (blocks < 2)
            {
                throw new FlashToolException(FlashErrorCode.DeviceIoError, "The device is too small for the random access test.");
            }

            var random = new Random((int)_seed);
            using var buffer = new AlignedBuffer(block);
            foreach (var phase in new[] { DeviceTestPhase.Read, DeviceTestPhase.Write })
            {
                if (phase == DeviceTestPhase.Write)
                {
                    random.NextBytes(buffer.Span);
                }

                var latencies = new List<double>();
                var errors = 0;
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                while (clock.Elapsed < _options.RandomDuration)
                {
                    _ct.ThrowIfCancellationRequested();
                    var offset = random.NextInt64(blocks) * block;
                    var start = _options.Clock();
                    var ok = phase == DeviceTestPhase.Read ? TryRead(offset, buffer.Span) : TryWrite(offset, buffer.Span);
                    latencies.Add(Seconds(start) * 1000);
                    if (!ok)
                    {
                        errors++;
                        _bad.Add(offset, block, phase == DeviceTestPhase.Read ? DeviceBadAreaKind.ReadError : DeviceBadAreaKind.WriteError);
                    }

                    if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(150))
                    {
                        lastReport = clock.Elapsed;
                        var fraction = Math.Min(1, clock.Elapsed / _options.RandomDuration);
                        _progress?.Report(new OperationProgress(
                            phase == DeviceTestPhase.Read ? OperationPhase.Reading : OperationPhase.Writing,
                            (long)latencies.Count * block,
                            null,
                            fraction,
                            latencies.Count * block / clock.Elapsed.TotalSeconds,
                            _options.RandomDuration - clock.Elapsed));
                    }
                }

                if (phase == DeviceTestPhase.Write)
                {
                    TryFlush();
                }

                var seconds = clock.Elapsed.TotalSeconds;
                latencies.Sort();
                _random.Add(new RandomAccessStats(
                    phase,
                    latencies.Count,
                    latencies.Count / seconds,
                    latencies.Count > 0 ? latencies.Average() : 0,
                    Percentile(latencies, 0.99),
                    errors));
            }
        }

        // ---- Result ----

        public DeviceTestResult Result(TimeSpan elapsed)
        {
            var verdict = DeviceTestVerdict.Healthy;
            var wraps = false;
            long? realCapacity = null;
            if (!_aliasShifts.IsEmpty)
            {
                var gcd = _aliasShifts.Aggregate(0L, (a, s) => (long)BigInteger.GreatestCommonDivisor(a, Math.Abs(s)));
                verdict = DeviceTestVerdict.FakeCapacity;
                wraps = true;
                realCapacity = gcd > 0 && gcd < _length ? gcd : null;
            }
            else if (_probeVerdictFake)
            {
                verdict = DeviceTestVerdict.FakeCapacity;
                realCapacity = _realCapacity;
            }
            else if (_options.Mode == DeviceTestMode.WriteVerify && LostFromSomePointOn() is { } capacity)
            {
                verdict = DeviceTestVerdict.FakeCapacity;
                realCapacity = capacity;
            }
            else if (_bad.Count > 0)
            {
                verdict = DeviceTestVerdict.BadAreas;
            }

            return new DeviceTestResult(_options.Mode, _length, verdict, elapsed)
            {
                Samples = _samples,
                Passes = _passes,
                SlowAreas = SlowAreas(),
                BadAreas = _bad.Areas,
                BadAreaCount = _bad.Count,
                BadBytes = _bad.Bytes,
                RealCapacity = realCapacity,
                AddressesWrap = wraps,
                RandomAccess = _random,
            };
        }

        /// <summary>
        /// A device that drops whatever is written past its real size: everything from the first bad
        /// offset to the end is bad. Returns that offset.
        /// </summary>
        private long? LostFromSomePointOn()
        {
            if (_bad.Count == 0 || _bad.FirstOffset <= 0)
            {
                return null;
            }

            var rest = _length - _bad.FirstOffset;
            var significant = rest >= Math.Min(64L * 1024 * 1024, _length / 4) || rest >= _length / 100;
            return significant && _bad.Bytes >= rest * 0.9 ? _bad.FirstOffset : null;
        }

        private void Finish(Sampler sampler)
        {
            sampler.Complete();
            _samples.AddRange(sampler.Samples);
            var latencies = sampler.Latencies;
            latencies.Sort();
            var speeds = sampler.Samples.Select(s => s.BytesPerSecond).ToList();
            _passes.Add(new DeviceTestPassStats(
                sampler.Phase,
                sampler.TotalBytes,
                sampler.TotalSeconds > 0 ? sampler.TotalBytes / sampler.TotalSeconds : 0,
                speeds.Count > 0 ? speeds.Min() : 0,
                speeds.Count > 0 ? speeds.Max() : 0,
                Percentile(latencies, 0.5),
                latencies.Count > 0 ? latencies[^1] : 0));
        }

        /// <summary>Runs of samples below half the median speed of their pass.</summary>
        private List<DeviceSlowArea> SlowAreas()
        {
            var areas = new List<DeviceSlowArea>();
            foreach (var group in _samples.GroupBy(s => s.Phase))
            {
                var samples = group.OrderBy(s => s.Offset).ToList();
                if (samples.Count < 3)
                {
                    continue;
                }

                var median = Percentile(samples.Select(s => s.BytesPerSecond).Order().ToList(), 0.5);
                foreach (var sample in samples.Where(s => s.BytesPerSecond < median * 0.5))
                {
                    if (areas.Count > 0 && areas[^1].Phase == group.Key && areas[^1].Offset + areas[^1].Length == sample.Offset)
                    {
                        areas[^1] = areas[^1] with { Length = areas[^1].Length + sample.Length };
                    }
                    else
                    {
                        areas.Add(new DeviceSlowArea(group.Key, sample.Offset, sample.Length));
                    }
                }
            }

            return areas;
        }

        // A device error (after the policy's retries) becomes false; the caller records the bad area.
        private bool TryRead(long offset, Span<byte> data)
        {
            try
            {
                _device.Read(offset, data);
                return true;
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                return false;
            }
        }

        private bool TryWrite(long offset, ReadOnlySpan<byte> data)
        {
            try
            {
                _device.Write(offset, data);
                return true;
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                return false;
            }
        }

        private bool TryFlush()
        {
            try
            {
                _device.Flush();
                return true;
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                return false;
            }
        }

        private static bool IsDeviceError(Exception ex) => ex is not OperationCanceledException && IoRetryPolicy.IsRetryable(ex);
    }

    private static double Percentile(List<double> sorted, double fraction)
        => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>Speed and request times of one pass, collected into chart points.</summary>
    private sealed class Sampler
    {
        private readonly long _bucket;
        private readonly IProgress<DeviceTestSample>? _sink;
        private long _start = -1;
        private long _bytes;
        private double _seconds;
        private double _maxLatency;

        public Sampler(DeviceTestPhase phase, long bucket, IProgress<DeviceTestSample>? sink)
        {
            Phase = phase;
            _bucket = bucket;
            _sink = sink;
        }

        public DeviceTestPhase Phase { get; }

        public List<DeviceTestSample> Samples { get; } = [];

        public List<double> Latencies { get; } = [];

        public long TotalBytes { get; private set; }

        public double TotalSeconds { get; private set; }

        public void Add(long offset, int bytes, double seconds)
        {
            if (_start < 0)
            {
                _start = offset;
            }

            _bytes += bytes;
            _seconds += seconds;
            _maxLatency = Math.Max(_maxLatency, seconds * 1000);
            Latencies.Add(seconds * 1000);
            TotalBytes += bytes;
            TotalSeconds += seconds;
            if (_bytes >= _bucket)
            {
                Emit();
            }
        }

        public void Complete()
        {
            if (_bytes > 0)
            {
                Emit();
            }
        }

        private void Emit()
        {
            var sample = new DeviceTestSample(Phase, _start, _bytes, _seconds > 0 ? _bytes / _seconds : 0, _maxLatency);
            Samples.Add(sample);
            _sink?.Report(sample);
            _start = -1;
            _bytes = 0;
            _seconds = 0;
            _maxLatency = 0;
        }
    }

    /// <summary>Bad areas, adjacent ones of the same kind merged; thread-safe.</summary>
    private sealed class BadAreaList
    {
        private readonly int _max;
        private readonly List<DeviceBadArea> _areas = [];
        private DeviceBadArea? _open;

        public BadAreaList(int max) => _max = max;

        public long Count { get; private set; }

        public long Bytes { get; private set; }

        public long FirstOffset { get; private set; } = long.MaxValue;

        public IReadOnlyList<DeviceBadArea> Areas
        {
            get
            {
                lock (_areas)
                {
                    var all = new List<DeviceBadArea>(_areas);
                    if (_open != null && all.Count < _max)
                    {
                        all.Add(_open);
                    }

                    return all.OrderBy(a => a.Offset).ToList();
                }
            }
        }

        public void Add(long offset, long length, DeviceBadAreaKind kind)
        {
            lock (_areas)
            {
                Bytes += length;
                FirstOffset = Math.Min(FirstOffset, offset);
                if (_open is { } open && open.Kind == kind && open.Offset + open.Length == offset)
                {
                    _open = open with { Length = open.Length + length };
                    return;
                }

                if (_open != null && _areas.Count < _max)
                {
                    _areas.Add(_open);
                }

                _open = new DeviceBadArea(offset, length, kind);
                Count++;
            }
        }
    }
}

/// <summary>
/// Inverts the pattern of <see cref="EraseDeviceOperation.FillPattern"/>: from a word read back it tells
/// for which offset that word was written.
/// </summary>
internal static class PatternDecoder
{
    private const ulong Golden = 0x9E3779B97F4A7C15;

    private static readonly ulong InverseGolden = Inverse(Golden);
    private static readonly ulong InverseMultiplier1 = Inverse(0xBF58476D1CE4E5B9);
    private static readonly ulong InverseMultiplier2 = Inverse(0x94D049BB133111EB);

    /// <summary>The offset (within <paramref name="length"/>) whose pattern word is <paramref name="word"/>.</summary>
    public static bool TryDecode(ulong word, ulong seed, long length, out long offset)
    {
        var z = word;
        z ^= (z >> 31) ^ (z >> 62);
        z *= InverseMultiplier2;
        z ^= (z >> 27) ^ (z >> 54);
        z *= InverseMultiplier1;
        z ^= (z >> 30) ^ (z >> 60);
        var index = (z - seed) * InverseGolden;
        if (index < (ulong)length / sizeof(ulong))
        {
            offset = (long)index * sizeof(ulong);
            return true;
        }

        offset = -1;
        return false;
    }

    /// <summary>Multiplicative inverse modulo 2^64 of an odd number (Newton's iteration).</summary>
    private static ulong Inverse(ulong a)
    {
        var x = a;
        for (var i = 0; i < 6; i++)
        {
            x *= 2 - (a * x);
        }

        return x;
    }
}
