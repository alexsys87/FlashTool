namespace FlashTool.Core.IO;

/// <summary>
/// Decorator that repeats failed device requests at the same offset according
/// to an <see cref="IoRetryPolicy"/>. Requests are idempotent (whole aligned
/// chunks), so repeating a read or a write is always safe.
/// </summary>
public sealed class ResilientBlockDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly IoRetryPolicy _policy;
    private readonly CancellationToken _cancellationToken;

    public ResilientBlockDevice(IBlockDevice inner, IoRetryPolicy policy, CancellationToken cancellationToken)
    {
        _inner = inner;
        _policy = policy;
        _cancellationToken = cancellationToken;
        if (inner is IAbortableIo abortable)
        {
            abortable.SetAbortToken(cancellationToken);
        }
    }

    /// <summary>Number of requests that succeeded only after a retry.</summary>
    public int RecoveredErrors { get; private set; }

    public long Length => _inner.Length;

    public int SectorSize => _inner.SectorSize;

    public bool CanWrite => _inner.CanWrite;

    public void Read(long offset, Span<byte> buffer)
    {
        var attempts = 0;
        while (true)
        {
            try
            {
                _inner.Read(offset, buffer);
                Recovered(attempts);
                return;
            }
            catch (Exception ex) when (IoRetryPolicy.IsRetryable(ex))
            {
                Failed(ex, IoOperationKind.Read, offset, buffer.Length, ref attempts);
            }
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        var attempts = 0;
        while (true)
        {
            try
            {
                _inner.Write(offset, buffer);
                Recovered(attempts);
                return;
            }
            catch (Exception ex) when (IoRetryPolicy.IsRetryable(ex))
            {
                Failed(ex, IoOperationKind.Write, offset, buffer.Length, ref attempts);
            }
        }
    }

    public void Flush()
    {
        var attempts = 0;
        while (true)
        {
            try
            {
                _inner.Flush();
                Recovered(attempts);
                return;
            }
            catch (Exception ex) when (IoRetryPolicy.IsRetryable(ex))
            {
                Failed(ex, IoOperationKind.Flush, 0, 0, ref attempts);
            }
        }
    }

    public void Dispose() => _inner.Dispose();

    private void Recovered(int attempts)
    {
        if (attempts > 0)
        {
            RecoveredErrors++;
        }
    }

    /// <summary>Waits before the next try, or asks the user / rethrows when the round is exhausted.</summary>
    private void Failed(Exception error, IoOperationKind kind, long offset, int length, ref int attempts)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        attempts++;
        if (attempts < _policy.AutomaticAttempts)
        {
            _cancellationToken.WaitHandle.WaitOne(_policy.DelayFor(attempts));
            _cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (_policy.AskUser == null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        // Runs on a worker thread; the UI thread shows the question meanwhile.
        var retry = _policy.AskUser(new IoErrorContext(kind, offset, length, error, attempts), _cancellationToken).GetAwaiter().GetResult();
        if (!retry)
        {
            throw new OperationCanceledException("The user cancelled after a device I/O error.", error, _cancellationToken);
        }

        attempts = 0;
    }
}
