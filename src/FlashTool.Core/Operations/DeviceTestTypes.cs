using FlashTool.Core.IO;

namespace FlashTool.Core.Operations;

public enum DeviceTestMode
{
    /// <summary>Reads the whole device: speed by offset, request times, unreadable areas. Writes nothing.</summary>
    Read,

    /// <summary>Destructive: fills the whole device with a position-dependent pattern, then reads it all back.</summary>
    WriteVerify,

    /// <summary>
    /// Destructive but quick: writes 1 MiB probes at powers of two and spread over the device, reads them back
    /// and narrows down where data stops being kept. Catches the usual counterfeits in minutes.
    /// </summary>
    QuickProbe,

    /// <summary>Destructive: random 4 KiB reads, then random 4 KiB writes, for a fixed time each (queue depth 1).</summary>
    RandomAccess,
}

/// <summary>Direction of a measurement.</summary>
public enum DeviceTestPhase
{
    Read,
    Write,
}

public enum DeviceTestVerdict
{
    /// <summary>Everything that was tested was read and written correctly.</summary>
    Healthy,

    /// <summary>Some areas could not be read or written, or returned wrong data.</summary>
    BadAreas,

    /// <summary>The device keeps less data than it reports: see <see cref="DeviceTestResult.RealCapacity"/>.</summary>
    FakeCapacity,
}

public enum DeviceBadAreaKind
{
    ReadError,
    WriteError,

    /// <summary>Read back, but not what was written there.</summary>
    WrongData,
}

/// <summary>Average speed over [<see cref="Offset"/>, +<see cref="Length"/>) and the longest single request in it.</summary>
public sealed record DeviceTestSample(DeviceTestPhase Phase, long Offset, long Length, double BytesPerSecond, double MaxLatencyMs);

public sealed record DeviceBadArea(long Offset, long Length, DeviceBadAreaKind Kind);

/// <summary>A stretch where the speed stays well below the median of the pass.</summary>
public sealed record DeviceSlowArea(DeviceTestPhase Phase, long Offset, long Length);

/// <summary>Summary of one sequential pass.</summary>
public sealed record DeviceTestPassStats(
    DeviceTestPhase Phase,
    long Bytes,
    double AverageBytesPerSecond,
    double MinBytesPerSecond,
    double MaxBytesPerSecond,
    double MedianLatencyMs,
    double MaxLatencyMs);

/// <summary>Random 4 KiB access: operations per second and request times.</summary>
public sealed record RandomAccessStats(DeviceTestPhase Phase, int Operations, double Iops, double AverageLatencyMs, double P99LatencyMs, int Errors)
{
    public const int BlockSize = 4096;

    public double BytesPerSecond => Iops * BlockSize;
}

public sealed record DeviceTestOptions
{
    public DeviceTestMode Mode { get; init; } = DeviceTestMode.Read;

    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    public int PipelineDepth { get; init; } = 4;

    /// <summary>About this many points per sequential pass, for the chart.</summary>
    public int SampleCount { get; init; } = 400;

    /// <summary>Length of each half (reads, writes) of <see cref="DeviceTestMode.RandomAccess"/>.</summary>
    public TimeSpan RandomDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Evenly spread probes of <see cref="DeviceTestMode.QuickProbe"/> (powers of two come on top).</summary>
    public int ProbeCount { get; init; } = 64;

    /// <summary>Seed of the pattern; a fresh one is used when null.</summary>
    public ulong? PatternSeed { get; init; }

    /// <summary>
    /// Errors are part of the result, so there is no question to the user: one retry, then the area is
    /// recorded as bad and the test moves on.
    /// </summary>
    public IoRetryPolicy IoRetry { get; init; } = new() { AutomaticAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(100) };

    /// <summary>Receives every chart point as soon as it is measured.</summary>
    public IProgress<DeviceTestSample>? SampleReported { get; init; }

    /// <summary>At most this many bad areas are kept (all are counted).</summary>
    public int MaxReportedAreas { get; init; } = 1000;

    public bool IsDestructive => Mode != DeviceTestMode.Read;
}

public sealed record DeviceTestResult(DeviceTestMode Mode, long DeviceLength, DeviceTestVerdict Verdict, TimeSpan Elapsed)
{
    public IReadOnlyList<DeviceTestSample> Samples { get; init; } = [];

    public IReadOnlyList<DeviceTestPassStats> Passes { get; init; } = [];

    public IReadOnlyList<DeviceSlowArea> SlowAreas { get; init; } = [];

    public IReadOnlyList<DeviceBadArea> BadAreas { get; init; } = [];

    /// <summary>All bad areas, also those beyond <see cref="DeviceTestOptions.MaxReportedAreas"/>.</summary>
    public long BadAreaCount { get; init; }

    public long BadBytes { get; init; }

    /// <summary>How much the device really keeps, when it is counterfeit.</summary>
    public long? RealCapacity { get; init; }

    /// <summary>The device returned data written for another offset: its addresses wrap around.</summary>
    public bool AddressesWrap { get; init; }

    public IReadOnlyList<RandomAccessStats> RandomAccess { get; init; } = [];
}
