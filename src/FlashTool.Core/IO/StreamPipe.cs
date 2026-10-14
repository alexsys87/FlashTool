using System.IO.Pipelines;

namespace FlashTool.Core.IO;

/// <summary>
/// In-memory bounded pipe that turns a push-based API (a library that
/// writes into a Stream we give it) into a pull-based Stream and vice versa,
/// without touching the disk. Back-pressure keeps memory usage bounded.
/// </summary>
public sealed class StreamPipe
{
    private readonly Pipe _pipe;

    public StreamPipe(long bufferBytes = 32L * 1024 * 1024)
    {
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: bufferBytes,
            resumeWriterThreshold: bufferBytes / 2,
            minimumSegmentSize: 64 * 1024,
            useSynchronizationContext: false));
    }

    /// <summary>Creates the consumer side. <paramref name="declaredLength"/> is reported as <see cref="Stream.Length"/>.</summary>
    /// <param name="declaredLength">Total number of bytes the producer will write, if known.</param>
    /// <param name="reportSeekable">
    /// Report <see cref="Stream.CanSeek"/> = true for consumers that insist on it but read
    /// sequentially (SevenZipSharp). Only no-op seeks are honored; see <see cref="ReaderStream.Seek"/>.
    /// </param>
    public Stream CreateReader(long? declaredLength = null, bool reportSeekable = false)
        => new ReaderStream(_pipe.Reader, declaredLength, reportSeekable);

    /// <summary>Creates the producer side. Disposing it completes the pipe.</summary>
    public Stream CreateWriter() => new WriterStream(_pipe.Writer);

    /// <summary>Completes the writer with an error so the reader observes it.</summary>
    public void FailWriter(Exception error) => _pipe.Writer.Complete(error);

    /// <summary>Completes the reader early; subsequent writes fail and unblock the producer.</summary>
    public void CancelReader() => _pipe.Reader.Complete();

    private sealed class ReaderStream : Stream
    {
        private readonly PipeReader _reader;
        private readonly long? _length;
        private readonly bool _reportSeekable;
        private readonly Stream _inner;
        private long _position;

        // Position requested through Seek before any data was consumed (e.g. a size probe
        // via Seek(0, End)); reading is only allowed once it is back at the real position.
        private long _virtualPosition;

        public ReaderStream(PipeReader reader, long? length, bool reportSeekable)
        {
            _reader = reader;
            _length = length;
            _reportSeekable = reportSeekable && length.HasValue;
            _inner = reader.AsStream(leaveOpen: true);
        }

        public override bool CanRead => true;

        public override bool CanSeek => _reportSeekable;

        public override bool CanWrite => false;

        public override long Length => _length ?? throw new NotSupportedException();

        public override long Position
        {
            get => _virtualPosition;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            EnsureAtRealPosition();
            var n = _inner.Read(buffer);
            _position += n;
            _virtualPosition = _position;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureAtRealPosition();
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _position += n;
            _virtualPosition = _position;
            return n;
        }

        /// <summary>
        /// Honors only seeks that do not require moving through the data: to the current
        /// position (position queries) or, before anything was read, anywhere and back
        /// (size probes such as Seek(0, End) followed by Seek(0, Begin)).
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _virtualPosition + offset,
                SeekOrigin.End when _length.HasValue => _length.Value + offset,
                _ => throw new NotSupportedException(),
            };

            if (target == _position || (_position == 0 && _reportSeekable && target >= 0))
            {
                _virtualPosition = target;
                return target;
            }

            throw new NotSupportedException("A pipe can only be read sequentially.");
        }

        private void EnsureAtRealPosition()
        {
            if (_virtualPosition != _position)
            {
                throw new NotSupportedException("A pipe can only be read sequentially.");
            }
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Complete();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class WriterStream : Stream
    {
        private readonly PipeWriter _writer;
        private long _position;
        private bool _completed;

        public WriterStream(PipeWriter writer) => _writer = writer;

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
            while (!buffer.IsEmpty)
            {
                var chunk = Math.Min(buffer.Length, 1024 * 1024);
                var span = _writer.GetSpan(chunk);
                chunk = Math.Min(chunk, span.Length);
                buffer[..chunk].CopyTo(span);
                _writer.Advance(chunk);
                _position += chunk;
                buffer = buffer[chunk..];

                // Synchronous wait is intentional: producers run on worker threads.
                var result = _writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                if (result.IsCompleted)
                {
                    throw new OperationCanceledException("The consumer closed the pipe.");
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (offset == 0 && origin == SeekOrigin.Current)
            {
                return _position;
            }

            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            // Native extractors may pre-size the output; it is meaningless for a pipe.
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_completed)
            {
                _completed = true;
                _writer.Complete();
            }

            base.Dispose(disposing);
        }
    }
}
