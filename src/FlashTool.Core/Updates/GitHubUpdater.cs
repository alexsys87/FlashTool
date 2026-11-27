// Self-contained GitHub release updater: depends only on the .NET base library (System.Net.Http,
// System.Text.Json, System.Security.Cryptography). To reuse it in another project, copy this single
// file and change the namespace; nothing else from FlashTool is needed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlashTool.Core.Updates;

/// <summary>Where to look for releases and which version is running now.</summary>
public sealed class GitHubUpdaterOptions
{
    /// <summary>Repository owner (user or organization), e.g. <c>alexsys87</c>.</summary>
    public required string Owner { get; init; }

    /// <summary>Repository name, e.g. <c>FlashTool</c>.</summary>
    public required string Repository { get; init; }

    /// <summary>Version of the running application; releases newer than this are updates.</summary>
    public required Version CurrentVersion { get; init; }

    /// <summary>Also offer releases marked as pre-release on GitHub. Off by default.</summary>
    public bool IncludePrereleases { get; init; }

    /// <summary>
    /// Refuse to keep a download whose SHA-256 is unknown. The hash is taken from the asset's
    /// <c>digest</c> reported by GitHub, or from a checksum file attached to the same release
    /// (<c>SHA256SUMS</c>, <c>SHA256SUMS.txt</c>, <c>&lt;asset&gt;.sha256</c>, ...). On by default.
    /// </summary>
    public bool RequireChecksum { get; init; } = true;

    /// <summary>GitHub REST API root; change it for GitHub Enterprise or tests.</summary>
    public Uri ApiBaseUrl { get; init; } = new("https://api.github.com/");

    /// <summary>
    /// Sent when the <see cref="HttpClient"/> has no User-Agent of its own (GitHub rejects requests without one).
    /// </summary>
    public string UserAgent { get; init; } = "GitHubUpdater/1.0";

    /// <summary>Optional token for private repositories or a higher API rate limit.</summary>
    public string? AccessToken { get; init; }

    /// <summary>How many of the most recent releases are examined (1-100).</summary>
    public int ReleasesToScan { get; init; } = 30;
}

/// <summary>A published GitHub release.</summary>
public sealed record GitHubRelease(
    string TagName,
    Version Version,
    string Name,
    string? Notes,
    Uri HtmlUrl,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>A file attached to a release.</summary>
/// <param name="Sha256">Lower-case hex SHA-256 reported by GitHub, if any.</param>
public sealed record GitHubReleaseAsset(string Name, long Size, Uri DownloadUrl, string? ContentType, string? Sha256);

/// <summary>Result of <see cref="GitHubUpdater.CheckForUpdateAsync"/>.</summary>
/// <param name="Latest">Newest suitable release, even when it is not newer than the running version.</param>
/// <param name="Asset">The file chosen from <paramref name="Latest"/> by the asset selector, if one matched.</param>
public sealed record UpdateCheckResult(Version CurrentVersion, GitHubRelease? Latest, GitHubReleaseAsset? Asset)
{
    /// <summary>A release newer than the running version exists (it may lack a file for this application).</summary>
    public bool IsNewerVersionAvailable => Latest is not null && GitHubUpdater.CompareVersions(Latest.Version, CurrentVersion) > 0;

    /// <summary>A newer release exists and has a file for this application.</summary>
    public bool IsUpdateAvailable => IsNewerVersionAvailable && Asset is not null;
}

/// <summary>Download progress; <see cref="TotalBytes"/> is null when the server does not report a length.</summary>
public readonly record struct UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;
}

/// <summary>A downloaded and verified release file.</summary>
/// <param name="Verified">The SHA-256 matched a known checksum (always true when <see cref="GitHubUpdaterOptions.RequireChecksum"/> is set).</param>
public sealed record DownloadedUpdate(string FilePath, string Sha256, bool Verified);

/// <summary>Failure talking to GitHub or verifying a download.</summary>
public sealed class GitHubUpdateException : Exception
{
    public GitHubUpdateException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// Checks GitHub releases of a repository for a newer version and downloads its files.
/// Thread-safe; the <see cref="HttpClient"/> is not disposed by this class.
/// </summary>
/// <example>
/// <code>
/// var updater = new GitHubUpdater(httpClient, new GitHubUpdaterOptions
/// {
///     Owner = "alexsys87",
///     Repository = "FlashTool",
///     CurrentVersion = typeof(App).Assembly.GetName().Version!,
/// });
/// var check = await updater.CheckForUpdateAsync(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
/// if (check.IsUpdateAvailable)
/// {
///     var file = await updater.DownloadAsync(check.Latest!, check.Asset!, Path.GetTempPath(), progress);
/// }
/// </code>
/// </example>
public sealed partial class GitHubUpdater
{
    private const int BufferSize = 81920;

    private static readonly string[] ChecksumFileNames = ["SHA256SUMS", "SHA256SUMS.txt", "sha256sums.txt", "checksums.txt", "CHECKSUMS.txt"];

    private readonly HttpClient _http;
    private readonly GitHubUpdaterOptions _options;

    public GitHubUpdater(HttpClient http, GitHubUpdaterOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Repository);
        ArgumentNullException.ThrowIfNull(options.CurrentVersion);
        _http = http;
        _options = options;
    }

    public GitHubUpdaterOptions Options => _options;

    /// <summary>Recent published releases (no drafts, no tags that are not versions), newest version first.</summary>
    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        var perPage = Math.Clamp(_options.ReleasesToScan, 1, 100);
        var url = new Uri(_options.ApiBaseUrl, $"repos/{Uri.EscapeDataString(_options.Owner)}/{Uri.EscapeDataString(_options.Repository)}/releases?per_page={perPage}");

        using var request = CreateRequest(url, "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GitHubUpdateException("GitHub returned a response that is not valid JSON.", inner: ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubUpdateException("GitHub returned an unexpected response instead of a release list.");
            }

            var releases = new List<GitHubRelease>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (TryParseRelease(item) is { } release)
                {
                    releases.Add(release);
                }
            }

            // Stable before pre-release of the same number, then by publication date.
            return releases
                .OrderByDescending(r => r.Version, VersionComparer.Instance)
                .ThenBy(r => r.IsPrerelease)
                .ThenByDescending(r => r.PublishedAt)
                .ToList();
        }
    }

    /// <summary>Newest release allowed by <see cref="GitHubUpdaterOptions.IncludePrereleases"/>, or null when there is none.</summary>
    public async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        var releases = await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
        return releases.FirstOrDefault(r => _options.IncludePrereleases || !r.IsPrerelease);
    }

    /// <summary>
    /// Finds the newest release and the file in it that suits this application.
    /// </summary>
    /// <param name="selectAsset">Picks the file to download; null takes the first asset that is not a checksum file.</param>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(Func<GitHubReleaseAsset, bool>? selectAsset = null, CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        var asset = latest?.Assets.FirstOrDefault(a => selectAsset?.Invoke(a) ?? !IsChecksumFile(a.Name));
        return new UpdateCheckResult(_options.CurrentVersion, latest, asset);
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="destinationFolder"/> and checks its SHA-256.
    /// The file appears under its final name only after it is complete and verified;
    /// an existing file of that name is replaced.
    /// </summary>
    public async Task<DownloadedUpdate> DownloadAsync(
        GitHubRelease release,
        GitHubReleaseAsset asset,
        string destinationFolder,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);

        var fileName = Path.GetFileName(asset.Name);
        if (string.IsNullOrEmpty(fileName) || fileName != asset.Name || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new GitHubUpdateException($"The release file name \"{asset.Name}\" is not a valid file name.");
        }

        var expected = asset.Sha256 ?? await FindPublishedChecksumAsync(release, asset, cancellationToken).ConfigureAwait(false);
        if (expected is null && _options.RequireChecksum)
        {
            throw new GitHubUpdateException($"No SHA-256 checksum is published for \"{asset.Name}\"; the download was not started.");
        }

        Directory.CreateDirectory(destinationFolder);
        var target = Path.Combine(destinationFolder, fileName);
        var partial = target + ".part";

        string actual;
        try
        {
            actual = await DownloadToFileAsync(asset, partial, progress, cancellationToken).ConfigureAwait(false);
            if (expected is not null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new GitHubUpdateException($"The SHA-256 of the downloaded \"{asset.Name}\" does not match the published checksum.");
            }

            File.Move(partial, target, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        return new DownloadedUpdate(target, actual, expected is not null);
    }

    /// <summary>
    /// Reads a version from a tag such as <c>v1.2.3</c>, <c>release-1.2</c> or <c>1.2.3-beta.1</c>
    /// (the pre-release suffix is ignored).
    /// </summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var match = VersionInTag().Match(tag);
        if (!match.Success || !Version.TryParse(match.Value, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>Compares versions treating missing components as zero (so 1.2 == 1.2.0.0).</summary>
    public static int CompareVersions(Version a, Version b) => Normalize(a).CompareTo(Normalize(b));

    /// <summary>Whether the name looks like a checksum list rather than a program file.</summary>
    public static bool IsChecksumFile(string name) =>
        ChecksumFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".sha256sum", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the hash of <paramref name="fileName"/> in a checksum list: GNU (<c>hash  name</c>,
    /// <c>hash *name</c>), BSD (<c>SHA256 (name) = hash</c>) or a bare hash when
    /// <paramref name="singleFile"/> is set (a <c>&lt;file&gt;.sha256</c> next to the file).
    /// </summary>
    public static string? FindChecksum(string checksumList, string fileName, bool singleFile = false)
    {
        foreach (var rawLine in checksumList.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var bsd = BsdChecksumLine().Match(line);
            if (bsd.Success)
            {
                if (string.Equals(bsd.Groups["name"].Value, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return bsd.Groups["hash"].Value.ToLowerInvariant();
                }

                continue;
            }

            var gnu = GnuChecksumLine().Match(line);
            if (gnu.Success)
            {
                var name = gnu.Groups["name"].Value.Trim();
                if (name.Length == 0 ? singleFile : string.Equals(Path.GetFileName(name.Replace('\\', '/')), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return gnu.Groups["hash"].Value.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    private async Task<string?> FindPublishedChecksumAsync(GitHubRelease release, GitHubReleaseAsset asset, CancellationToken cancellationToken)
    {
        // A dedicated "<file>.sha256" first, then a shared list for the whole release.
        var candidates = release.Assets
            .Where(a => a != asset && IsChecksumFile(a.Name) && a.Size <= 1024 * 1024)
            .OrderByDescending(a => a.Name.StartsWith(asset.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var file in candidates)
        {
            var dedicated = file.Name.StartsWith(asset.Name, StringComparison.OrdinalIgnoreCase);
            if (!dedicated && !ChecksumFileNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
            {
                // Somebody else's "<other>.sha256".
                continue;
            }

            using var request = CreateRequest(file.DownloadUrl, "application/octet-stream");
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (FindChecksum(text, asset.Name, singleFile: dedicated) is { } hash)
            {
                return hash;
            }
        }

        return null;
    }

    private async Task<string> DownloadToFileAsync(GitHubReleaseAsset asset, string path, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(asset.DownloadUrl, "application/octet-stream");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            long received = 0;
            progress?.Report(new UpdateDownloadProgress(0, total));
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new UpdateDownloadProgress(received, total));
            }

            if (total is { } expectedLength && received != expectedLength)
            {
                throw new GitHubUpdateException($"The download of \"{asset.Name}\" ended after {received} of {expectedLength} bytes.");
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private HttpRequestMessage CreateRequest(Uri url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        }

        // The token is meant for GitHub only; asset downloads redirect to other hosts, and
        // HttpClient drops the Authorization header on such redirects.
        if (!string.IsNullOrEmpty(_options.AccessToken) && url.Host == _options.ApiBaseUrl.Host)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubUpdateException($"Could not connect to {request.RequestUri?.Host}: {ex.Message}", ex.StatusCode, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubUpdateException($"{request.RequestUri?.Host} did not respond in time.", inner: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var status = response.StatusCode;
            if ((status == HttpStatusCode.Forbidden || status == (HttpStatusCode)429)
                && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
                && remaining.FirstOrDefault() == "0")
            {
                var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
                    && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("t", CultureInfo.CurrentCulture)
                        : null;
                throw new GitHubUpdateException(
                    reset is null ? "The GitHub API request limit is exhausted; try again later." : $"The GitHub API request limit is exhausted; try again after {reset}.",
                    status);
            }

            if (status == HttpStatusCode.NotFound)
            {
                throw new GitHubUpdateException($"Not found on GitHub: {request.RequestUri}", status);
            }

            throw new GitHubUpdateException($"GitHub answered {(int)status} ({response.ReasonPhrase}) for {request.RequestUri}", status);
        }
    }

    private static GitHubRelease? TryParseRelease(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || GetBool(item, "draft")
            || GetString(item, "tag_name") is not { } tag
            || !TryParseVersion(tag, out var version)
            || !Uri.TryCreate(GetString(item, "html_url"), UriKind.Absolute, out var htmlUrl))
        {
            return null;
        }

        var assets = new List<GitHubReleaseAsset>();
        if (item.TryGetProperty("assets", out var assetList) && assetList.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetList.EnumerateArray())
            {
                if (GetString(a, "name") is { Length: > 0 } name
                    && Uri.TryCreate(GetString(a, "browser_download_url"), UriKind.Absolute, out var download)
                    && (GetString(a, "state") ?? "uploaded") == "uploaded")
                {
                    var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                    assets.Add(new GitHubReleaseAsset(name, size, download, GetString(a, "content_type"), ParseDigest(GetString(a, "digest"))));
                }
            }
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(GetString(item, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null;
        var title = GetString(item, "name");
        return new GitHubRelease(
            tag,
            version,
            string.IsNullOrWhiteSpace(title) ? tag : title,
            GetString(item, "body"),
            htmlUrl,
            published,
            GetBool(item, "prerelease"),
            assets);
    }

    /// <summary>GitHub reports <c>"digest": "sha256:&lt;hex&gt;"</c> for assets uploaded since mid-2025.</summary>
    private static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"\d+(\.\d+){1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex VersionInTag();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})(?:\s+\*?(?<name>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex GnuChecksumLine();

    [GeneratedRegex(@"^SHA256\s*\((?<name>.+)\)\s*=\s*(?<hash>[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BsdChecksumLine();

    private sealed class VersionComparer : IComparer<Version>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(Version? x, Version? y) => x is null || y is null ? Comparer<Version?>.Default.Compare(x, y) : CompareVersions(x, y);
    }
}
