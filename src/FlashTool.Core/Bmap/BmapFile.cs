using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FlashTool.Core.Bmap;

public enum BmapChecksumType
{
    /// <summary>Format versions before 1.3 carry no checksums.</summary>
    None,
    Sha1,
    Sha256,
}

/// <summary>Why a .bmap file cannot be used.</summary>
public enum BmapProblem
{
    None,

    /// <summary>Not well-formed XML, or a required element is missing or not a number.</summary>
    Malformed,

    /// <summary>A format version newer than 2.x.</summary>
    UnsupportedVersion,

    /// <summary>A checksum type other than SHA-1 or SHA-256.</summary>
    UnsupportedChecksum,

    /// <summary>The checksum of the .bmap file itself does not match: it was edited or damaged.</summary>
    FileChecksumMismatch,

    /// <summary>Sizes and ranges contradict each other (block count, mapped count, overlapping ranges).</summary>
    Inconsistent,

    /// <summary>Larger than any real block map.</summary>
    TooLarge,
}

/// <summary>Blocks <see cref="FirstBlock"/>..<see cref="LastBlock"/> (inclusive) hold data.</summary>
/// <param name="Checksum">Hash of the range's data, when the format has one.</param>
public sealed record BmapRange(long FirstBlock, long LastBlock, byte[]? Checksum);

/// <summary>
/// A block map as written by bmaptool (and by Yocto's wic): the image size and the block
/// ranges that hold data. Everything else is a hole, i.e. zeros in the image, which does not
/// have to be written to the device. Format versions 1.0 to 2.0 are read; 2.0 is written.
/// </summary>
public sealed class BmapFile
{
    /// <summary>Real maps are a few hundred kilobytes; this leaves plenty of room.</summary>
    public const int MaxFileSize = 32 * 1024 * 1024;

    private BmapFile(string version, long imageSize, int blockSize, long blocksCount, long mappedBlocksCount, BmapChecksumType checksumType, IReadOnlyList<BmapRange> ranges)
    {
        Version = version;
        ImageSize = imageSize;
        BlockSize = blockSize;
        BlocksCount = blocksCount;
        MappedBlocksCount = mappedBlocksCount;
        ChecksumType = checksumType;
        Ranges = ranges;
    }

    public string Version { get; }

    /// <summary>Size of the uncompressed image in bytes.</summary>
    public long ImageSize { get; }

    public int BlockSize { get; }

    public long BlocksCount { get; }

    public long MappedBlocksCount { get; }

    public BmapChecksumType ChecksumType { get; }

    /// <summary>Mapped ranges in ascending order, not overlapping.</summary>
    public IReadOnlyList<BmapRange> Ranges { get; }

    /// <summary>Bytes of the image that hold data (the last block may be partial).</summary>
    public long MappedBytes => MappedByteRanges().Sum(r => r.End - r.Start);

    /// <summary>Mapped ranges as byte offsets [Start, End) within the image.</summary>
    public IEnumerable<(long Start, long End)> MappedByteRanges()
        => Ranges.Select(r => (r.FirstBlock * BlockSize, Math.Min((r.LastBlock + 1) * BlockSize, ImageSize)));

    public static BmapFile Parse(byte[] content)
        => TryParse(content, out var bmap, out var problem) ? bmap : throw new FormatException($"Unusable .bmap file: {problem}.");

    /// <summary>Reads and fully validates a .bmap file, including its own checksum.</summary>
    public static bool TryParse(byte[] content, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BmapFile? bmap, out BmapProblem problem)
    {
        ArgumentNullException.ThrowIfNull(content);
        bmap = null;
        if (content.Length > MaxFileSize)
        {
            problem = BmapProblem.TooLarge;
            return false;
        }

        XElement root;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxFileSize,
            };
            using var reader = XmlReader.Create(new MemoryStream(content, writable: false), settings);
            root = XDocument.Load(reader).Root ?? throw new XmlException("No root element.");
        }
        catch (XmlException)
        {
            problem = BmapProblem.Malformed;
            return false;
        }

        try
        {
            problem = Read(root, content, out bmap);
            return problem == BmapProblem.None;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            problem = BmapProblem.Malformed;
            return false;
        }
    }

    private static BmapProblem Read(XElement root, byte[] content, out BmapFile? bmap)
    {
        bmap = null;
        if (root.Name.LocalName != "bmap")
        {
            return BmapProblem.Malformed;
        }

        var version = ((string?)root.Attribute("version") ?? string.Empty).Trim();
        var parts = version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return BmapProblem.Malformed;
        }

        if (major > 2)
        {
            return BmapProblem.UnsupportedVersion;
        }

        var imageSize = Number(root, "ImageSize");
        var blockSize = Number(root, "BlockSize");
        var blocksCount = Number(root, "BlocksCount");
        var mappedCount = Number(root, "MappedBlocksCount");
        if (imageSize < 0 || blockSize is <= 0 or > 64 * 1024 * 1024 || blocksCount != (imageSize + blockSize - 1) / blockSize)
        {
            return BmapProblem.Inconsistent;
        }

        // 1.4 is 2.0 released under the wrong number. 1.3 had SHA-1 only; earlier versions had no checksums.
        var checksumType = BmapChecksumType.None;
        string? rangeAttribute = null;
        string? fileChecksumElement = null;
        if (major == 2 || (major == 1 && minor == 4))
        {
            checksumType = Text(root, "ChecksumType").ToLowerInvariant() switch
            {
                "sha1" => BmapChecksumType.Sha1,
                "sha256" => BmapChecksumType.Sha256,
                _ => (BmapChecksumType)(-1),
            };
            if ((int)checksumType < 0)
            {
                return BmapProblem.UnsupportedChecksum;
            }

            rangeAttribute = "chksum";
            fileChecksumElement = "BmapFileChecksum";
        }
        else if (major == 1 && minor == 3)
        {
            checksumType = BmapChecksumType.Sha1;
            rangeAttribute = "sha1";
            fileChecksumElement = "BmapFileSHA1";
        }

        if (fileChecksumElement != null && !FileChecksumMatches(content, Text(root, fileChecksumElement), checksumType))
        {
            return BmapProblem.FileChecksumMismatch;
        }

        var blockMap = root.Element("BlockMap") ?? throw new FormatException("No BlockMap.");
        var ranges = new List<BmapRange>();
        long mapped = 0;
        var previousLast = -1L;
        foreach (var element in blockMap.Elements("Range"))
        {
            var bounds = element.Value.Split('-', 2, StringSplitOptions.TrimEntries);
            var first = long.Parse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture);
            var last = bounds.Length > 1 ? long.Parse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture) : first;
            if (first > last || first <= previousLast || last >= blocksCount)
            {
                return BmapProblem.Inconsistent;
            }

            byte[]? checksum = null;
            if (rangeAttribute != null && (string?)element.Attribute(rangeAttribute) is { } hex)
            {
                checksum = Convert.FromHexString(hex.Trim());
                if (checksum.Length != HashLength(checksumType))
                {
                    return BmapProblem.Malformed;
                }
            }

            ranges.Add(new BmapRange(first, last, checksum));
            mapped += last - first + 1;
            previousLast = last;
        }

        if (mapped != mappedCount)
        {
            return BmapProblem.Inconsistent;
        }

        bmap = new BmapFile(version, imageSize, (int)blockSize, blocksCount, mappedCount, checksumType, ranges);
        return BmapProblem.None;
    }

    /// <summary>
    /// As bmaptool checks it: the file is hashed with the stored checksum replaced by ASCII zeros.
    /// </summary>
    private static bool FileChecksumMatches(byte[] content, string stored, BmapChecksumType type)
    {
        var expected = Encoding.ASCII.GetBytes(stored);
        if (expected.Length != HashLength(type) * 2)
        {
            return false;
        }

        var position = content.AsSpan().IndexOf(expected);
        if (position < 0)
        {
            return false;
        }

        var copy = (byte[])content.Clone();
        copy.AsSpan(position, expected.Length).Fill((byte)'0');
        var actual = Convert.ToHexString(Hash(type, copy));
        return actual.Equals(stored, StringComparison.OrdinalIgnoreCase);
    }

    internal static int HashLength(BmapChecksumType type) => type switch
    {
        BmapChecksumType.Sha1 => 20,
        BmapChecksumType.Sha256 => 32,
        _ => 0,
    };

    internal static HashAlgorithmName? Algorithm(BmapChecksumType type) => type switch
    {
        BmapChecksumType.Sha1 => HashAlgorithmName.SHA1,
        BmapChecksumType.Sha256 => HashAlgorithmName.SHA256,
        _ => null,
    };

    private static byte[] Hash(BmapChecksumType type, byte[] data)
#pragma warning disable CA5350 // SHA-1 is what format 1.3 uses; it only detects damage here.
        => type == BmapChecksumType.Sha1 ? SHA1.HashData(data) : SHA256.HashData(data);
#pragma warning restore CA5350

    private static string Text(XElement root, string name)
        => (root.Element(name) ?? throw new FormatException($"No {name}.")).Value.Trim();

    private static long Number(XElement root, string name)
        => long.Parse(Text(root, name), NumberStyles.None, CultureInfo.InvariantCulture);
}
