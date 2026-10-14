using System.Diagnostics;

namespace FlashTool.Core.Operations;

public enum OperationPhase
{
    Preparing,
    Writing,
    Reading,
    Verifying,
    Finalizing,
    Completed,
}

/// <summary>Progress snapshot reported to the UI.</summary>
/// <param name="Phase">Current phase.</param>
/// <param name="BytesProcessed">Bytes processed in this phase.</param>
/// <param name="BytesTotal">Total bytes of this phase, when known.</param>
/// <param name="Fraction">0..1 progress of this phase (estimated if the total is unknown).</param>
/// <param name="BytesPerSecond">Recent throughput.</param>
/// <param name="Remaining">Estimated remaining time of this phase.</param>
public readonly record struct OperationProgress(
    OperationPhase Phase,
    long BytesProcessed,
    long? BytesTotal,
    double Fraction,
    double BytesPerSecond,
    TimeSpan? Remaining);

/// <summary>
/// Throttles progress notifications and computes a smoothed speed.
/// </summary>
internal sealed class ProgressReporter
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(4);

    private readonly IProgress<OperationProgress>? _progress;
    private readonly OperationPhase _phase;
    private readonly long? _total;
    private readonly Func<double>? _fallbackFraction;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();
    private TimeSpan _lastReport = TimeSpan.FromDays(-1);

    public ProgressReporter(IProgress<OperationProgress>? progress, OperationPhase phase, long? total, Func<double>? fallbackFraction = null)
    {
        _progress = progress;
        _phase = phase;
        _total = total;
        _fallbackFraction = fallbackFraction;
        progress?.Report(new OperationProgress(phase, 0, total, 0, 0, null));
    }

    public void Report(long processed, bool force = false)
    {
        if (_progress == null)
        {
            return;
        }

        var now = _clock.Elapsed;
        if (!force && now - _lastReport < Interval)
        {
            return;
        }

        _lastReport = now;
        _samples.Enqueue((now, processed));
        while (_samples.Count > 2 && now - _samples.Peek().Time > SpeedWindow)
        {
            _samples.Dequeue();
        }

        var first = _samples.Peek();
        var seconds = (now - first.Time).TotalSeconds;
        var speed = seconds > 0.2 ? (processed - first.Bytes) / seconds : 0;

        double fraction;
        if (_total is > 0)
        {
            fraction = Math.Clamp((double)processed / _total.Value, 0, 1);
        }
        else
        {
            fraction = Math.Clamp(_fallbackFraction?.Invoke() ?? 0, 0, 1);
        }

        TimeSpan? remaining = null;
        if (speed > 0 && _total is > 0)
        {
            remaining = TimeSpan.FromSeconds((_total.Value - processed) / speed);
        }
        else if (speed > 0 && fraction > 0.01)
        {
            remaining = TimeSpan.FromSeconds(_clock.Elapsed.TotalSeconds * (1 - fraction) / fraction);
        }

        _progress.Report(new OperationProgress(_phase, processed, _total, fraction, speed, remaining));
    }
}
