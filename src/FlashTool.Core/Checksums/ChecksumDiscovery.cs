using System.Text;

namespace FlashTool.Core.Checksums;

/// <summary>A checksum found next to an image, and where it was read from.</summary>
/// <param name="Sha256">The value.</param>
/// <param name="Origin">Name of the file or URL it came from, for display.</param>
public sealed record FoundChecksum(byte[] Sha256, string Origin);

/// <summary>
/// Looks for the checksum file that usually sits next to a published image: its own sidecar
/// (<c>image.img.xz.sha256</c>) or a list of the directory (<c>SHA256SUMS</c>).
/// </summary>
/// <remarks>
/// A checksum fetched from the same server as the image catches a damaged file, but it cannot
/// prove the file is the one the authors published.
/// </remarks>
public static class ChecksumDiscovery
{
    private const int MaxFileSize = 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly string[] SidecarSuffixes = [".sha256", ".sha256sum", ".sha"];

    private static readonly string[] ListNames = ["SHA256SUMS", "sha256sums", "SHA256SUMS.txt", "sha256sums.txt", "sha256sum.txt"];

    /// <summary>Looks beside a local image. Cheap enough to call on the UI thread.</summary>
    public static FoundChecksum? FindForFile(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);

        var fullPath = Path.GetFullPath(imagePath);
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (directory is null || fileName.Length == 0)
        {
            return null;
        }

        foreach (var suffix in SidecarSuffixes)
        {
            if (TryReadFile(Path.Combine(directory, fileName + suffix), out var text) && ChecksumFile.TryFind(text, fileName, acceptSingleEntry: true, out var hash))
            {
                return new FoundChecksum(hash, fileName + suffix);
            }
        }

        foreach (var name in ListNames)
        {
            if (TryReadFile(Path.Combine(directory, name), out var text) && ChecksumFile.TryFind(text, fileName, acceptSingleEntry: false, out var hash))
            {
                return new FoundChecksum(hash, name);
            }
        }

        return null;
    }

    /// <summary>
    /// Looks beside an image that is downloaded from <paramref name="imageUri"/>. Failures and missing files
    /// are not errors, they just mean nothing was found; only cancelling through <paramref name="cancellationToken"/> throws.
    /// </summary>
    public static async Task<FoundChecksum?> FindForUrlAsync(HttpClient http, Uri imageUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(imageUri);

        // Signed or parameterized links have no predictable neighbours.
        if (!imageUri.IsAbsoluteUri || imageUri.Query.Length > 0 || imageUri.Segments.Length == 0)
        {
            return null;
        }

        var lastSegment = imageUri.Segments[^1];
        var fileName = Uri.UnescapeDataString(lastSegment);
        if (fileName.Length == 0 || fileName.EndsWith('/'))
        {
            return null;
        }

        foreach (var suffix in SidecarSuffixes)
        {
            var found = await TryFetchAsync(http, new Uri(imageUri, lastSegment + suffix), fileName, acceptSingleEntry: true, cancellationToken).ConfigureAwait(false);
            if (found != null)
            {
                return found;
            }
        }

        foreach (var name in ListNames.Take(2))
        {
            var found = await TryFetchAsync(http, new Uri(imageUri, name), fileName, acceptSingleEntry: false, cancellationToken).ConfigureAwait(false);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static async Task<FoundChecksum?> TryFetchAsync(HttpClient http, Uri uri, string fileName, bool acceptSingleEntry, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxFileSize)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxFileSize)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return ChecksumFile.TryFind(text, fileName, acceptSingleEntry, out var hash) ? new FoundChecksum(hash, uri.ToString()) : null;
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable, slow or not a checksum file: nothing found.
            return null;
        }
    }

    private static bool TryReadFile(string path, out string text)
    {
        text = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileSize)
            {
                return false;
            }

            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
