using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlashTool.Core.Updates;

namespace FlashTool.Core.Tests;

public sealed class GitHubUpdaterTests
{
    private const string ReleasesUrl = "https://api.github.com/repos/owner/app/releases?per_page=30";

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2")]
    [InlineData("release-2.0.1.7", "2.0.1.7")]
    [InlineData("v0.3.0-beta.2", "0.3.0")]
    public void Version_is_read_from_tag(string tag, string expected)
    {
        Assert.True(GitHubUpdater.TryParseVersion(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void Tag_without_version_is_rejected(string? tag) => Assert.False(GitHubUpdater.TryParseVersion(tag, out _));

    [Fact]
    public void Missing_version_components_count_as_zero()
    {
        Assert.Equal(0, GitHubUpdater.CompareVersions(new Version(0, 1, 2), new Version(0, 1, 2, 0)));
        Assert.True(GitHubUpdater.CompareVersions(new Version(0, 1, 3), new Version(0, 1, 2, 9)) > 0);
    }

    [Fact]
    public void Checksum_lists_in_gnu_and_bsd_formats_are_understood()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var list = $"﻿# comment\n{a}  other.zip\n{b} *dir/App.zip\r\nSHA256 (App.msi) = {a}\n";

        Assert.Equal(b.ToLowerInvariant(), GitHubUpdater.FindChecksum(list, "app.zip"));
        Assert.Equal(a, GitHubUpdater.FindChecksum(list, "App.msi"));
        Assert.Null(GitHubUpdater.FindChecksum(list, "missing.zip"));
        Assert.Equal(a, GitHubUpdater.FindChecksum(a + "\n", "App.zip", singleFile: true));
        Assert.Null(GitHubUpdater.FindChecksum(a + "\n", "App.zip"));
    }

    [Fact]
    public async Task Newest_stable_release_with_matching_file_is_offered()
    {
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, new object[]
        {
            Release("v0.1.1", assets: [Asset("App-win.zip")]),
            Release("v0.3.0-rc.1", prerelease: true, assets: [Asset("App-win.zip")]),
            Release("v0.4.0", draft: true, assets: [Asset("App-win.zip")]),
            Release("v0.2.0", assets: [Asset("SHA256SUMS.txt"), Asset("App-win.zip"), Asset("App-linux.tar.gz")]),
            Release("nightly", assets: [Asset("App-win.zip")]),
        });
        var updater = CreateUpdater(handler, new Version(0, 1, 2));

        var check = await updater.CheckForUpdateAsync(a => a.Name.Contains("win", StringComparison.Ordinal));

        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("v0.2.0", check.Latest!.TagName);
        Assert.Equal("App-win.zip", check.Asset!.Name);

        // Without a selector the checksum list is skipped.
        Assert.Equal("App-win.zip", (await updater.CheckForUpdateAsync()).Asset!.Name);
    }

    [Fact]
    public async Task Prerelease_is_offered_only_when_allowed()
    {
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, new object[]
        {
            Release("v0.2.0", assets: [Asset("App.zip")]),
            Release("v0.3.0-rc.1", prerelease: true, assets: [Asset("App.zip")]),
        });

        var updater = CreateUpdater(handler, new Version(0, 2, 0), includePrereleases: true);
        var check = await updater.CheckForUpdateAsync();

        Assert.True(check.IsUpdateAvailable);
        Assert.True(check.Latest!.IsPrerelease);
        Assert.Equal(new Version(0, 3, 0), check.Latest.Version);

        Assert.False((await CreateUpdater(handler, new Version(0, 2, 0)).CheckForUpdateAsync()).IsNewerVersionAvailable);
    }

    [Fact]
    public async Task Same_version_is_not_an_update()
    {
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, new object[] { Release("v0.1.2", assets: [Asset("App.zip")]) });

        var check = await CreateUpdater(handler, new Version(0, 1, 2, 0)).CheckForUpdateAsync();

        Assert.NotNull(check.Latest);
        Assert.False(check.IsNewerVersionAvailable);
        Assert.False(check.IsUpdateAvailable);
    }

    [Fact]
    public async Task Newer_release_without_suitable_file_is_reported_separately()
    {
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, new object[] { Release("v1.0.0", assets: [Asset("App-linux.tar.gz")]) });

        var check = await CreateUpdater(handler, new Version(0, 1)).CheckForUpdateAsync(a => a.Name.EndsWith(".msi", StringComparison.Ordinal));

        Assert.True(check.IsNewerVersionAvailable);
        Assert.False(check.IsUpdateAvailable);
        Assert.Null(check.Asset);
    }

    [Fact]
    public async Task Download_is_verified_by_github_digest()
    {
        using var dir = new TempDir();
        var content = RandomNumberGenerator.GetBytes(300_000);
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, new object[] { Release("v1.0.0", assets: [Asset("App.zip", content.Length, Sha(content))]) });
        handler.Bytes(DownloadUrl("v1.0.0", "App.zip"), content);
        var updater = CreateUpdater(handler, new Version(0, 1));
        var check = await updater.CheckForUpdateAsync();
        var reports = new List<UpdateDownloadProgress>();

        var result = await updater.DownloadAsync(check.Latest!, check.Asset!, dir.Path, new SyncProgress<UpdateDownloadProgress>(reports.Add));

        Assert.True(result.Verified);
        Assert.Equal(Path.Combine(dir.Path, "App.zip"), result.FilePath);
        Assert.Equal(content, File.ReadAllBytes(result.FilePath));
        Assert.Equal(content.Length, reports[^1].BytesReceived);
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.False(File.Exists(result.FilePath + ".part"));
    }

    [Fact]
    public async Task Download_is_verified_by_checksum_list_in_release()
    {
        using var dir = new TempDir();
        var content = Encoding.UTF8.GetBytes("new version");
        var sums = Encoding.UTF8.GetBytes($"{new string('0', 64)}  Other.zip\n{Sha(content)}  App.zip\n");
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, new object[] { Release("v1.0.0", assets: [Asset("App.zip", content.Length), Asset("SHA256SUMS.txt", sums.Length)]) });
        handler.Bytes(DownloadUrl("v1.0.0", "App.zip"), content);
        handler.Bytes(DownloadUrl("v1.0.0", "SHA256SUMS.txt"), sums);
        var updater = CreateUpdater(handler, new Version(0, 1));
        var check = await updater.CheckForUpdateAsync();

        var result = await updater.DownloadAsync(check.Latest!, check.Asset!, dir.Path);

        Assert.True(result.Verified);
        Assert.Equal(Sha(content), result.Sha256);
    }

    [Fact]
    public async Task Corrupted_download_is_rejected_and_removed()
    {
        using var dir = new TempDir();
        var content = Encoding.UTF8.GetBytes("new version");
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, new object[] { Release("v1.0.0", assets: [Asset("App.zip", content.Length, new string('1', 64))]) });
        handler.Bytes(DownloadUrl("v1.0.0", "App.zip"), content);
        var updater = CreateUpdater(handler, new Version(0, 1));
        var check = await updater.CheckForUpdateAsync();

        await Assert.ThrowsAsync<GitHubUpdateException>(() => updater.DownloadAsync(check.Latest!, check.Asset!, dir.Path));

        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Download_without_checksum_is_refused_unless_allowed()
    {
        using var dir = new TempDir();
        var content = Encoding.UTF8.GetBytes("new version");
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, new object[] { Release("v1.0.0", assets: [Asset("App.zip", content.Length)]) });
        handler.Bytes(DownloadUrl("v1.0.0", "App.zip"), content);

        var strict = CreateUpdater(handler, new Version(0, 1));
        var check = await strict.CheckForUpdateAsync();
        await Assert.ThrowsAsync<GitHubUpdateException>(() => strict.DownloadAsync(check.Latest!, check.Asset!, dir.Path));
        Assert.DoesNotContain(handler.Requested, u => u.EndsWith("App.zip", StringComparison.Ordinal));

        var lenient = CreateUpdater(handler, new Version(0, 1), requireChecksum: false);
        var result = await lenient.DownloadAsync(check.Latest!, check.Asset!, dir.Path);
        Assert.False(result.Verified);
        Assert.Equal(content, File.ReadAllBytes(result.FilePath));
    }

    [Fact]
    public async Task Exhausted_rate_limit_is_explained()
    {
        var handler = new FakeHandler();
        handler.Respond(ReleasesUrl, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1900000000");
            return response;
        });

        var ex = await Assert.ThrowsAsync<GitHubUpdateException>(() => CreateUpdater(handler, new Version(0, 1)).CheckForUpdateAsync());

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_carry_github_headers()
    {
        var handler = new FakeHandler();
        handler.Json(ReleasesUrl, Array.Empty<object>());
        using var client = new HttpClient(handler);
        var updater = new GitHubUpdater(client, new GitHubUpdaterOptions
        {
            Owner = "owner",
            Repository = "app",
            CurrentVersion = new Version(1, 0),
            AccessToken = "secret",
        });

        Assert.Null((await updater.CheckForUpdateAsync()).Latest);

        var request = handler.LastRequest!;
        Assert.Equal("GitHubUpdater/1.0", request.Headers.UserAgent.ToString());
        Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    private static GitHubUpdater CreateUpdater(FakeHandler handler, Version current, bool includePrereleases = false, bool requireChecksum = true) =>
        new(new HttpClient(handler), new GitHubUpdaterOptions
        {
            Owner = "owner",
            Repository = "app",
            CurrentVersion = current,
            IncludePrereleases = includePrereleases,
            RequireChecksum = requireChecksum,
        });

    private static string DownloadUrl(string tag, string name) => $"https://github.com/owner/app/releases/download/{tag}/{name}";

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static Dictionary<string, object?> Release(string tag, bool prerelease = false, bool draft = false, object[]? assets = null) => new()
    {
        ["tag_name"] = tag,
        ["name"] = "App " + tag,
        ["body"] = "notes",
        ["html_url"] = $"https://github.com/owner/app/releases/tag/{tag}",
        ["published_at"] = "2026-11-12T16:51:37Z",
        ["prerelease"] = prerelease,
        ["draft"] = draft,
        ["assets"] = (assets ?? []).Select(a => ((Func<string, object>)a).Invoke(tag)).ToArray(),
    };

    /// <summary>The download URL depends on the tag, so an asset is built once the release is known.</summary>
    private static Func<string, object> Asset(string name, long size = 100, string? sha256 = null) => tag => new Dictionary<string, object?>
    {
        ["name"] = name,
        ["size"] = size,
        ["state"] = "uploaded",
        ["content_type"] = "application/octet-stream",
        ["digest"] = sha256 is null ? null : "sha256:" + sha256,
        ["browser_download_url"] = DownloadUrl(tag, name),
    };

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = [];

        public HttpRequestMessage? LastRequest { get; private set; }

        public void Respond(string url, Func<HttpResponseMessage> response) => _routes[url] = response;

        public void Json(string url, object value)
        {
            var json = JsonSerializer.Serialize(value);
            Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        public void Bytes(string url, byte[] content) =>
            Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            LastRequest = request;
            return Task.FromResult(_routes.TryGetValue(url, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
