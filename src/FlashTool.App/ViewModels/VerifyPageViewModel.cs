using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Devices;
using FlashTool.Core.Operations;
using Wpf.Ui.Controls;

namespace FlashTool.App.ViewModels;

/// <summary>A row of the difference table.</summary>
public sealed record DifferenceRow(string Offset, string Length, string Location);

/// <summary>"Verify" page: byte-by-byte comparison of an image with a device.</summary>
public sealed partial class VerifyPageViewModel : LocalizedViewModel
{
    private const int MaxRowsShown = 500;

    private readonly IDiskManager _diskManager;
    private readonly IoErrorPrompt _ioErrors;
    private readonly SettingsService _settings;
    private readonly RememberedDevice? _rememberedDevice;
    private VerifyImageResult? _report;

    /// <summary>Differences found so far by the running (or interrupted) comparison.</summary>
    private VerifyDifferences? _live;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    [NotifyPropertyChangedFor(nameof(SizeWarning))]
    private DeviceItemViewModel? _selectedDevice;

    public VerifyPageViewModel(
        ImageSourceViewModel image,
        IDiskManager diskManager,
        DeviceListService devices,
        OperationState operation,
        IoErrorPrompt ioErrors,
        SettingsService settings)
    {
        Image = image;
        _diskManager = diskManager;
        _ioErrors = ioErrors;
        Devices = devices;
        Operation = operation;
        Operation.PropertyChanged += OnOperationChanged;
        Image.Loading += (_, _) => ClearReport();
        Image.PropertyChanged += OnImageChanged;

        _settings = settings;
        var selection = settings.Settings.VerifySelection;
        Image.SelectionChanged += (_, _) =>
        {
            selection.Source = Image.LoadedSource;
            selection.Entry = Image.SelectedEntry?.Name;
            settings.Save();
        };
        _rememberedDevice = new RememberedDevice(
            devices,
            () => selection.DeviceKey,
            key =>
            {
                selection.DeviceKey = key;
                settings.Save();
            },
            () => SelectedDevice,
            d => SelectedDevice = d,
            d => d.CanBeRead);
    }

    /// <summary>Reopens the image used last time.</summary>
    public Task RestoreAsync() => Image.RestoreAsync(_settings.Settings.VerifySelection.Source, _settings.Settings.VerifySelection.Entry);

    /// <summary>The reference image.</summary>
    public ImageSourceViewModel Image { get; }

    public DeviceListService Devices { get; }

    public OperationState Operation { get; }

    public ProgressViewModel Progress { get; } = new();

    public ResultViewModel Result { get; } = new();

    public string? SizeWarning
        => Image.SelectedEntry?.Entry.Length is { } length && SelectedDevice is { HasMedia: true } device && length > device.Info.Size
            ? Loc.Format("Image_TooLarge", Formatting.Size(length), Formatting.Size(device.Info.Size))
            : null;

    public bool HasReport => _report != null || _live != null;

    /// <summary>Hashes exist only once the whole image was compared.</summary>
    public bool HasHashes => _report != null;

    public string ImageHash => _report?.ImageSha256 ?? string.Empty;

    public string DeviceHash => _report?.DeviceSha256 ?? string.Empty;

    public bool HasDifferences => _report is { Identical: false } || (_report == null && _live != null);

    private IReadOnlyList<DifferenceRange>? Ranges => _report?.Ranges ?? _live?.Ranges;

    private long RangeCount => _report?.DifferenceRangeCount ?? _live?.RangeCount ?? 0;

    public IReadOnlyList<DifferenceRow> Differences => Ranges?
        .Take(MaxRowsShown)
        .Select(r => new DifferenceRow(
            $"0x{r.Offset:X} ({Formatting.Size(r.Offset)})",
            r.Length.ToString("N0", Loc.Instance.Culture),
            r.Area switch
            {
                DifferenceArea.PartitionTable => Loc.Get("Area_PartitionTable"),
                DifferenceArea.Partition => Loc.Format("Area_Partition", r.PartitionNumber),
                _ => Loc.Get("Area_Unpartitioned"),
            }))
        .ToList() ?? [];

    public string? MoreRangesText
        => RangeCount > Differences.Count
            ? Loc.Format("Verify_MoreRanges", Differences.Count, RangeCount.ToString("N0", Loc.Instance.Culture))
            : null;

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
    {
        _rememberedDevice?.OnSelectionChanged(value);
        if (!Operation.IsBusy)
        {
            ClearReport();
        }
    }

    private bool CanVerify()
        => Image.SelectedEntry is { IsChecking: false }
           && Image.Container != null
           && SelectedDevice is { CanBeRead: true }
           && SizeWarning == null
           && !Image.IsLoading
           && !Operation.IsBusy;

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        var entry = Image.SelectedEntry!;
        var device = SelectedDevice!;
        var container = Image.Container!;

        var token = Operation.Begin();
        var failed = false;
        ClearReport();
        Progress.Start();
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                Progress.Update(p);
                Operation.Report(p.Fraction);
            });

            var report = await VerifyImageOperation.RunAsync(
                container,
                entry.Entry,
                () => _diskManager.Open(device.Info, DiskAccessMode.Read),
                new VerifyImageOptions
                {
                    IoRetry = _ioErrors.Policy,
                    DifferencesFound = new Progress<VerifyDifferences>(OnDifferencesFound),
                },
                progress,
                token);

            SetReport(report);
            if (report.Identical)
            {
                Result.Show(
                    InfoBarSeverity.Success,
                    Loc.Get("Verify_Identical"),
                    Loc.Format("Verify_IdenticalDetails", Formatting.Size(report.BytesCompared), Formatting.Duration(report.Elapsed)));
            }
            else
            {
                failed = true;
                var details = Loc.Format(
                    "Verify_DifferentDetails",
                    Formatting.Size(report.DifferentBytes),
                    Formatting.Size(report.BytesCompared),
                    report.DifferenceRangeCount.ToString("N0", CultureInfo.CurrentCulture));
                Result.Show(InfoBarSeverity.Error, Loc.Get("Verify_Different"), details + Environment.NewLine + Loc.Get("Verify_ExpandNote"));
            }
        }
        catch (OperationCanceledException)
        {
            // Differences found before cancelling stay visible: they are already a verdict.
            if (_live is { } live)
            {
                failed = true;
                Result.Show(InfoBarSeverity.Error, Loc.Get("Verify_Different"), Loc.Get("Verify_CancelledAfterDifferences") + Environment.NewLine + DescribeLive(live));
            }
            else
            {
                Result.Show(InfoBarSeverity.Warning, Loc.Get("Cancelled"), string.Empty);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            var details = Formatting.Error(ex);
            if (_live is { } live)
            {
                details += Environment.NewLine + DescribeLive(live);
            }

            Result.Show(InfoBarSeverity.Error, Loc.Get("Verify_Failed"), details);
        }
        finally
        {
            Progress.Stop();
            Operation.End(failed);
        }
    }

    [RelayCommand]
    private void Cancel() => Operation.Cancel();

    /// <summary>Shows the mismatch as soon as it is found; the comparison goes on to map all of them.</summary>
    private void OnDifferencesFound(VerifyDifferences live)
    {
        if (!Operation.IsBusy || _report != null)
        {
            // A late update from an operation that has already finished.
            return;
        }

        _live = live;
        Result.Show(InfoBarSeverity.Error, Loc.Get("Verify_DifferentLive"), DescribeLive(live));
        NotifyReportChanged();
    }

    private static string DescribeLive(VerifyDifferences live)
        => Loc.Format(
            "Verify_DifferentSoFar",
            Formatting.Size(live.DifferentBytes),
            live.RangeCount.ToString("N0", CultureInfo.CurrentCulture),
            live.Ranges.Count > 0 ? $"0x{live.Ranges[0].Offset:X} ({Formatting.Size(live.Ranges[0].Offset)})" : "—");

    private void SetReport(VerifyImageResult? report)
    {
        _report = report;
        _live = null;
        NotifyReportChanged();
    }

    private void NotifyReportChanged()
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HasHashes));
        OnPropertyChanged(nameof(ImageHash));
        OnPropertyChanged(nameof(DeviceHash));
        OnPropertyChanged(nameof(HasDifferences));
        OnPropertyChanged(nameof(Differences));
        OnPropertyChanged(nameof(MoreRangesText));
    }

    private void ClearReport()
    {
        Result.Hide();
        SetReport(null);
    }

    private void OnImageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageSourceViewModel.SelectedEntry) or nameof(ImageSourceViewModel.IsLoading))
        {
            OnPropertyChanged(nameof(SizeWarning));
            VerifyCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            VerifyCommand.NotifyCanExecuteChanged();
        }
    }
}
