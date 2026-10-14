using System.IO.Compression;
using FlashTool.Core.IO;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Containers;

/// <summary>gzip stream (.img.gz). Single entry, uncompressed size unknown in advance.</summary>
public sealed class GzipContainerHandler : IContainerHandler
{
    public ContainerFormat Format => ContainerFormat.Gzip;

    public IReadOnlyList<string> Extensions { get; } = [".gz"];

    public bool IsAvailable => true;

    public bool CanCreate => true;

    public bool RequiresSeekableSource => false;

    public bool IsMatch(ReadOnlySpan<byte> header) => header.Length >= 3 && header[0] == 0x1F && header[1] == 0x8B && header[2] == 0x08;

    public IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default) => new GzipContainer(source);

    public Stream Create(Stream output, string entryName, long length, CompressionSettings settings)
    {
        var level = settings.Preset switch
        {
            CompressionPreset.Fastest or CompressionPreset.Fast => CompressionLevel.Fastest,
            CompressionPreset.Maximum => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        return new OwningWriteStream(new GZipStream(output, level, leaveOpen: false), null);
    }

    private sealed class GzipContainer : IImageContainer
    {
        public GzipContainer(IStreamSource source)
        {
            Source = source;

            // ISIZE in the trailer is modulo 2^32, useless for multi-gigabyte images.
            Entries = [new ImageEntry(0, ContainerNaming.GetInnerName(source.Name), null) { CompressedLength = source.Length }];
        }

        public ContainerFormat Format => ContainerFormat.Gzip;

        public IStreamSource Source { get; }

        public IReadOnlyList<ImageEntry> Entries { get; }

        public ImageEntryReader OpenEntry(ImageEntry entry, CancellationToken cancellationToken = default)
        {
            var counting = new CountingStream(Source.Open(cancellationToken));
            var gzip = new GZipStream(counting, CompressionMode.Decompress, leaveOpen: false);
            return new ImageEntryReader(gzip, null, Source.Length, () => counting.BytesRead);
        }

        public void Dispose()
        {
        }
    }
}
