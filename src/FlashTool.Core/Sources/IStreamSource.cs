namespace FlashTool.Core.Sources;

/// <summary>
/// Where image bytes come from: a local file or a URL. Every call to
/// <see cref="Open"/> returns an independent stream positioned at 0, so a
/// container can re-read its data (inspection first, writing later).
/// </summary>
public interface IStreamSource
{
    /// <summary>File name used for display and for naming entries (e.g. "raspios.img.xz").</summary>
    string Name { get; }

    /// <summary>Full path or URL.</summary>
    string Location { get; }

    /// <summary>Size of the raw (possibly compressed) data, when known.</summary>
    long? Length { get; }

    /// <summary>Whether streams returned by <see cref="Open"/> support seeking.</summary>
    bool IsSeekable { get; }

    bool IsRemote { get; }

    Stream Open(CancellationToken cancellationToken = default);
}
