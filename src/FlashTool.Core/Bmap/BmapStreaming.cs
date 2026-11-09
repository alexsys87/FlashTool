using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FlashTool.Core.Bmap;

/// <summary>
/// Follows the mapped byte ranges of an image while its data streams past in order:
/// hashes each range and hands over the data between ranges (the holes).
/// </summary>
internal sealed class BmapRangeWalker : IDisposable
{
    private readonly IReadOnlyList<(long Start, long End)> _ranges;
    private readonly IncrementalHash? _hash;
    private long _position;
    private int _index;

    public BmapRangeWalker(IReadOnlyList<(long Start, long End)> ranges, HashAlgorithmName? algorithm)
    {
        _ranges = ranges;
        _hash = algorithm is { } a ? IncrementalHash.CreateHash(a) : null;
    }

    /// <summary>
    /// Feeds the next data. <paramref name="rangeDone"/> gets the index and hash of every range
    /// that ends within it, <paramref name="hole"/> every stretch outside the ranges.
    /// </summary>
    public void Process(ReadOnlySpan<byte> data, Action<int, byte[]?> rangeDone, HoleAction hole)
    {
        while (!data.IsEmpty)
        {
            if (_index >= _ranges.Count)
            {
                hole(_position, data);
                _position += data.Length;
                return;
            }

            var (start, end) = _ranges[_index];
            if (_position < start)
            {
                var n = (int)Math.Min(data.Length, start - _position);
                hole(_position, data[..n]);
                Advance(ref data, n);
                continue;
            }

            var take = (int)Math.Min(data.Length, end - _position);
            _hash?.AppendData(data[..take]);
            Advance(ref data, take);
            if (_position == end)
            {
                rangeDone(_index, _hash?.GetHashAndReset());
                _index++;
            }
        }
    }

    /// <summary>Ranges that started but were not completed (the data ended early).</summary>
    public bool AllRangesDone => _index >= _ranges.Count;

    public void Dispose() => _hash?.Dispose();

    private void Advance(ref ReadOnlySpan<byte> data, int count)
    {
        data = data[count..];
        _position += count;
    }

    public delegate void HoleAction(long offset, ReadOnlySpan<byte> data);
}

/// <summary>
/// Checks that the image is the one the .bmap describes: every range matches its checksum and
/// every hole is zeros. bmaptool maps the holes of a sparse file, so data in a hole means the
/// .bmap belongs to another build of the image.
/// </summary>
internal sealed class BmapImageCheck : IDisposable
{
    private readonly BmapFile _bmap;
    private readonly List<(long Start, long End)> _ranges;
    private readonly BmapRangeWalker _walker;

    public BmapImageCheck(BmapFile bmap)
    {
        _bmap = bmap;
        _ranges = bmap.MappedByteRanges().ToList();
        _walker = new BmapRangeWalker(_ranges, BmapFile.Algorithm(bmap.ChecksumType));
    }

    /// <summary>Feeds the next data in order; throws <see cref="FlashErrorCode.BmapDataMismatch"/> on the first contradiction.</summary>
    public void Process(ReadOnlySpan<byte> data)
        => _walker.Process(
            data,
            (index, hash) =>
            {
                var expected = _bmap.Ranges[index].Checksum;
                if (expected != null && hash != null && !expected.AsSpan().SequenceEqual(hash))
                {
                    throw Mismatch(_ranges[index].Start);
                }
            },
            (offset, hole) =>
            {
                var nonZero = hole.IndexOfAnyExcept((byte)0);
                if (nonZero >= 0)
                {
                    throw Mismatch(offset + nonZero);
                }
            });

    public void Dispose() => _walker.Dispose();

    private static FlashToolException Mismatch(long offset)
        => new(FlashErrorCode.BmapDataMismatch, $"The .bmap file does not describe this image (at byte {offset}).", null, offset);
}

/// <summary>
/// Builds a format 2.0 .bmap with SHA-256 checksums for an image that is being created: the
/// given holes are left out, everything else is mapped. Data is fed in order as it is written.
/// </summary>
internal sealed class BmapBuilder : IDisposable
{
    public const int BlockSize = 4096;

    private readonly long _imageSize;
    private readonly List<(long First, long Last)> _blocks;
    private readonly string?[] _checksums;
    private readonly BmapRangeWalker _walker;

    /// <param name="holes">Byte ranges known to be zeros in the image; rounded inwards to whole blocks.</param>
    public BmapBuilder(long imageSize, IEnumerable<(long Start, long End)> holes)
    {
        _imageSize = imageSize;
        var blocksCount = (imageSize + BlockSize - 1) / BlockSize;
        _blocks = [];
        var next = 0L;
        foreach (var (start, end) in holes.OrderBy(h => h.Start))
        {
            var first = (start + BlockSize - 1) / BlockSize;
            var stop = Math.Min(end, imageSize) == imageSize ? blocksCount : end / BlockSize;
            if (stop <= first)
            {
                continue;
            }

            if (first > next)
            {
                _blocks.Add((next, first - 1));
            }

            next = Math.Max(next, stop);
        }

        if (next < blocksCount)
        {
            _blocks.Add((next, blocksCount - 1));
        }

        _checksums = new string?[_blocks.Count];
        var byteRanges = _blocks.Select(b => (b.First * BlockSize, Math.Min((b.Last + 1) * BlockSize, imageSize))).ToList();
        _walker = new BmapRangeWalker(byteRanges, HashAlgorithmName.SHA256);
    }

    public void Append(ReadOnlySpan<byte> data)
        => _walker.Process(data, (index, hash) => _checksums[index] = Convert.ToHexString(hash!).ToLowerInvariant(), (_, _) => { });

    /// <summary>The finished file, with its own checksum filled in as bmaptool expects.</summary>
    public byte[] ToBytes()
    {
        if (!_walker.AllRangesDone)
        {
            throw new InvalidOperationException("Not all of the image has been seen yet.");
        }

        var blocksCount = (_imageSize + BlockSize - 1) / BlockSize;
        var mapped = _blocks.Sum(b => b.Last - b.First + 1);
        var zeros = new string('0', 64);
        var text = new StringBuilder();
        text.Append("<?xml version=\"1.0\" ?>\n");
        text.Append("<!-- Block map of an image created by FlashTool, in the format of bmaptool.\n");
        text.Append(CultureInfo.InvariantCulture, $"     Image size {_imageSize} bytes, {mapped} of {blocksCount} blocks of {BlockSize} bytes hold data;\n");
        text.Append("     the rest are zeros that do not have to be written. -->\n");
        text.Append("<bmap version=\"2.0\">\n");
        text.Append(CultureInfo.InvariantCulture, $"    <ImageSize> {_imageSize} </ImageSize>\n");
        text.Append(CultureInfo.InvariantCulture, $"    <BlockSize> {BlockSize} </BlockSize>\n");
        text.Append(CultureInfo.InvariantCulture, $"    <BlocksCount> {blocksCount} </BlocksCount>\n");
        text.Append(CultureInfo.InvariantCulture, $"    <MappedBlocksCount> {mapped} </MappedBlocksCount>\n");
        text.Append("    <ChecksumType> sha256 </ChecksumType>\n");
        text.Append(CultureInfo.InvariantCulture, $"    <BmapFileChecksum> {zeros} </BmapFileChecksum>\n");
        text.Append("    <BlockMap>\n");
        for (var i = 0; i < _blocks.Count; i++)
        {
            var (first, last) = _blocks[i];
            var range = first == last ? first.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{first}-{last}");
            text.Append(CultureInfo.InvariantCulture, $"        <Range chksum=\"{_checksums[i]}\"> {range} </Range>\n");
        }

        text.Append("    </BlockMap>\n");
        text.Append("</bmap>\n");

        var bytes = Encoding.ASCII.GetBytes(text.ToString());
        var placeholder = Encoding.ASCII.GetBytes(zeros);
        var position = bytes.AsSpan().IndexOf(placeholder);
        var checksum = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        checksum.CopyTo(bytes, position);
        return bytes;
    }

    public void Dispose() => _walker.Dispose();
}
