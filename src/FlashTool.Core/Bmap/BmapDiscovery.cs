using FlashTool.Core.Containers;

namespace FlashTool.Core.Bmap;

/// <summary>Result of looking for a .bmap next to an image.</summary>
/// <param name="Bmap">The parsed map; null when none was found or it cannot be used.</param>
/// <param name="Origin">File name or URL of the .bmap that was found, for display.</param>
/// <param name="Problem">Why a .bmap that was found cannot be used.</param>
public sealed record BmapLookup(BmapFile? Bmap, string? Origin, BmapProblem Problem)
{
    public static BmapLookup NotFound { get; } = new(null, null, BmapProblem.None);

    public bool Found => Origin != null;
}

/// <summary>
/// Finds the .bmap that wic and bmaptool put next to an image. It describes the uncompressed
/// image, so "core-image.wic.zst" looks for "core-image.wic.bmap" first, then for
/// "core-image.wic.zst.bmap".
/// </summary>
public static class BmapDiscovery
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Where a .bmap for an image created at <paramref name="imagePath"/> is saved.</summary>
    public static string PathFor(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath) ?? string.Empty;
        return Path.Combine(directory, ContainerNaming.RemoveCompressionExtension(Path.GetFileName(imagePath)) + ".bmap");
    }

    public static IReadOnlyList<string> CandidateNames(string imageFileName)
    {
        var stripped = ContainerNaming.RemoveCompressionExtension(imageFileName);
        return stripped == imageFileName ? [imageFileName + ".bmap"] : [stripped + ".bmap", imageFileName + ".bmap"];
    }

    /// <summary>Looks beside a local image.</summary>
    public static BmapLookup FindForFile(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory == null)
        {
            return BmapLookup.NotFound;
        }

        foreach (var name in CandidateNames(Path.GetFileName(fullPath)))
        {
            var path = Path.Combine(directory, name);
            byte[] content;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                if (info.Length > BmapFile.MaxFileSize)
                {
                    return new BmapLookup(null, name, BmapProblem.TooLarge);
                }

                content = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            return BmapFile.TryParse(content, out var bmap, out var problem) ? new BmapLookup(bmap, name, BmapProblem.None) : new BmapLookup(null, name, problem);
        }

        return BmapLookup.NotFound;
    }

    /// <summary>
    /// Looks beside an image downloaded from <paramref name="imageUri"/>. Network failures just mean
    /// nothing was found; only cancelling through <paramref name="cancellationToken"/> throws.
    /// </summary>
    public static async Task<BmapLookup> FindForUrlAsync(HttpClient http, Uri imageUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(imageUri);

        // Signed or parameterized links have no predictable neighbours.
        if (!imageUri.IsAbsoluteUri || imageUri.Query.Length > 0 || imageUri.Segments.Length == 0)
        {
            return BmapLookup.NotFound;
        }

        var fileName = Uri.UnescapeDataString(imageUri.Segments[^1]);
        if (fileName.Length == 0 || fileName.EndsWith('/'))
        {
            return BmapLookup.NotFound;
        }

        foreach (var name in CandidateNames(fileName))
        {
            var uri = new Uri(imageUri, Uri.EscapeDataString(name));
            var content = await TryDownloadAsync(http, uri, cancellationToken).ConfigureAwait(false);
            if (content == null)
            {
                continue;
            }

            if (content.Length > BmapFile.MaxFileSize)
            {
                return new BmapLookup(null, uri.ToString(), BmapProblem.TooLarge);
            }

            return BmapFile.TryParse(content, out var bmap, out var problem) ? new BmapLookup(bmap, uri.ToString(), BmapProblem.None) : new BmapLookup(null, uri.ToString(), problem);
        }

        return BmapLookup.NotFound;
    }

    /// <summary>The body, or null when it is missing or unreachable; a body over the limit comes back one byte too long.</summary>
    private static async Task<byte[]?> TryDownloadAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength > BmapFile.MaxFileSize)
            {
                return new byte[BmapFile.MaxFileSize + 1];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > BmapFile.MaxFileSize)
                {
                    return new byte[BmapFile.MaxFileSize + 1];
                }
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
