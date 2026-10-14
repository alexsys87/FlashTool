using FlashTool.Core.IO;
using FlashTool.Core.Sources;
using Joveler.Compression.XZ;

namespace FlashTool.Core.Containers;

/// <summary>
/// .xz streams through native liblzma (multi-threaded compression and decompression).
/// </summary>
public sealed class XzContainerHandler : IContainerHandler
{
    private static readonly object InitLock = new();
    private static bool? _available;

    internal static ReadOnlySpan<byte> Magic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    public ContainerFormat Format => ContainerFormat.Xz;

    public IReadOnlyList<string> Extensions { get; } = [".xz"];

    public bool IsAvailable => EnsureInitialized();

    public bool CanCreate => true;

    public bool RequiresSeekableSource => false;

    public bool IsMatch(ReadOnlySpan<byte> header) => header.Length >= 6 && header[..6].SequenceEqual(Magic);

    public IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default)
    {
        RequireBackend();
        long? length = null;
        if (source.IsSeekable)
        {
            using var probe = source.Open(cancellationToken);
            length = XzIndexReader.TryGetUncompressedSize(probe);
        }

        return new XzContainer(source, length);
    }

    public Stream Create(Stream output, string entryName, long length, CompressionSettings settings)
    {
        RequireBackend();
        var level = settings.Preset switch
        {
            CompressionPreset.Fastest => LzmaCompLevel.Level0,
            CompressionPreset.Fast => LzmaCompLevel.Level3,
            CompressionPreset.Maximum => LzmaCompLevel.Level9,
            _ => LzmaCompLevel.Level6,
        };

        var compress = new XZCompressOptions { Level = level, LeaveOpen = false, BufferSize = 4 * 1024 * 1024 };
        var threads = LimitThreadsByMemory(compress, settings.EffectiveThreads);
        var xz = new XZStream(output, compress, new XZParallelCompressOptions { Threads = threads });
        return new OwningWriteStream(xz, null);
    }

    /// <summary>
    /// Each encoder thread may need hundreds of megabytes at high presets;
    /// keep the total below a quarter of physical memory.
    /// </summary>
    private static int LimitThreadsByMemory(XZCompressOptions options, int threads)
    {
        try
        {
            var budget = XZHardware.PhysMem() / 4;
            while (threads > 1 && XZMemory.ParallelEncoderMemUsage(options, new XZParallelCompressOptions { Threads = threads }) > budget)
            {
                threads--;
            }
        }
        catch (XZException)
        {
            threads = 1;
        }

        return threads;
    }

    private static void RequireBackend()
    {
        if (!EnsureInitialized())
        {
            throw new FlashToolException(FlashErrorCode.ContainerBackendUnavailable, "liblzma could not be loaded.", null, "xz");
        }
    }

    private static bool EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_available.HasValue)
            {
                return _available.Value;
            }

            try
            {
                var path = NativeLibraryLocator.Find(OperatingSystem.IsWindows() ? "liblzma.dll" : OperatingSystem.IsMacOS() ? "liblzma.dylib" : "liblzma.so");
                if (path != null)
                {
                    XZInit.GlobalInit(path);
                }
                else
                {
                    XZInit.GlobalInit();
                }

                _available = true;
            }
            catch (InvalidOperationException)
            {
                // Already initialized by someone else.
                _available = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                _available = false;
            }

            return _available.Value;
        }
    }

    private sealed class XzContainer : IImageContainer
    {
        public XzContainer(IStreamSource source, long? length)
        {
            Source = source;
            Entries = [new ImageEntry(0, ContainerNaming.GetInnerName(source.Name), length) { CompressedLength = source.Length }];
        }

        public ContainerFormat Format => ContainerFormat.Xz;

        public IStreamSource Source { get; }

        public IReadOnlyList<ImageEntry> Entries { get; }

        public ImageEntryReader OpenEntry(ImageEntry entry, CancellationToken cancellationToken = default)
        {
            var counting = new CountingStream(Source.Open(cancellationToken));
            try
            {
                var xz = new XZStream(
                    counting,
                    new XZDecompressOptions { LeaveOpen = false, BufferSize = 4 * 1024 * 1024 },
                    new XZParallelDecompressOptions { Threads = Math.Clamp(Environment.ProcessorCount, 1, 16) });
                return new ImageEntryReader(xz, entry.Length, Source.Length, () => counting.BytesRead);
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
}
