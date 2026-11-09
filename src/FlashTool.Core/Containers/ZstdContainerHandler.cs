using FlashTool.Core.IO;
using FlashTool.Core.Sources;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace FlashTool.Core.Containers;

/// <summary>
/// Zstandard stream (.img.zst) through the managed ZstdSharp port: no native
/// library is needed, compression is multi-threaded. Reads files made by zstd
/// and pzstd (several frames, skippable frames).
/// </summary>
public sealed class ZstdContainerHandler : IContainerHandler
{
    private const int BufferSize = 4 * 1024 * 1024;

    // zstd refuses windows above 2^27 unless told otherwise; accept images made with --long=31.
    private const int MaxWindowLog = 31;

    public ContainerFormat Format => ContainerFormat.Zstd;

    public IReadOnlyList<string> Extensions { get; } = [".zst"];

    public bool IsAvailable => true;

    public bool CanCreate => true;

    public bool RequiresSeekableSource => false;

    public bool IsMatch(ReadOnlySpan<byte> header) => ZstdFrameReader.IsMatch(header);

    public IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default)
    {
        long? length = null;

        // Finding the size means visiting every block header; cheap on disk, too many requests over HTTP.
        if (source.IsSeekable && !source.IsRemote)
        {
            using var probe = source.Open(cancellationToken);
            length = ZstdFrameReader.TryGetContentSize(probe);
        }

        return new ZstdContainer(source, length);
    }

    public Stream Create(Stream output, string entryName, long length, CompressionSettings settings)
    {
        var level = settings.Preset switch
        {
            CompressionPreset.Fastest => 1,
            CompressionPreset.Fast => 3,
            CompressionPreset.Maximum => 19,
            _ => 9,
        };

        CompressionStream? zstd = null;
        try
        {
            zstd = new CompressionStream(output, level, BufferSize, leaveOpen: true);
            zstd.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            var threads = LimitThreadsByMemory(level, settings.EffectiveThreads);
            if (threads > 1)
            {
                zstd.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, threads);
            }

            // Stores the size in the frame header, so reading the file back knows it up front.
            zstd.SetPledgedSrcSize((ulong)length);
            return new ZstdWriteStream(zstd, output, length);
        }
        catch
        {
            zstd?.Dispose();
            output.Dispose();
            throw;
        }
    }

    /// <summary>Keeps the encoders' memory below a quarter of what the process may use.</summary>
    private static int LimitThreadsByMemory(int level, int threads)
    {
        var budget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4;

        // A worker holds its own context plus input and output job buffers of similar size.
        var perWorker = (long)Methods.ZSTD_estimateCCtxSize(level) * 2;
        if (budget <= 0 || perWorker <= 0)
        {
            return threads;
        }

        return (int)Math.Clamp(budget / perWorker, 1, threads);
    }

    private sealed class ZstdContainer : IImageContainer
    {
        public ZstdContainer(IStreamSource source, long? length)
        {
            Source = source;
            Entries = [new ImageEntry(0, ContainerNaming.GetInnerName(source.Name), length) { CompressedLength = source.Length }];
        }

        public ContainerFormat Format => ContainerFormat.Zstd;

        public IStreamSource Source { get; }

        public IReadOnlyList<ImageEntry> Entries { get; }

        public ImageEntryReader OpenEntry(ImageEntry entry, CancellationToken cancellationToken = default)
        {
            var counting = new CountingStream(Source.Open(cancellationToken));
            try
            {
                var decompressor = new Decompressor();
                decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxWindowLog);
                var zstd = new DecompressionStream(counting, decompressor, BufferSize, checkEndOfStream: true, preserveDecompressor: false, leaveOpen: false);
                return new ImageEntryReader(zstd, entry.Length, Source.Length, () => counting.BytesRead);
            }
            catch
            {
                counting.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Finishes the frame and closes the file on dispose. When fewer bytes than
    /// pledged were written (the read failed or was cancelled) zstd rejects the
    /// frame; that error is swallowed so it does not hide the original failure.
    /// The frame stays unterminated, so the partial file cannot pass for a valid one.
    /// </summary>
    private sealed class ZstdWriteStream : Stream
    {
        private readonly CompressionStream _zstd;
        private readonly Stream _output;
        private readonly long _expected;
        private long _written;
        private bool _disposed;

        public ZstdWriteStream(CompressionStream zstd, Stream output, long expected)
        {
            _zstd = zstd;
            _output = output;
            _expected = expected;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _zstd.Write(buffer);
            _written += buffer.Length;
        }

        public override void Flush() => _zstd.Flush();

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
                    _zstd.Dispose();
                }
                catch (ZstdException) when (_written != _expected)
                {
                }
                finally
                {
                    _output.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
