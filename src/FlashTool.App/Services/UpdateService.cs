using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using FlashTool.App.Localization;
using FlashTool.Core.Updates;

namespace FlashTool.App.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Downloaded,
    Failed,
}

/// <summary>
/// Checks FlashTool releases on GitHub and downloads the file matching this build:
/// the MSI for an installed copy, the standalone zip for a single-file exe, the plain zip otherwise.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string Owner = "alexsys87";
    public const string Repository = "FlashTool";

    /// <summary>A silent check at startup happens at most this often.</summary>
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(20);

    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly BuildVariant _variant = BuildVariant.Detect();
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanDownload), nameof(HasDownload), nameof(HasRelease))]
    private UpdateState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(HasRelease))]
    private UpdateCheckResult? _lastCheck;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownload), nameof(CanDownload))]
    private DownloadedUpdate? _download;

    public UpdateService(HttpClient http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Name of the release file this copy of FlashTool updates from.</summary>
    public string AssetName => _variant.AssetName;

    public bool IsInstaller => _variant.IsInstaller;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    /// <summary>A newer release has a file for this build and it is not downloaded yet.</summary>
    public bool CanDownload => LastCheck?.IsUpdateAvailable == true && (State is UpdateState.Available or UpdateState.Failed) && Download is null;

    public bool HasDownload => State == UpdateState.Downloaded && Download is not null;

    /// <summary>There is a newer release to show (release notes link, badge).</summary>
    public bool HasRelease => LastCheck?.IsNewerVersionAvailable == true;

    public bool CheckAutomatically
    {
        get => _settings.Settings.CheckForUpdates;
        set
        {
            if (_settings.Settings.CheckForUpdates == value)
            {
                return;
            }

            _settings.Settings.CheckForUpdates = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool IncludePrereleases
    {
        get => _settings.Settings.IncludePrereleaseUpdates;
        set
        {
            if (_settings.Settings.IncludePrereleaseUpdates == value)
            {
                return;
            }

            _settings.Settings.IncludePrereleaseUpdates = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Startup check: silent on failure, skipped when turned off or done recently.</summary>
    public async Task CheckOnStartupAsync()
    {
        var last = _settings.Settings.LastUpdateCheck;
        if (!CheckAutomatically || (last is { } at && DateTimeOffset.UtcNow - at < AutomaticCheckInterval && at <= DateTimeOffset.UtcNow))
        {
            return;
        }

        await CheckAsync(silent: true);
    }

    public async Task CheckAsync(bool silent = false)
    {
        if (IsBusy)
        {
            return;
        }

        using var cts = BeginOperation();
        State = UpdateState.Checking;
        StatusText = Loc.Get("Update_Checking");
        IsProgressIndeterminate = true;
        Download = null;
        try
        {
            var check = await CreateUpdater().CheckForUpdateAsync(a => string.Equals(a.Name, _variant.AssetName, StringComparison.OrdinalIgnoreCase), cts.Token);
            _settings.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            _settings.Save();
            LastCheck = check;
            if (check.IsUpdateAvailable)
            {
                State = UpdateState.Available;
                StatusText = Loc.Format("Update_Available", FormatVersion(check.Latest!), Formatting.Size(check.Asset!.Size));
            }
            else if (check.IsNewerVersionAvailable)
            {
                // Newer, but without a file for this build: offer the release page only.
                State = UpdateState.Available;
                StatusText = Loc.Format("Update_AvailableNoAsset", FormatVersion(check.Latest!), _variant.AssetName);
            }
            else
            {
                State = UpdateState.UpToDate;
                StatusText = Loc.Format("Update_UpToDate", CurrentVersion.ToString(3));
            }
        }
        catch (Exception ex) when (ex is GitHubUpdateException or HttpRequestException or IOException or OperationCanceledException)
        {
            State = silent ? UpdateState.Idle : UpdateState.Failed;
            StatusText = silent ? string.Empty : ex is OperationCanceledException ? Loc.Get("Update_Cancelled") : Loc.Format("Update_CheckFailed", ex.Message);
        }
        finally
        {
            EndOperation(cts);
        }
    }

    public async Task DownloadAsync()
    {
        if (IsBusy || LastCheck is not { IsUpdateAvailable: true, Latest: { } release, Asset: { } asset })
        {
            return;
        }

        using var cts = BeginOperation();
        State = UpdateState.Downloading;
        Progress = 0;
        IsProgressIndeterminate = asset.Size <= 0;
        StatusText = Loc.Format("Update_Downloading", asset.Name);
        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            // Reports are posted to the UI thread and may arrive after the download has finished.
            if (State == UpdateState.Downloading && p.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                Progress = fraction * 100;
                StatusText = Loc.Format("Update_DownloadingProgress", asset.Name, Formatting.Size(p.BytesReceived), Formatting.Size(p.TotalBytes!.Value));
            }
        });

        try
        {
            Download = await CreateUpdater().DownloadAsync(release, asset, DownloadFolder(), progress, cts.Token);
            State = UpdateState.Downloaded;
            StatusText = Loc.Format(_variant.IsInstaller ? "Update_DownloadedInstaller" : "Update_DownloadedArchive", Download.FilePath);
        }
        catch (Exception ex) when (ex is GitHubUpdateException or HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            State = UpdateState.Failed;
            StatusText = ex is OperationCanceledException ? Loc.Get("Update_Cancelled") : Loc.Format("Update_DownloadFailed", ex.Message);
        }
        finally
        {
            EndOperation(cts);
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Starts the downloaded MSI; the caller closes the app so the installer can replace it.</summary>
    public bool StartInstaller()
    {
        if (Download is not { } file || !File.Exists(file.FilePath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{file.FilePath}\"") { UseShellExecute = true });
        return true;
    }

    public void ShowDownloadInFolder()
    {
        if (Download is { } file && File.Exists(file.FilePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FilePath}\"") { UseShellExecute = true });
        }
    }

    public void OpenReleasePage()
    {
        var url = LastCheck?.Latest?.HtmlUrl.ToString() ?? $"https://github.com/{Owner}/{Repository}/releases";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private GitHubUpdater CreateUpdater() => new(_http, new GitHubUpdaterOptions
    {
        Owner = Owner,
        Repository = Repository,
        CurrentVersion = CurrentVersion,
        IncludePrereleases = IncludePrereleases,
    });

    private CancellationTokenSource BeginOperation()
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
        }

        IsProgressIndeterminate = false;
    }

    private static string FormatVersion(GitHubRelease release) =>
        release.IsPrerelease ? $"{release.TagName} ({Loc.Get("Update_Prerelease")})" : release.TagName;

    /// <summary>The user's Downloads folder, or %LOCALAPPDATA%\FlashTool\Updates when it is missing.</summary>
    private static string DownloadFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashTool", "Updates");
    }

    /// <summary>Which of the published files this copy of FlashTool was made from.</summary>
    private sealed record BuildVariant(string AssetName, bool IsInstaller)
    {
        public static BuildVariant Detect()
        {
            var runtime = $"net{Environment.Version.Major}";

            // A single-file app has no assembly location on disk.
#pragma warning disable IL3000
            var singleFile = string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);
#pragma warning restore IL3000
            if (!singleFile)
            {
                return new BuildVariant($"FlashTool-{runtime}-win-x64.zip", false);
            }

            // The MSI installs the single-file exe into "Program Files\FlashTool-netN".
            var folder = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var installed = !string.IsNullOrEmpty(programFiles)
                && string.Equals(folder.TrimEnd(Path.DirectorySeparatorChar), Path.Combine(programFiles, $"FlashTool-{runtime}"), StringComparison.OrdinalIgnoreCase);
            return installed
                ? new BuildVariant($"FlashTool-{runtime}-win-x64.msi", true)
                : new BuildVariant($"FlashTool-{runtime}-win-x64-standalone.zip", false);
        }
    }
}
