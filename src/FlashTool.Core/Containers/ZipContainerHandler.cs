using System.IO.Compression;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Containers;

/// <summary>ZIP archives (deflate / stored), possibly holding several images.</summary>
public sealed class ZipContainerHandler : IContainerHandler
{
    public ContainerFormat Format => ContainerFormat.Zip;

    public IReadOnlyList<string> Extensions { get; } = [".zip"];

    public bool IsAvailable => true;

    public bool CanCreate => true;

    public bool RequiresSeekableSource => true;

    public bool IsMatch(ReadOnlySpan<byte> header)
        => header.Length >= 4 && header[0] == 'P' && header[1] == 'K' && header[2] is 3 or 5 && header[3] is 4 or 6;

    public IImageContainer Open(IStreamSource source, CancellationToken cancellationToken = default) => new ZipImageContainer(source, cancellationToken);

    public Stream Create(Stream output, string entryName, long length, CompressionSettings settings)
    {
        var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        try
        {
            var level = settings.Preset switch
            {
                CompressionPreset.Fastest or CompressionPreset.Fast => CompressionLevel.Fastest,
                CompressionPreset.Maximum => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal,
            };

            var entry = archive.CreateEntry(entryName, level);
            entry.LastWriteTime = DateTimeOffset.Now;
            return new OwningWriteStream(entry.Open(), null, archive);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private sealed class ZipImageContainer : IImageContainer
    {
        private readonly Stream _stream;
        private readonly ZipArchive _archive;
        private readonly List<ZipArchiveEntry> _zipEntries;

        public ZipImageContainer(IStreamSource source, CancellationToken cancellationToken)
        {
            Source = source;
            _stream = source.Open(cancellationToken);
            try
            {
                _archive = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: false);
            }
            catch (InvalidDataException ex)
            {
                _stream.Dispose();
                throw new FlashToolException(FlashErrorCode.UnsupportedContainer, ex.Message, ex);
            }

            _zipEntries = _archive.Entries.Where(e => !e.FullName.EndsWith('/') && e.Length > 0).ToList();
            Entries = _zipEntries
                .Select((e, i) => new ImageEntry(i, e.FullName, e.Length) { CompressedLength = e.CompressedLength })
                .ToList();
        }

        public ContainerFormat Format => ContainerFormat.Zip;

        public IStreamSource Source { get; }

        public IReadOnlyList<ImageEntry> Entries { get; }

        public ImageEntryReader OpenEntry(ImageEntry entry, CancellationToken cancellationToken = default)
        {
            var zipEntry = _zipEntries[entry.Index];
            Stream data;
            try
            {
                data = zipEntry.Open();
            }
            catch (InvalidDataException ex)
            {
                // e.g. an entry compressed with LZMA, bzip2 or zstd.
                throw new FlashToolException(FlashErrorCode.UnsupportedCompressionMethod, ex.Message, ex);
            }

            return new ImageEntryReader(data, zipEntry.Length);
        }

        public void Dispose() => _archive.Dispose();
    }
}
