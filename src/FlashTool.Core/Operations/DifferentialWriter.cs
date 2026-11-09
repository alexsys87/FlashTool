using System.Diagnostics;
using FlashTool.Core.IO;

namespace FlashTool.Core.Operations;

/// <summary>What differential writing did.</summary>
/// <param name="BytesCompared">Image data compared with what the device already held.</param>
/// <param name="BytesMatched">Compared data that was already there and was not written.</param>
/// <param name="BytesWritten">Data sent to the device, with or without comparing first.</param>
/// <param name="BytesWrittenWithoutCompare">Written straight away, while comparing was off for having found too many differences.</param>
/// <param name="TimesComparingStopped">How often comparing was turned off (it comes back on where the data matches again).</param>
public sealed record DifferentialWriteStats(long BytesCompared, long BytesMatched, long BytesWritten, long BytesWrittenWithoutCompare, int TimesComparingStopped)
{
    /// <summary>Share of the compared data that was already on the device.</summary>
    public double MatchedFraction => BytesCompared > 0 ? (double)BytesMatched / BytesCompared : 0;
}

/// <summary>
/// Writes only what differs from the device. Each piece is read back first and compared in 64 KiB
/// blocks; differing blocks closer than <see cref="MergeGap"/> are written as one request, since
/// small scattered writes are slow and wear cheap flash more than a slightly longer one.
/// </summary>
/// <remarks>
/// Reading costs time but no wear. When a stretch of the image hardly matches (a rebuilt root file
/// system, a different image), comparing only makes the write slower, so it is turned off once the
/// differences in a sliding window pass a threshold: the share above which writing everything is
/// faster (1 - write speed / read speed, as measured), but never below one half, because every
/// matching block still saves a write. While it is off, every <see cref="ProbeInterval"/>-th piece is
/// still compared; when those probes match again (another partition), comparing resumes.
/// A failed compare read is not an error: that piece is simply written.
/// </remarks>
internal sealed class DifferentialWriter : IDisposable
{
    public const int CompareBlock = 64 * 1024;
    public const int MergeGap = 256 * 1024;
    public const int ProbeInterval = 16;
    private const int ProbesToResume = 2;
    private const double MinThreshold = 0.5;
    private const double MaxThreshold = 0.9;

    private readonly IBlockDevice _device;
    private readonly IBlockDevice _compareDevice;
    private readonly long _windowBytes;
    private readonly AlignedBuffer _readBuffer;
    private readonly Queue<(long Compared, long Different)> _window = new();
    private readonly Queue<double> _probes = new();
    private long _windowCompared;
    private long _windowDifferent;
    private bool _comparing = true;
    private int _sinceProbe;
    private long _readBytes;
    private double _readSeconds;
    private long _writeBytes;
    private double _writeSeconds;
    private long _compared;
    private long _matched;
    private long _written;
    private long _writtenWithoutCompare;
    private int _stops;

    /// <param name="device">Writes go here (with the operation's retry policy).</param>
    /// <param name="compareDevice">Compare reads go here: the same device, but its errors do not ask the user.</param>
    /// <param name="maxPiece">Largest piece that will be passed to <see cref="Write"/>.</param>
    /// <param name="windowBytes">How much compared data the decision to stop comparing looks at.</param>
    public DifferentialWriter(IBlockDevice device, IBlockDevice compareDevice, int maxPiece, long windowBytes)
    {
        _device = device;
        _compareDevice = compareDevice;
        _windowBytes = windowBytes;
        _readBuffer = new AlignedBuffer(maxPiece);
    }

    /// <summary>Bytes read for comparing plus bytes written: what the device really did, for the speed shown.</summary>
    public long DeviceBytes => Interlocked.Read(ref _readBytes) + Interlocked.Read(ref _writeBytes);

    public DifferentialWriteStats Stats => new(_compared, _matched, _written, _writtenWithoutCompare, _stops);

    /// <summary>
    /// Brings [<paramref name="offset"/>, + data.Length) of the device in line with <paramref name="data"/>
    /// (sector-aligned, as it would be written in full).
    /// </summary>
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        var probe = false;
        if (!_comparing)
        {
            _sinceProbe++;
            probe = _sinceProbe % ProbeInterval == 0;
            if (!probe)
            {
                WriteTimed(offset, data);
                _writtenWithoutCompare += data.Length;
                return;
            }
        }

        var actual = _readBuffer.Span[..data.Length];
        if (!TryRead(offset, actual))
        {
            WriteTimed(offset, data);
            Account(data.Length, data.Length, probe);
            return;
        }

        // Differing 64 KiB blocks (on absolute boundaries), merged across short identical gaps.
        long different = 0;
        long runStart = -1;
        long runEnd = -1;
        var end = offset + data.Length;
        for (var block = offset; block < end;)
        {
            var next = Math.Min(end, (block / CompareBlock * CompareBlock) + CompareBlock);
            var at = (int)(block - offset);
            var length = (int)(next - block);
            if (!data.Slice(at, length).SequenceEqual(actual.Slice(at, length)))
            {
                if (runStart >= 0 && block - runEnd <= MergeGap)
                {
                    runEnd = next;
                }
                else
                {
                    if (runStart >= 0)
                    {
                        different += Flush(offset, data, runStart, runEnd);
                    }

                    runStart = block;
                    runEnd = next;
                }
            }

            block = next;
        }

        if (runStart >= 0)
        {
            different += Flush(offset, data, runStart, runEnd);
        }

        Account(data.Length, different, probe);
    }

    public void Dispose() => _readBuffer.Free();

    private long Flush(long offset, ReadOnlySpan<byte> data, long start, long end)
    {
        WriteTimed(start, data[(int)(start - offset)..(int)(end - offset)]);
        return end - start;
    }

    private void Account(long compared, long different, bool probe)
    {
        _compared += compared;
        _matched += compared - different;
        var share = compared > 0 ? (double)different / compared : 0;
        if (probe)
        {
            _probes.Enqueue(share);
            while (_probes.Count > ProbesToResume)
            {
                _probes.Dequeue();
            }

            if (_probes.Count == ProbesToResume && _probes.All(p => p < Threshold / 2))
            {
                _comparing = true;
                _probes.Clear();
                ResetWindow();
            }

            return;
        }

        _window.Enqueue((compared, different));
        _windowCompared += compared;
        _windowDifferent += different;
        while (_window.Count > 1 && _windowCompared - _window.Peek().Compared >= _windowBytes)
        {
            var (c, d) = _window.Dequeue();
            _windowCompared -= c;
            _windowDifferent -= d;
        }

        if (_windowCompared >= _windowBytes && (double)_windowDifferent / _windowCompared > Threshold)
        {
            _comparing = false;
            _stops++;
            _sinceProbe = 0;
            _probes.Clear();
            ResetWindow();
        }
    }

    /// <summary>Above this share of differences, comparing costs more than it saves.</summary>
    private double Threshold
    {
        get
        {
            if (_readSeconds <= 0 || _writeSeconds <= 0 || _writeBytes < 4 * 1024 * 1024)
            {
                return MinThreshold;
            }

            var read = _readBytes / _readSeconds;
            var write = _writeBytes / _writeSeconds;
            return Math.Clamp(1 - (write / read), MinThreshold, MaxThreshold);
        }
    }

    private void ResetWindow()
    {
        _window.Clear();
        _windowCompared = 0;
        _windowDifferent = 0;
    }

    private void WriteTimed(long offset, ReadOnlySpan<byte> data)
    {
        var start = Stopwatch.GetTimestamp();
        _device.Write(offset, data);
        _writeSeconds += Stopwatch.GetElapsedTime(start).TotalSeconds;
        Interlocked.Add(ref _writeBytes, data.Length);
        _written += data.Length;
    }

    private bool TryRead(long offset, Span<byte> buffer)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            _compareDevice.Read(offset, buffer);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IoRetryPolicy.IsRetryable(ex))
        {
            return false;
        }
        finally
        {
            _readSeconds += Stopwatch.GetElapsedTime(start).TotalSeconds;
            Interlocked.Add(ref _readBytes, buffer.Length);
        }
    }
}
