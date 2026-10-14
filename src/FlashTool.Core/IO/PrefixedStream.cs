namespace FlashTool.Core.IO;

/// <summary>
/// Replays already-consumed leading bytes before continuing with the inner
/// stream. Lets us inspect the head of a forward-only stream and still
/// write it from the very first byte.
/// </summary>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private int _prefixPosition;

    public PrefixedStream(byte[] prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < _prefix.Length)
        {
            var n = Math.Min(buffer.Length, _prefix.Length - _prefixPosition);
            _prefix.AsSpan(_prefixPosition, n).CopyTo(buffer);
            _prefixPosition += n;
            return n;
        }

        return _inner.Read(buffer);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
