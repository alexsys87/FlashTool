using FlashTool.Core.IO;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Containers;

/// <summary>Uncompressed image: the file content is the disk content.</summary>
public sealed class RawContainerHandler : IContainerHandler
{
    public ContainerFormat Format => ContainerFormat.Raw;

    public IReadOnlyList<string> Extensions { get; } = [".img", ".iso", ".bin", ".raw", ".dd"];

    public bool IsAvailable => true;

    public bool CanCreate => true;

    public bool RequiresSeekableSource => false;

    /// <summary>Raw is the fallback and never claims a header.</summary>
    public bool IsMatch(ReadOnlySpan<byte> header) => false;

    public IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default) => new RawContainer(source);

    public Stream Create(Stream output, string entryName, long length, CompressionSettings settings)
    {
        if (output.CanSeek)
        {
            // Pre-allocating reduces fragmentation of multi-gigabyte files.
            output.SetLength(length);
        }

        return new OwningWriteStream(output, null);
    }

    private sealed class RawContainer : IImageContainer
    {
        public RawContainer(IStreamSource source)
        {
            Source = source;
            Entries = [new ImageEntry(0, source.Name, source.Length) { CompressedLength = source.Length }];
        }

        public ContainerFormat Format => ContainerFormat.Raw;

        public IStreamSource Source { get; }

        public IReadOnlyList<ImageEntry> Entries { get; }

        public ImageEntryReader OpenEntry(ImageEntry entry, CancellationToken cancellationToken = default)
        {
            var counting = new CountingStream(Source.Open(cancellationToken));
            return new ImageEntryReader(counting, Source.Length, Source.Length, () => counting.BytesRead);
        }

        public void Dispose()
        {
        }
    }
}
