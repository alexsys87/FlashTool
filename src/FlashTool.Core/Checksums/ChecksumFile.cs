using System.Text;

namespace FlashTool.Core.Checksums;

/// <summary>
/// Reads SHA-256 values out of the checksum files that projects publish next to
/// their images: <c>sha256sum</c> output (<c>SHA256SUMS</c>), BSD style lines and
/// files that hold nothing but the hash. Also writes the one-line file that goes
/// next to an image FlashTool has created.
/// </summary>
public static class ChecksumFile
{
    /// <summary>Where the checksum file of an image lives: the image's own name plus <c>.sha256</c>.</summary>
    public static string SidecarPath(string imagePath) => imagePath + ".sha256";

    /// <summary>
    /// The line <c>sha256sum</c> prints, which <c>sha256sum -c</c> accepts when it is run in the directory of the image.
    /// </summary>
    public static string Format(ReadOnlySpan<byte> sha256, string fileName) => $"{Sha256Hex.Format(sha256)}  {fileName}\n";

    /// <summary>Writes the checksum file next to <paramref name="imagePath"/> and returns its path.</summary>
    public static string WriteSidecar(string imagePath, ReadOnlySpan<byte> sha256)
    {
        var path = SidecarPath(imagePath);
        File.WriteAllText(path, Format(sha256, Path.GetFileName(imagePath)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>
    /// Finds the value listed for <paramref name="fileName"/>. Names are compared
    /// without their directory part and ignoring case.
    /// </summary>
    /// <param name="content">Text of the checksum file.</param>
    /// <param name="fileName">Name of the image file.</param>
    /// <param name="acceptSingleEntry">
    /// Take the only entry of the file whatever name it carries. Right for a
    /// sidecar that is paired with the image by its own name (<c>image.img.xz.sha256</c>),
    /// wrong for a shared list such as <c>SHA256SUMS</c>.
    /// </param>
    /// <param name="sha256">The value that was found.</param>
    public static bool TryFind(string content, string fileName, bool acceptSingleEntry, out byte[] sha256)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);

        var entries = Parse(content);
        foreach (var (name, hash) in entries)
        {
            if (name != null && string.Equals(BaseName(name), fileName, StringComparison.OrdinalIgnoreCase))
            {
                sha256 = hash;
                return true;
            }
        }

        if (acceptSingleEntry && entries.Count == 1)
        {
            sha256 = entries[0].Hash;
            return true;
        }

        sha256 = [];
        return false;
    }

    private static List<(string? Name, byte[] Hash)> Parse(string content)
    {
        var entries = new List<(string? Name, byte[] Hash)>();
        foreach (var raw in content.Split('\n'))
        {
            // A byte order mark would hide the first hash.
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (TryParseBsd(line, out var bsdName, out var bsdHash))
            {
                entries.Add((bsdName, bsdHash));
            }
            else if (TryParseGnu(line, out var gnuName, out var gnuHash))
            {
                entries.Add((gnuName, gnuHash));
            }
            else if (Sha256Hex.TryParse(line, out var bareHash))
            {
                entries.Add((null, bareHash));
            }
        }

        return entries;
    }

    /// <summary><c>SHA256 (name) = hash</c></summary>
    private static bool TryParseBsd(string line, out string name, out byte[] hash)
    {
        name = string.Empty;
        hash = [];
        if (!line.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var open = line.IndexOf('(', StringComparison.Ordinal);
        var close = line.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return false;
        }

        var rest = line[(close + 1)..].TrimStart();
        if (rest.Length == 0 || rest[0] != '=' || !Sha256Hex.TryParse(rest[1..], out hash))
        {
            return false;
        }

        name = line[(open + 1)..close];
        return true;
    }

    /// <summary><c>hash  name</c> or <c>hash *name</c> (the star marks binary mode).</summary>
    private static bool TryParseGnu(string line, out string name, out byte[] hash)
    {
        name = string.Empty;
        hash = [];
        const int hexLength = Sha256Hex.ByteLength * 2;
        if (line.Length <= hexLength + 1 || !char.IsWhiteSpace(line[hexLength]) || !Sha256Hex.TryParse(line[..hexLength], out hash))
        {
            return false;
        }

        name = line[hexLength..].TrimStart().TrimStart('*');
        return name.Length > 0;
    }

    private static string BaseName(string name)
    {
        var slash = name.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? name : name[(slash + 1)..];
    }
}
