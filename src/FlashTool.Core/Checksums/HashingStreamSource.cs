using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Checksums;

/// <summary>
/// Wraps a source and hashes whatever is read through it, so that the SHA-256 of a download is
/// known without fetching the file a second time.
/// </summary>
/// <remarks>
/// Containers open a source several times: format detection, an index at the end of the file, the
/// data itself. Only a stream that was read from offset 0 to the last byte in one go can tell the
/// digest of the whole file; the others are ignored. Archives that are read with random access
/// (zip, 7z) never produce one.
/// </remarks>
public sealed class HashingStreamSource : IStreamSource
{
    private readonly IStreamSource _inner;
    private readonly object _gate = new();
    private readonly List<HashingReadStream> _open = [];
    private byte[]? _digest;

    public HashingStreamSource(IStreamSource inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public string Name => _inner.Name;

    public string Location => _inner.Location;

    public long? Length => _inner.Length;

    public bool IsSeekable => _inner.IsSeekable;

    public bool IsRemote => _inner.IsRemote;

    public Stream Open(CancellationToken cancellationToken = default)
    {
        var stream = new HashingReadStream(_inner.Open(cancellationToken), this);
        lock (_gate)
        {
            _open.Add(stream);
        }

        return stream;
    }

    /// <summary>
    /// SHA-256 of the whole source, available once some stream has read it completely from the start.
    /// </summary>
    public bool TryGetSha256([NotNullWhen(true)] out byte[]? sha256)
    {
        lock (_gate)
        {
            foreach (var stream in _open)
            {
                if (stream.TryGetDigest(out sha256))
                {
                    return true;
                }
            }

            sha256 = _digest;
            return sha256 != null;
        }
    }

    internal void Closed(HashingReadStream stream, byte[]? digest)
    {
        lock (_gate)
        {
            _open.Remove(stream);
            if (digest != null)
            {
                _digest = digest;
            }
        }
    }

    internal bool IsComplete(long hashed, bool atEnd) => Length is { } length ? hashed == length : atEnd;
}

/// <summary>Read-only stream that feeds a SHA-256 while it is read front to back.</summary>
internal sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly HashingStreamSource _owner;
    private readonly object _sync = new();
    private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _banked;
    private long _hashed;
    private bool _atEnd;
    private bool _disposed;

    public HashingReadStream(Stream inner, HashingStreamSource owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set
        {
            _inner.Position = value;
            Moved(value);
        }
    }

    /// <summary>Digest of the whole source if the current (or an earlier) pass covered all of it.</summary>
    public bool TryGetDigest([NotNullWhen(true)] out byte[]? digest)
    {
        lock (_sync)
        {
            digest = CurrentDigest();
            return digest != null;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Feed(buffer, read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Feed(buffer.Span, read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = _inner.Seek(offset, origin);
        Moved(position);
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            byte[]? digest;
            lock (_sync)
            {
                digest = CurrentDigest();
                _hash?.Dispose();
                _hash = null;
            }

            _owner.Closed(this, digest);
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Feed(ReadOnlySpan<byte> buffer, int read)
    {
        lock (_sync)
        {
            if (_hash is null)
            {
                return;
            }

            if (read == 0)
            {
                _atEnd |= buffer.Length > 0;
                return;
            }

            _hash.AppendData(buffer[..read]);
            _hashed += read;
        }
    }

    /// <summary>A jump to the very start begins a new pass; any other jump spoils the current one.</summary>
    private void Moved(long position)
    {
        lock (_sync)
        {
            if (_hash != null && position == _hashed)
            {
                return;
            }

            _banked = CurrentDigest();
            _hash?.Dispose();
            _hashed = 0;
            _atEnd = false;
            _hash = position == 0 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        }
    }

    private byte[]? CurrentDigest() =>
        _hash != null && _owner.IsComplete(_hashed, _atEnd) ? _hash.GetCurrentHash() : _banked;
}
