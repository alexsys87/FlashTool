using FlashTool.Core.Sources;

namespace FlashTool.Core.Containers;

/// <summary>Physical layout of an image file.</summary>
public enum ContainerFormat
{
    /// <summary>Plain uncompressed image (.img, .iso, .bin, ...).</summary>
    Raw,
    Zip,
    Xz,
    Gzip,
    SevenZip,
}

public enum CompressionPreset
{
    Fastest,
    Fast,
    Normal,
    Maximum,
}

/// <summary>Settings for creating compressed images.</summary>
/// <param name="Preset">Speed/size trade-off.</param>
/// <param name="Threads">Worker threads; 0 picks a value automatically.</param>
public sealed record CompressionSettings(CompressionPreset Preset = CompressionPreset.Normal, int Threads = 0)
{
    public int EffectiveThreads => Threads > 0 ? Threads : Math.Clamp(Environment.ProcessorCount, 1, 16);
}

/// <summary>A file inside a container that may be a disk image.</summary>
/// <param name="Index">Container-specific index.</param>
/// <param name="Name">Path of the file inside the container.</param>
/// <param name="Length">Uncompressed size, when known.</param>
public sealed record ImageEntry(int Index, string Name, long? Length)
{
    public long? CompressedLength { get; init; }
}

/// <summary>
/// Forward-only stream of uncompressed image data for one entry.
/// </summary>
public sealed class ImageEntryReader : IDisposable
{
    private readonly Func<long>? _compressedPosition;
    private readonly IDisposable? _owner;

    public ImageEntryReader(Stream data, long? length, long? compressedLength = null, Func<long>? compressedPosition = null, IDisposable? owner = null)
    {
        Data = data;
        Length = length;
        CompressedLength = compressedLength;
        _compressedPosition = compressedPosition;
        _owner = owner;
    }

    public Stream Data { get; }

    /// <summary>Uncompressed length when known in advance.</summary>
    public long? Length { get; }

    /// <summary>Size of the compressed input, used as progress fallback when <see cref="Length"/> is unknown.</summary>
    public long? CompressedLength { get; }

    public long CompressedPosition => _compressedPosition?.Invoke() ?? 0;

    public void Dispose()
    {
        Data.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>An opened image file (plain or archive).</summary>
public interface IImageContainer : IDisposable
{
    ContainerFormat Format { get; }

    IStreamSource Source { get; }

    IReadOnlyList<ImageEntry> Entries { get; }

    /// <summary>Opens a decompressing stream for an entry. Only one entry may be open at a time.</summary>
    ImageEntryReader OpenEntry(ImageEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Plug-in point for a container format: detection, reading and (optionally)
/// streaming creation.
/// </summary>
public interface IContainerHandler
{
    ContainerFormat Format { get; }

    /// <summary>File extensions, the first one is used when creating files.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>False when a native backend (e.g. 7z.dll) is missing.</summary>
    bool IsAvailable { get; }

    bool CanCreate { get; }

    /// <summary>True when the archive directory has to be read with random access (zip, 7z).</summary>
    bool RequiresSeekableSource { get; }

    bool IsMatch(ReadOnlySpan<byte> header);

    IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a new container in <paramref name="output"/> holding one entry and
    /// returns the stream to write the uncompressed data into. Data is
    /// compressed on the fly; disposing the returned stream finalizes the
    /// container and disposes <paramref name="output"/>.
    /// </summary>
    Stream Create(Stream output, string entryName, long length, CompressionSettings settings);
}
