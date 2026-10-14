using FlashTool.Core.IO;
using FlashTool.Core.Sources;
using SevenZip;

namespace FlashTool.Core.Containers;

/// <summary>
/// 7z archives through the native 7z.dll (via SevenZipSharp). The library
/// only offers push-style APIs, so <see cref="StreamPipe"/> bridges them to
/// streams: data still flows in memory only, never through temp files.
/// </summary>
public sealed class SevenZipContainerHandler : IContainerHandler
{
    private static readonly object InitLock = new();
    private static string? _libraryPath;

    internal static ReadOnlySpan<byte> Magic => [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];

    public ContainerFormat Format => ContainerFormat.SevenZip;

    public IReadOnlyList<string> Extensions { get; } = [".7z"];

    public bool IsAvailable
    {
        get
        {
            lock (InitLock)
            {
                return _libraryPath != null;
            }
        }
    }

    public bool CanCreate => true;

    public bool RequiresSeekableSource => true;

    /// <summary>
    /// Registers the path to 7z.dll. Called once at startup by the host
    /// application; without it the format is reported as unavailable.
    /// </summary>
    public static bool Initialize(string libraryPath)
    {
        if (!File.Exists(libraryPath))
        {
            return false;
        }

        lock (InitLock)
        {
            SevenZipBase.SetLibraryPath(libraryPath);
            _libraryPath = libraryPath;
            return true;
        }
    }

    public bool IsMatch(ReadOnlySpan<byte> header) => header.Length >= 6 && header[..6].SequenceEqual(Magic);

    public IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default)
    {
        RequireBackend();
        return new SevenZipContainer(source, cancellationToken);
    }

    public Stream Create(Stream output, string entryName, long length, CompressionSettings settings)
    {
        RequireBackend();
        var compressor = new SevenZipCompressor
        {
            ArchiveFormat = OutArchiveFormat.SevenZip,
            CompressionMethod = CompressionMethod.Lzma2,
            CompressionMode = CompressionMode.Create,
            CompressionLevel = settings.Preset switch
            {
                CompressionPreset.Fastest => CompressionLevel.Fast,
                CompressionPreset.Fast => CompressionLevel.Low,
                CompressionPreset.Maximum => CompressionLevel.Ultra,
                _ => CompressionLevel.Normal,
            },
            FastCompression = true,
        };
        compressor.CustomParameters["mt"] = settings.EffectiveThreads.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var pipe = new StreamPipe();
        var reader = pipe.CreateReader(length);
        var writer = pipe.CreateWriter();
        var task = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    compressor.CompressStreamDictionary(new Dictionary<string, Stream> { [entryName] = reader }, output);
                }
                finally
                {
                    // Unblocks the producer if 7z stopped reading early.
                    reader.Dispose();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return new OwningWriteStream(writer, () => task, output);
    }

    private static void RequireBackend()
    {
        lock (InitLock)
        {
            if (_libraryPath == null)
            {
                throw new FlashToolException(FlashErrorCode.ContainerBackendUnavailable, "7z.dll is not available.", null, "7z");
            }
        }
    }

    private sealed class SevenZipContainer : IImageContainer
    {
        private readonly SevenZipExtractor _extractor;
        private readonly Stream _stream;

        public SevenZipContainer(IStreamSource source, CancellationToken cancellationToken)
        {
            Source = source;
            _stream = source.Open(cancellationToken);
            try
            {
                _extractor = new SevenZipExtractor(_stream, leaveOpen: true);
                Entries = _extractor.ArchiveFileData
                    .Where(f => !f.IsDirectory && f.Size > 0)
                    .Select(f => new ImageEntry(f.Index, f.FileName, (long)f.Size))
                    .ToList();
            }
            catch (SevenZipException ex)
            {
                _stream.Dispose();
                throw new FlashToolException(FlashErrorCode.UnsupportedContainer, ex.Message, ex);
            }
        }

        public ContainerFormat Format => ContainerFormat.SevenZip;

        public IStreamSource Source { get; }

        public IReadOnlyList<ImageEntry> Entries { get; }

        public ImageEntryReader OpenEntry(ImageEntry entry, CancellationToken cancellationToken = default)
        {
            var pipe = new StreamPipe();
            var writer = pipe.CreateWriter();
            var reader = pipe.CreateReader(entry.Length);

            var task = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        _extractor.ExtractFile(entry.Index, writer);
                        writer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        // Surfaces on the consumer side; ignored if the consumer already left.
                        pipe.FailWriter(ex is SevenZipException ? new FlashToolException(FlashErrorCode.UnsupportedContainer, ex.Message, ex) : ex);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            return new ImageEntryReader(reader, entry.Length, owner: new ExtractionHandle(reader, task));
        }

        public void Dispose()
        {
            _extractor.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>Stops the extraction thread when the consumer is done.</summary>
    private sealed class ExtractionHandle(Stream reader, Task task) : IDisposable
    {
        public void Dispose()
        {
            reader.Dispose();
            try
            {
                task.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
                // Errors were already delivered through the pipe.
            }
        }
    }
}
