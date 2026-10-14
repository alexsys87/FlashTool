using System.Net;
using System.Net.Http.Headers;

namespace FlashTool.Core.Sources;

/// <summary>
/// Image located on an HTTP(S) server. Data is streamed directly into the
/// decompressor and the device; nothing is stored in temporary files.
/// When the server supports byte ranges the stream is seekable (required for
/// zip/7z, whose directory is at the end) and broken connections are resumed.
/// </summary>
public sealed class HttpStreamSource : IStreamSource
{
    private readonly HttpClient _client;
    private readonly EntityTagHeaderValue? _etag;

    private HttpStreamSource(HttpClient client, Uri uri, string name, long? length, bool supportsRanges, EntityTagHeaderValue? etag)
    {
        _client = client;
        Uri = uri;
        Name = name;
        Length = length;
        IsSeekable = supportsRanges && length.HasValue;
        _etag = etag;
    }

    public Uri Uri { get; }

    public string Name { get; }

    public string Location => Uri.ToString();

    public long? Length { get; }

    public bool IsSeekable { get; }

    public bool IsRemote => true;

    /// <summary>
    /// Probes the server: resolves redirects, file name, size and range support.
    /// </summary>
    public static async Task<HttpStreamSource> CreateAsync(HttpClient client, Uri uri, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new FlashToolException(
                FlashErrorCode.HttpError,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                (int)response.StatusCode,
                response.ReasonPhrase);
        }

        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;
        long? length = supportsRanges
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        var name = GetFileName(response, uri, finalUri);
        return new HttpStreamSource(client, uri, name, length, supportsRanges, response.Headers.ETag);
    }

    public Stream Open(CancellationToken cancellationToken = default)
        => new HttpRangeStream(_client, Uri, Length, IsSeekable, _etag, cancellationToken);

    private static string GetFileName(HttpResponseMessage response, Uri original, Uri final)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var fromHeader = disposition?.FileNameStar ?? disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            return SanitizeFileName(fromHeader.Trim('"'));
        }

        foreach (var candidate in new[] { original, final })
        {
            var segment = Uri.UnescapeDataString(candidate.Segments.LastOrDefault() ?? string.Empty).Trim('/');
            if (segment.Contains('.', StringComparison.Ordinal))
            {
                return SanitizeFileName(segment);
            }
        }

        return "download.img";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}
