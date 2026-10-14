using FlashTool.Core.IO;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Containers;

/// <summary>
/// Knows every supported container format. New formats are added by
/// registering another <see cref="IContainerHandler"/>.
/// </summary>
public sealed class ContainerRegistry
{
    private const int HeaderProbeSize = 512;

    public ContainerRegistry(IEnumerable<IContainerHandler> handlers)
    {
        Handlers = handlers.ToList();
        if (!Handlers.Any(h => h.Format == ContainerFormat.Raw))
        {
            throw new ArgumentException("The raw handler is mandatory.", nameof(handlers));
        }
    }

    public static ContainerRegistry Default { get; } = new(
    [
        new XzContainerHandler(),
        new ZipContainerHandler(),
        new SevenZipContainerHandler(),
        new GzipContainerHandler(),
        new RawContainerHandler(),
    ]);

    public IReadOnlyList<IContainerHandler> Handlers { get; }

    public IContainerHandler Get(ContainerFormat format) => Handlers.First(h => h.Format == format);

    /// <summary>Detects the format by magic bytes; falls back to raw.</summary>
    public IContainerHandler Detect(ReadOnlySpan<byte> header)
    {
        foreach (var handler in Handlers)
        {
            if (handler.IsMatch(header))
            {
                return handler;
            }
        }

        return Get(ContainerFormat.Raw);
    }

    /// <summary>Chooses the format for a new file from its extension ("backup.img.xz" -> xz).</summary>
    public IContainerHandler FromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return Handlers.FirstOrDefault(h => h.Format != ContainerFormat.Raw && h.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            ?? Get(ContainerFormat.Raw);
    }

    /// <summary>Opens a source, detecting its container format from content.</summary>
    public IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderProbeSize];
        int read;
        using (var probe = source.Open(cancellationToken))
        {
            read = probe.ReadUpTo(header);
        }

        var handler = Detect(header.AsSpan(0, read));
        if (!handler.IsAvailable)
        {
            throw new FlashToolException(FlashErrorCode.ContainerBackendUnavailable, $"{handler.Format} support is not available.", null, handler.Format.ToString());
        }

        if (handler.RequiresSeekableSource && !source.IsSeekable)
        {
            throw new FlashToolException(FlashErrorCode.SourceNotSeekable, $"{handler.Format} needs random access but the source does not support it.", null, handler.Format.ToString());
        }

        return handler.Open(source, cancellationToken);
    }
}
