namespace FlashTool.Core.Checksums;

/// <summary>
/// SHA-256 values as people write them: 64 hex digits in any case, optionally
/// prefixed with <c>sha256:</c>.
/// </summary>
public static class Sha256Hex
{
    /// <summary>Length of a SHA-256 value in bytes.</summary>
    public const int ByteLength = 32;

    private const string Prefix = "sha256:";

    /// <summary>Parses a value typed or pasted by the user; surrounding white space is ignored.</summary>
    public static bool TryParse(string? text, out byte[] hash)
    {
        hash = [];
        if (text is null)
        {
            return false;
        }

        var digits = text.AsSpan().Trim();
        if (digits.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[Prefix.Length..].TrimStart();
        }

        if (digits.Length != ByteLength * 2)
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        hash = Convert.FromHexString(digits);
        return true;
    }

    /// <summary>Lower-case hex, the way <c>sha256sum</c> prints it.</summary>
    public static string Format(ReadOnlySpan<byte> hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
