using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Devices;
using FlashTool.Core.Operations;
using Wpf.Ui.Controls;

namespace FlashTool.App.ViewModels;

/// <summary>A row of the bad area table.</summary>
public sealed record BadAreaRow(string Offset, string Length, string Kind);

/// <summary>
/// "Device test" page: read speed and errors without touching the data, or destructive tests that
/// fill the device and read it back (counterfeit capacity, speed of both directions), a quick probe
/// for counterfeits and random 4 KiB access.
/// </summary>
public sealed partial class DeviceTestPageViewModel : LocalizedViewModel
{
    private const int MaxRowsShown = 200;

    private readonly IDiskManager _diskManager;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly RememberedDevice? _rememberedDevice;
    private readonly List<DeviceTestSample> _liveSamples = [];
    private DeviceTestResult? _report;
    private string? _reportDevice;
    private DeviceItemViewModel? _testedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(FormatCommand))]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    private LocalizedOption<EraseFileSystem> _selectedFileSystem;

    /// <summary>Points of the chart, the running test's included.</summary>
    [ObservableProperty]
    private IReadOnlyList<DeviceTestSample> _samples = [];

    [ObservableProperty]
    private long _chartLength;

    public DeviceTestPageViewModel(
        IDiskManager diskManager,
        SettingsService settings,
        DialogService dialogs,
        DeviceListService devices,
        OperationState operation)
    {
        _diskManager = diskManager;
        _settings = settings;
        _dialogs = dialogs;
        Devices = devices;
        Operation = operation;
        Operation.PropertyChanged += OnOperationChanged;

        FileSystems = new[] { EraseFileSystem.Auto, EraseFileSystem.Fat32, EraseFileSystem.ExFat }
            .Select(f => new LocalizedOption<EraseFileSystem>(f, $"FileSystem_{f}"))
            .ToList();
        _selectedFileSystem = FileSystems.FirstOrDefault(f => f.Value == settings.Settings.DeviceTestFileSystem) ?? FileSystems[0];

        _rememberedDevice = new RememberedDevice(
            devices,
            () => settings.Settings.DeviceTestDeviceKey,
            key =>
            {
                settings.Settings.DeviceTestDeviceKey = key;
                settings.Save();
            },
            () => SelectedDevice,
            d => SelectedDevice = d,
            d => d.CanBeRead);
    }

    /// <summary>Raised with a file path when the chart should be saved as a picture (the view owns the chart).</summary>
    public event EventHandler<string>? PngExportRequested;

    public DeviceListService Devices { get; }

    public OperationState Operation { get; }

    public ProgressViewModel Progress { get; } = new();

    public ResultViewModel Result { get; } = new();

    public IReadOnlyList<LocalizedOption<EraseFileSystem>> FileSystems { get; }

    public DeviceTestMode Mode
    {
        get => _settings.Settings.DeviceTestMode;
        set
        {
            if (_settings.Settings.DeviceTestMode == value)
            {
                return;
            }

            _settings.Settings.DeviceTestMode = value;
            _settings.Save();
            OnPropertyChanged(string.Empty);
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    // One flag per radio button.
    public bool IsRead
    {
        get => Mode == DeviceTestMode.Read;
        set => SetModeIf(value, DeviceTestMode.Read);
    }

    public bool IsWriteVerify
    {
        get => Mode == DeviceTestMode.WriteVerify;
        set => SetModeIf(value, DeviceTestMode.WriteVerify);
    }

    public bool IsQuickProbe
    {
        get => Mode == DeviceTestMode.QuickProbe;
        set => SetModeIf(value, DeviceTestMode.QuickProbe);
    }

    public bool IsRandomAccess
    {
        get => Mode == DeviceTestMode.RandomAccess;
        set => SetModeIf(value, DeviceTestMode.RandomAccess);
    }

    public bool IsDestructive => Mode != DeviceTestMode.Read;

    /// <summary>What the chosen test does to the selected device.</summary>
    public string WorkText
    {
        get
        {
            if (SelectedDevice is not { HasMedia: true } device)
            {
                return string.Empty;
            }

            var size = Formatting.Size(device.Info.Size);
            return Mode switch
            {
                DeviceTestMode.Read => Loc.Format("Test_WillRead", size),
                DeviceTestMode.WriteVerify => Loc.Format("Test_WillWriteAndRead", size),
                DeviceTestMode.QuickProbe => Loc.Get("Test_WillProbe"),
                _ => Loc.Get("Test_WillRandom"),
            };
        }
    }

    public bool HasReport => _report != null;

    public bool HasChart => Samples.Count > 0;

    public IReadOnlyList<DeviceSlowArea> SlowAreas => _report?.SlowAreas ?? [];

    public IReadOnlyList<DeviceBadArea> BadAreas => _report?.BadAreas ?? [];

    public long? RealCapacity => _report?.RealCapacity;

    public IReadOnlyList<BadAreaRow> BadAreaRows => (_report?.BadAreas ?? [])
        .Take(MaxRowsShown)
        .Select(a => new BadAreaRow(
            a.Offset.ToString("N0", Loc.Instance.Culture),
            Formatting.Size(a.Length),
            Loc.Get($"Test_Bad_{a.Kind}")))
        .ToList();

    public bool HasBadAreas => _report is { BadAreaCount: > 0 };

    public string BadAreasMoreText => _report is { } r && r.BadAreaCount > BadAreaRows.Count
        ? Loc.Format("Test_BadAreasMore", BadAreaRows.Count, r.BadAreaCount.ToString("N0", Loc.Instance.Culture))
        : string.Empty;

    /// <summary>After a destructive test the device holds only the pattern: offer a fresh file system.</summary>
    public bool CanOfferFormat => _report is { Mode: not DeviceTestMode.Read, Verdict: not DeviceTestVerdict.FakeCapacity } && _testedDevice != null;

    /// <summary>A counterfeit device: a file system over its claimed size would lose data.</summary>
    public bool IsCounterfeitReport => _report is { Verdict: DeviceTestVerdict.FakeCapacity };

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
    {
        _rememberedDevice?.OnSelectionChanged(value);
        OnPropertyChanged(nameof(WorkText));
    }

    partial void OnSelectedFileSystemChanged(LocalizedOption<EraseFileSystem> value)
    {
        _settings.Settings.DeviceTestFileSystem = value.Value;
        _settings.Save();
    }

    partial void OnSamplesChanged(IReadOnlyList<DeviceTestSample> value) => OnPropertyChanged(nameof(HasChart));

    private bool CanStart()
        => SelectedDevice is { } device
           && (IsDestructive ? device.CanBeWritten : device.CanBeRead)
           && !Operation.IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var device = SelectedDevice!;
        var mode = Mode;
        if (mode != DeviceTestMode.Read)
        {
            var message = Loc.Format("Test_ConfirmMessage", device.Name, Formatting.Size(device.Info.Size), Loc.Get($"Test_Mode_{mode}"));
            if (!await _dialogs.ConfirmAsync(Loc.Get("Test_ConfirmTitle"), message, Loc.Get("Test_ConfirmButton")))
            {
                return;
            }
        }

        ClearReport();
        ChartLength = device.Info.Size;
        var token = Operation.Begin();
        var failed = false;
        Progress.Start();
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                Progress.Update(p);
                Operation.Report(p.Fraction);
            });

            var options = new DeviceTestOptions
            {
                Mode = mode,
                SampleReported = new Progress<DeviceTestSample>(sample =>
                {
                    _liveSamples.Add(sample);
                    Samples = _liveSamples.ToList();
                }),
            };

            var report = await DeviceTestOperation.RunAsync(
                () => _diskManager.Open(device.Info, mode == DeviceTestMode.Read ? DiskAccessMode.Read : DiskAccessMode.Write),
                options,
                progress,
                token);

            _report = report;
            _reportDevice = device.Name;
            _testedDevice = mode == DeviceTestMode.Read ? null : device;
            ChartLength = report.DeviceLength;
            Samples = report.Samples;
            OnPropertyChanged(string.Empty);
            FormatCommand.NotifyCanExecuteChanged();
            ShowReport(report);
            failed = report.Verdict != DeviceTestVerdict.Healthy;
        }
        catch (OperationCanceledException)
        {
            Result.Show(InfoBarSeverity.Warning, Loc.Get("Cancelled"), Loc.Get(mode == DeviceTestMode.Read ? "Test_CancelledRead" : "Test_CancelledDestructive"));
        }
        catch (Exception ex)
        {
            failed = true;
            Result.Show(InfoBarSeverity.Error, Loc.Get("Test_Failed"), Formatting.Error(ex));
        }
        finally
        {
            Progress.Stop();
            Operation.End(failed);
            _ = Devices.RefreshAsync();
        }
    }

    [RelayCommand]
    private void Cancel() => Operation.Cancel();

    private bool CanFormat() => CanOfferFormat && !Operation.IsBusy && SelectedDevice is { CanBeWritten: true } device && device.Key == _testedDevice!.Key;

    /// <summary>Creates a partition and FAT32/exFAT on the device that was just tested.</summary>
    [RelayCommand(CanExecute = nameof(CanFormat))]
    private async Task FormatAsync()
    {
        var device = SelectedDevice!;
        var token = Operation.Begin();
        var failed = false;
        Progress.Start();
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                Progress.Update(p);
                Operation.Report(p.Fraction);
            });

            var result = await EraseDeviceOperation.RunAsync(
                () => _diskManager.Open(device.Info, DiskAccessMode.Write),
                new EraseOptions { Mode = EraseMode.Quick, FileSystem = SelectedFileSystem.Value },
                progress,
                token);

            if (result.Layout is { } layout)
            {
                Result.Show(InfoBarSeverity.Success, Loc.Get("Test_Formatted"), Loc.Format("Erase_Formatted", Formatting.FileSystem(layout.FileSystem), Formatting.Size(layout.PartitionLength)));
            }

            _testedDevice = null;
            OnPropertyChanged(nameof(CanOfferFormat));
        }
        catch (OperationCanceledException)
        {
            Result.Show(InfoBarSeverity.Warning, Loc.Get("Cancelled"), string.Empty);
        }
        catch (Exception ex)
        {
            failed = true;
            Result.Show(InfoBarSeverity.Error, Loc.Get("Erase_Failed"), Formatting.Error(ex));
        }
        finally
        {
            Progress.Stop();
            Operation.End(failed);
            FormatCommand.NotifyCanExecuteChanged();
            _ = Devices.RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (_report is not { } report)
        {
            return;
        }

        var path = _dialogs.PickSaveFile(SuggestedName(".csv"), "CSV", ".csv");
        if (path == null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, DeviceTestReport.ToCsv(report, _reportDevice ?? string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Result.Show(InfoBarSeverity.Error, Loc.Get("Test_ExportFailed"), ex.Message);
        }
    }

    [RelayCommand]
    private void ExportPng()
    {
        var path = _dialogs.PickSaveFile(SuggestedName(".png"), "PNG", ".png");
        if (path != null)
        {
            PngExportRequested?.Invoke(this, path);
        }
    }

    /// <summary>Called by the view when saving the picture failed.</summary>
    public void ReportExportError(string message) => Result.Show(InfoBarSeverity.Error, Loc.Get("Test_ExportFailed"), message);

    private string SuggestedName(string extension)
    {
        var folder = _settings.Settings.LastSaveFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(folder, $"device-test-{DateTime.Now:yyyyMMdd-HHmm}{extension}");
    }

    private void ShowReport(DeviceTestResult report)
    {
        var lines = new List<string>();
        var severity = InfoBarSeverity.Success;
        string title;
        switch (report.Verdict)
        {
            case DeviceTestVerdict.FakeCapacity:
                severity = InfoBarSeverity.Error;
                title = Loc.Get("Test_Verdict_Fake");
                lines.Add(report.RealCapacity is { } real
                    ? Loc.Format(report.AddressesWrap ? "Test_FakeWraps" : "Test_FakeDrops", Formatting.Size(real), Formatting.Size(report.DeviceLength))
                    : Loc.Get("Test_FakeUnknownSize"));
                lines.Add(Loc.Get("Test_FakeAdvice"));
                break;
            case DeviceTestVerdict.BadAreas:
                severity = InfoBarSeverity.Warning;
                title = Loc.Get("Test_Verdict_Bad");
                lines.Add(Loc.Format("Test_BadSummary", report.BadAreaCount.ToString("N0", Loc.Instance.Culture), Formatting.Size(report.BadBytes)));
                break;
            default:
                title = Loc.Get(report.Mode == DeviceTestMode.QuickProbe ? "Test_Verdict_ProbeOk" : "Test_Verdict_Healthy");
                break;
        }

        foreach (var pass in report.Passes)
        {
            lines.Add(Loc.Format(
                pass.Phase == DeviceTestPhase.Write ? "Test_PassWrite" : "Test_PassRead",
                Speed(pass.AverageBytesPerSecond),
                Speed(pass.MinBytesPerSecond),
                Speed(pass.MaxBytesPerSecond)));
            lines.Add(Loc.Format("Test_Latency", Milliseconds(pass.MedianLatencyMs), Milliseconds(pass.MaxLatencyMs)));
        }

        var slow = report.SlowAreas.Count;
        if (slow > 0)
        {
            lines.Add(Loc.Format("Test_SlowAreas", slow, Formatting.Size(report.SlowAreas.Sum(a => a.Length))));
        }

        foreach (var random in report.RandomAccess)
        {
            lines.Add(Loc.Format(
                random.Phase == DeviceTestPhase.Write ? "Test_RandomWrite" : "Test_RandomRead",
                random.Iops.ToString("N0", Loc.Instance.Culture),
                Speed(random.BytesPerSecond),
                Milliseconds(random.AverageLatencyMs),
                Milliseconds(random.P99LatencyMs)));
        }

        lines.Add(Loc.Format("Test_Elapsed", Formatting.Duration(report.Elapsed)));
        if (report.Mode != DeviceTestMode.Read && report.Verdict != DeviceTestVerdict.FakeCapacity)
        {
            lines.Add(Loc.Get("Test_FormatHint"));
        }

        Result.Show(severity, title, string.Join(Environment.NewLine, lines));
    }

    private static string Speed(double bytesPerSecond) => Formatting.Size((long)bytesPerSecond) + Loc.Get("Test_PerSecond");

    private static string Milliseconds(double ms) => ms.ToString(ms >= 10 ? "N0" : "0.##", Loc.Instance.Culture);

    private void ClearReport()
    {
        _report = null;
        _reportDevice = null;
        _testedDevice = null;
        _liveSamples.Clear();
        Samples = [];
        Result.Hide();
        OnPropertyChanged(string.Empty);
        FormatCommand.NotifyCanExecuteChanged();
    }

    private void SetModeIf(bool selected, DeviceTestMode mode)
    {
        if (selected)
        {
            Mode = mode;
        }
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            StartCommand.NotifyCanExecuteChanged();
            FormatCommand.NotifyCanExecuteChanged();
        }
    }
}
