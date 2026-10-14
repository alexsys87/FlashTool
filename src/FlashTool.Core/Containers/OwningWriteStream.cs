namespace FlashTool.Core.Containers;

/// <summary>
/// Write-only stream that forwards to <c>inner</c> and, when disposed,
/// disposes a chain of owned objects in order (entry stream, archive, file),
/// optionally waiting for a background completion task.
/// </summary>
internal sealed class OwningWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly IDisposable[] _owned;
    private readonly Func<Task>? _completion;
    private long _position;
    private bool _disposed;

    public OwningWriteStream(Stream inner, Func<Task>? completion, params IDisposable[] owned)
    {
        _inner = inner;
        _owned = owned;
        _completion = completion;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _position;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        _position += buffer.Length;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            List<Exception>? errors = null;
            try
            {
                _inner.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }

            if (_completion != null)
            {
                try
                {
                    _completion().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    (errors ??= []).Add(ex);
                }
            }

            foreach (var item in _owned)
            {
                try
                {
                    item.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= []).Add(ex);
                }
            }

            if (errors != null)
            {
                throw errors.Count == 1 ? errors[0] : new AggregateException(errors);
            }
        }

        base.Dispose(disposing);
    }
}
