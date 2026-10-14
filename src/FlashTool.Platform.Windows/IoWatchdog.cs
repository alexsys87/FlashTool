using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace FlashTool.Platform.Windows;

/// <summary>
/// Interrupts synchronous disk I/O that hangs (a stalled USB card reader) or
/// that the user wants to cancel, using CancelSynchronousIo on the blocked
/// thread. The interrupted request then fails and goes through the normal
/// retry logic, so a hang becomes a retry instead of a frozen operation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class IoWatchdog : IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<long, Request> _active = new();
    private readonly Timer _timer;
    private long _nextId;
    private CancellationToken _abort;

    public IoWatchdog(TimeSpan timeout)
    {
        _timeout = timeout;
        _timer = new Timer(_ => Check(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public void SetAbortToken(CancellationToken token) => _abort = token;

    /// <summary>Starts watching the current thread's next blocking request.</summary>
    public Request Arm()
    {
        var thread = NativeMethods.OpenThread(NativeMethods.ThreadTerminate, false, NativeMethods.GetCurrentThreadId());
        var request = new Request(this, Interlocked.Increment(ref _nextId), thread, DateTime.UtcNow + _timeout);
        _active[request.Id] = request;
        return request;
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var request in _active.Values)
        {
            request.Dispose();
        }
    }

    private void Check()
    {
        var now = DateTime.UtcNow;
        foreach (var request in _active.Values)
        {
            if (_abort.IsCancellationRequested)
            {
                request.Interrupt(cancelled: true);
            }
            else if (now > request.Deadline)
            {
                request.Interrupt(cancelled: false);
            }
        }
    }

    internal sealed class Request : IDisposable
    {
        private readonly IoWatchdog _owner;
        private readonly object _lock = new();
        private nint _thread;

        public Request(IoWatchdog owner, long id, nint thread, DateTime deadline)
        {
            _owner = owner;
            Id = id;
            _thread = thread;
            Deadline = deadline;
        }

        public long Id { get; }

        public DateTime Deadline { get; }

        public bool TimedOut { get; private set; }

        public bool Cancelled { get; private set; }

        public bool Fired => TimedOut || Cancelled;

        public void Interrupt(bool cancelled)
        {
            lock (_lock)
            {
                if (_thread == 0 || Fired)
                {
                    return;
                }

                Cancelled = cancelled;
                TimedOut = !cancelled;
                NativeMethods.CancelSynchronousIo(_thread);
            }
        }

        /// <summary>Translates the aborted request into a retryable timeout or a cancellation.</summary>
        public Exception ToException(TimeSpan timeout) => Cancelled
            ? new OperationCanceledException(_owner._abort)
            : new TimeoutException($"The device did not respond within {timeout.TotalSeconds:0} s.");

        public void Dispose()
        {
            _owner._active.TryRemove(Id, out _);
            lock (_lock)
            {
                if (_thread != 0)
                {
                    NativeMethods.CloseHandle(_thread);
                    _thread = 0;
                }
            }
        }
    }
}
