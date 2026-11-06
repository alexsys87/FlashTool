using System.Runtime.Versioning;

namespace FlashTool.Platform.Windows;

/// <summary>
/// Keeps Windows from going to sleep on its idle timer while a long disk operation runs
/// (<c>SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)</c>). Dispose it when
/// the operation ends.
/// </summary>
/// <remarks>
/// The execution state belongs to the thread that sets it, and operations are async: their
/// continuations can run on any thread. So every lease parks a small dedicated thread that
/// sets the state, waits to be released and clears it again. That makes the lease safe to
/// dispose from any thread, and the request goes away with the thread if the process dies.
/// <para>
/// The display may still turn off. Sleep that the user asks for (lid, power button, Start
/// menu) cannot be prevented, and a Modern Standby laptop on battery ends the request by
/// itself some minutes after its sleep timeout. Failing to set the state is not an error:
/// the lease then simply does nothing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SleepInhibitor : IDisposable
{
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private readonly Thread _thread;
    private int _disposed;

    private SleepInhibitor()
    {
        _thread = new Thread(Hold)
        {
            IsBackground = true,
            Name = "FlashTool keep-awake",
        };
        _thread.Start();

        // Return only once the request is in place (or has failed), so callers can rely on it.
        _started.Wait();
    }

    /// <summary>Prevents idle sleep until the returned lease is disposed.</summary>
    public static SleepInhibitor Acquire() => new();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        _thread.Join();

        // Nothing touches the events once the thread has finished.
        _release.Dispose();
        _started.Dispose();
    }

    private void Hold()
    {
        try
        {
            SetState(NativeMethods.EsContinuous | NativeMethods.EsSystemRequired);
        }
        finally
        {
            _started.Set();
        }

        _release.Wait();
        SetState(NativeMethods.EsContinuous);
    }

    private static void SetState(uint state)
    {
        try
        {
            _ = NativeMethods.SetThreadExecutionState(state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort: a power hint must never take the application down. Without it the
            // system just keeps its normal sleep behaviour.
        }
    }
}
