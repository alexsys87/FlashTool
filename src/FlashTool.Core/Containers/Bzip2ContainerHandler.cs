using System.Buffers;
using FlashTool.Core.IO;
using FlashTool.Core.Sources;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.BZip2;

namespace FlashTool.Core.Containers;

/// <summary>
/// bzip2 stream (.img.bz2) through the managed SharpZipLib codec. Reads
/// concatenated streams as written by pbzip2 (what Yocto uses for bz2 images).
/// Single entry, uncompressed size unknown in advance. Decoding is
/// single-threaded; encoding splits the data into independent streams like
/// pbzip2 and compresses them in parallel.
/// </summary>
public sealed class Bzip2ContainerHandler : IContainerHandler
{
    private const int BufferSize = 1024 * 1024;

    // 900 KB blocks, the bzip2 default.
    private const int Level = 9;

    public ContainerFormat Format => ContainerFormat.Bzip2;

    public IReadOnlyList<string> Extensions { get; } = [".bz2"];

    public bool IsAvailable => true;

    public bool CanCreate => true;

    public bool RequiresSeekableSource => false;

    /// <summary>"BZh1".."BZh9" followed by a block or an end-of-stream marker.</summary>
    public bool IsMatch(ReadOnlySpan<byte> header)
    {
        if (header.Length < 10 || !IsStreamHeader(header))
        {
            return false;
        }

        var marker = header[4..10];
        return marker.SequenceEqual((ReadOnlySpan<byte>)[0x31, 0x41, 0x59, 0x26, 0x53, 0x59])
            || marker.SequenceEqual((ReadOnlySpan<byte>)[0x17, 0x72, 0x45, 0x38, 0x50, 0x90]);
    }

    public IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default) => new Bzip2Container(source);

    public Stream Create(Stream output, string entryName, long length, CompressionSettings settings)
    {
        // The presets are ignored: the bzip2 level is only the block size, and
        // smaller blocks are not faster, just compress worse (more so in parallel).
        var threads = settings.EffectiveThreads;
        return threads > 1
            ? new ParallelBzip2WriteStream(output, threads)
            : new OwningWriteStream(new BZip2OutputStream(output, Level) { IsStreamOwner = true }, null);
    }

    private static bool IsStreamHeader(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[0] == (byte)'B' && data[1] == (byte)'Z' && data[2] == (byte)'h' && data[3] is >= (byte)'1' and <= (byte)'9';

    private sealed class Bzip2Container : IImageContainer
    {
        public Bzip2Container(IStreamSource source)
        {
            Source = source;
            Entries = [new ImageEntry(0, ContainerNaming.GetInnerName(source.Name), null) { CompressedLength = source.Length }];
        }

        public ContainerFormat Format => ContainerFormat.Bzip2;

        public IStreamSource Source { get; }

        public IReadOnlyList<ImageEntry> Entries { get; }

        public ImageEntryReader OpenEntry(ImageEntry entry, CancellationToken cancellationToken = default)
        {
            var counting = new CountingStream(Source.Open(cancellationToken));
            return new ImageEntryReader(new ConcatenatedBzip2Stream(new PeekableStream(counting)), null, Source.Length, () => counting.BytesRead);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Decodes bzip2 streams one after another until the input ends. Data after
    /// the last stream that is not another stream is ignored, as bzip2 does.
    /// </summary>
    private sealed class ConcatenatedBzip2Stream : Stream
    {
        private readonly PeekableStream _input;
        private BZip2InputStream? _current;
        private bool _started;
        private bool _finished;

        public ConcatenatedBzip2Stream(PeekableStream input)
        {
            _input = input;
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                while (!_finished)
                {
                    if (_current == null && !StartNextStream())
                    {
                        _finished = true;
                        break;
                    }

                    var read = _current!.Read(buffer, offset, count);
                    if (read > 0 || count == 0)
                    {
                        return read;
                    }

                    _current.Dispose();
                    _current = null;
                }

                return 0;
            }
            catch (SharpZipBaseException ex)
            {
                throw new InvalidDataException($"The bzip2 data is damaged: {ex.Message}", ex);
            }
            catch (EndOfStreamException ex)
            {
                // Raised by the decoder, not by the source: the file is truncated.
                throw new InvalidDataException("The bzip2 data ends unexpectedly.", ex);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
                _input.Dispose();
            }

            base.Dispose(disposing);
        }

        private bool StartNextStream()
        {
            if (!IsStreamHeader(_input.Peek(4)))
            {
                if (!_started)
                {
                    throw new InvalidDataException("Not a bzip2 stream.");
                }

                return false;
            }

            _started = true;
            _current = new BZip2InputStream(_input) { IsStreamOwner = false };
            return true;
        }
    }

    /// <summary>
    /// Compresses every chunk of input as a complete bzip2 stream on the thread
    /// pool and writes the streams in order, as pbzip2 does. Any bzip2 decoder
    /// reads the result as one file.
    /// </summary>
    private sealed class ParallelBzip2WriteStream : Stream
    {
        private readonly Stream _output;
        private readonly int _chunkSize;
        private readonly int _maxInFlight;
        private readonly Queue<Task<byte[]>> _inFlight = new();
        private byte[] _chunk;
        private int _filled;
        private long _position;
        private bool _anyChunk;
        private bool _disposed;

        public ParallelBzip2WriteStream(Stream output, int threads)
        {
            _output = output;

            // About one block per stream, so splitting costs almost nothing in ratio.
            _chunkSize = Level * 100_000;
            _maxInFlight = threads * 2;
            _chunk = ArrayPool<byte>.Shared.Rent(_chunkSize);
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
            _position += buffer.Length;
            while (!buffer.IsEmpty)
            {
                var n = Math.Min(buffer.Length, _chunkSize - _filled);
                buffer[..n].CopyTo(_chunk.AsSpan(_filled));
                _filled += n;
                buffer = buffer[n..];
                if (_filled == _chunkSize)
                {
                    SubmitChunk();
                }
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try
                {
                    // An empty input still becomes a valid (empty) bzip2 file.
                    if (_filled > 0 || !_anyChunk)
                    {
                        SubmitChunk();
                    }

                    while (_inFlight.Count > 0)
                    {
                        WriteCompleted();
                    }
                }
                finally
                {
                    // Let the remaining workers finish before their buffers go back to the pool.
                    try
                    {
                        Task.WaitAll([.. _inFlight]);
                    }
                    catch (AggregateException)
                    {
                        // Already failing; the first error has been thrown.
                    }

                    ArrayPool<byte>.Shared.Return(_chunk);
                    _output.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void SubmitChunk()
        {
            var chunk = _chunk;
            var length = _filled;
            _inFlight.Enqueue(Task.Run(() =>
            {
                try
                {
                    using var compressed = new MemoryStream((length / 4) + 1024);
                    using (var bzip2 = new BZip2OutputStream(compressed, Level) { IsStreamOwner = false })
                    {
                        bzip2.Write(chunk, 0, length);
                    }

                    return compressed.ToArray();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(chunk);
                }
            }));

            _anyChunk = true;
            _chunk = ArrayPool<byte>.Shared.Rent(_chunkSize);
            _filled = 0;
            while (_inFlight.Count >= _maxInFlight)
            {
                WriteCompleted();
            }
        }

        private void WriteCompleted() => _output.Write(_inFlight.Dequeue().GetAwaiter().GetResult());
    }

    /// <summary>
    /// Buffered read stream that can look ahead without consuming. Also makes
    /// ReadByte cheap, which the bzip2 decoder calls for every input byte.
    /// </summary>
    private sealed class PeekableStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _buffer = new byte[BufferSize];
        private int _position;
        private int _length;

        public PeekableStream(Stream inner)
        {
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

        /// <summary>Up to <paramref name="count"/> next bytes; fewer only at the end of the input.</summary>
        public ReadOnlySpan<byte> Peek(int count)
        {
            if (_length - _position < count)
            {
                Buffer.BlockCopy(_buffer, _position, _buffer, 0, _length - _position);
                _length -= _position;
                _position = 0;
                while (_length < count)
                {
                    var read = _inner.Read(_buffer, _length, _buffer.Length - _length);
                    if (read == 0)
                    {
                        break;
                    }

                    _length += read;
                }
            }

            return _buffer.AsSpan(_position, Math.Min(count, _length - _position));
        }

        public override int ReadByte()
        {
            if (_position == _length && !Fill())
            {
                return -1;
            }

            return _buffer[_position++];
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || (_position == _length && !Fill()))
            {
                return 0;
            }

            var n = Math.Min(buffer.Length, _length - _position);
            _buffer.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private bool Fill()
        {
            _position = 0;
            _length = _inner.Read(_buffer, 0, _buffer.Length);
            return _length > 0;
        }
    }
}
