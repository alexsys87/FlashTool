namespace FlashTool.Core.IO;

public enum IoOperationKind
{
    Read,
    Write,
    Flush,
}

/// <summary>Describes an I/O request that kept failing.</summary>
/// <param name="Kind">What was attempted.</param>
/// <param name="Offset">Byte offset on the device.</param>
/// <param name="Length">Number of bytes.</param>
/// <param name="Error">The last error.</param>
/// <param name="Attempts">How many times the request was tried.</param>
public sealed record IoErrorContext(IoOperationKind Kind, long Offset, int Length, Exception Error, int Attempts);

/// <summary>
/// How device I/O errors (bad sectors, timeouts, flaky USB connections) are
/// handled: a number of automatic retries of the very same request, then
/// optionally a question to the user whether to keep retrying.
/// </summary>
public sealed record IoRetryPolicy
{
    public static IoRetryPolicy Default { get; } = new();

    /// <summary>No retries at all (errors surface immediately).</summary>
    public static IoRetryPolicy None { get; } = new() { AutomaticAttempts = 1 };

    /// <summary>Tries per round before <see cref="AskUser"/> is consulted (including the first try).</summary>
    public int AutomaticAttempts { get; init; } = 5;

    /// <summary>Pause before the first retry; doubled for every further retry.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Called when a round of automatic attempts failed. Return true to start
    /// another round at the same position, false to abort the operation.
    /// When null the error is thrown.
    /// </summary>
    public Func<IoErrorContext, CancellationToken, Task<bool>>? AskUser { get; init; }

    /// <summary>Errors worth retrying: media/transport errors and timeouts, not logic errors.</summary>
    public static bool IsRetryable(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException => false,
        IOException or TimeoutException => true,
        FlashToolException f => f.Code == FlashErrorCode.DeviceIoError,
        _ => false,
    };

    internal TimeSpan DelayFor(int failedAttempts)
    {
        var ms = InitialDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, failedAttempts - 1));
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
    }
}

/// <summary>
/// Implemented by devices whose blocking I/O can be interrupted (Windows raw
/// disks): lets a pressed "Cancel" abort an I/O request that hangs.
/// </summary>
public interface IAbortableIo
{
    void SetAbortToken(CancellationToken token);
}
