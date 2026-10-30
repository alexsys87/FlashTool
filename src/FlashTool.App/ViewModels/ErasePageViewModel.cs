using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core;
using FlashTool.Core.Devices;
using FlashTool.Core.FileSystems.Fat;
using FlashTool.Core.Operations;
using FlashTool.Core.Partitioning;
using Wpf.Ui.Controls;

namespace FlashTool.App.ViewModels;

/// <summary>"Erase" page: wipe a device (quick, full or destructive) and create a new file system.</summary>
public sealed partial class ErasePageViewModel : LocalizedViewModel
{
    private readonly IDiskManager _diskManager;
    private readonly IoErrorPrompt _ioErrors;
    private readonly RememberedDevice? _rememberedDevice;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    private LocalizedOption<ErasePattern> _selectedPattern;

    [ObservableProperty]
    private LocalizedOption<EraseFileSystem> _selectedFileSystem;

    [ObservableProperty]
    private LocalizedOption<ErasePartitionScheme> _selectedScheme;

    [ObservableProperty]
    private string _label;

    public ErasePageViewModel(
        IDiskManager diskManager,
        SettingsService settings,
        DialogService dialogs,
        DeviceListService devices,
        OperationState operation,
        IoErrorPrompt ioErrors)
    {
        _diskManager = diskManager;
        _ioErrors = ioErrors;
        _settings = settings;
        _dialogs = dialogs;
        Devices = devices;
        Operation = operation;
        Operation.PropertyChanged += OnOperationChanged;

        Patterns = Enum.GetValues<ErasePattern>().Select(p => new LocalizedOption<ErasePattern>(p, $"Pattern_{p}")).ToList();
        FileSystems = Enum.GetValues<EraseFileSystem>().Select(f => new LocalizedOption<EraseFileSystem>(f, $"FileSystem_{f}")).ToList();
        Schemes = Enum.GetValues<ErasePartitionScheme>().Select(s => new LocalizedOption<ErasePartitionScheme>(s, $"Scheme_{s}")).ToList();

        var saved = settings.Settings;
        _selectedPattern = Patterns.FirstOrDefault(p => p.Value == saved.ErasePattern) ?? Patterns[0];
        _selectedFileSystem = FileSystems.FirstOrDefault(f => f.Value == saved.EraseFileSystem) ?? FileSystems[0];
        _selectedScheme = Schemes.FirstOrDefault(s => s.Value == saved.ErasePartitionScheme) ?? Schemes[0];
        _label = saved.EraseLabel;

        _rememberedDevice = new RememberedDevice(
            devices,
            () => settings.Settings.EraseDeviceKey,
            key =>
            {
                settings.Settings.EraseDeviceKey = key;
                settings.Save();
            },
            () => SelectedDevice,
            d => SelectedDevice = d,
            d => d.CanBeWritten);
    }

    public DeviceListService Devices { get; }

    public OperationState Operation { get; }

    public ProgressViewModel Progress { get; } = new();

    public ResultViewModel Result { get; } = new();

    public IReadOnlyList<LocalizedOption<ErasePattern>> Patterns { get; }

    public IReadOnlyList<LocalizedOption<EraseFileSystem>> FileSystems { get; }

    public IReadOnlyList<LocalizedOption<ErasePartitionScheme>> Schemes { get; }

    public static int MaxLabelLength => Fat32Formatter.MaxLabelLength;

    public EraseMode Mode
    {
        get => _settings.Settings.EraseMode;
        set
        {
            if (_settings.Settings.EraseMode == value)
            {
                return;
            }

            _settings.Settings.EraseMode = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsQuick));
            OnPropertyChanged(nameof(IsFull));
            OnPropertyChanged(nameof(IsSecure));
            OnPropertyChanged(nameof(WorkText));
        }
    }

    // One flag per radio button.
    public bool IsQuick
    {
        get => Mode == EraseMode.Quick;
        set => SetModeIf(value, EraseMode.Quick);
    }

    public bool IsFull
    {
        get => Mode == EraseMode.Full;
        set => SetModeIf(value, EraseMode.Full);
    }

    public bool IsSecure
    {
        get => Mode == EraseMode.Secure;
        set => SetModeIf(value, EraseMode.Secure);
    }

    public bool Verify
    {
        get => _settings.Settings.EraseVerify;
        set
        {
            _settings.Settings.EraseVerify = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(WorkText));
        }
    }

    /// <summary>False when the device is left without a file system.</summary>
    public bool CreatesFileSystem => SelectedFileSystem.Value != EraseFileSystem.None;

    /// <summary>What will be created on the selected device, or why it cannot be.</summary>
    public string PlanText
    {
        get
        {
            if (SelectedDevice is not { HasMedia: true } device || PlanError != null)
            {
                return string.Empty;
            }

            var layout = TryPlan(device, out _);
            return layout == null
                ? Loc.Get("Erase_PlanEmpty")
                : Loc.Format("Erase_Plan", Formatting.FileSystem(layout.FileSystem), Formatting.Size(layout.PartitionLength), layout.Table.ToString().ToUpperInvariant(), Formatting.Size(layout.ClusterSize));
        }
    }

    /// <summary>Set when the chosen layout does not fit the selected device.</summary>
    public string? PlanError
    {
        get
        {
            if (SelectedDevice is not { HasMedia: true } device)
            {
                return null;
            }

            TryPlan(device, out var error);
            return error;
        }
    }

    /// <summary>How much the chosen method writes and reads.</summary>
    public string WorkText
    {
        get
        {
            if (SelectedDevice is not { HasMedia: true } device || Mode == EraseMode.Quick)
            {
                return string.Empty;
            }

            var size = Formatting.Size(device.Info.Size);
            return Mode == EraseMode.Secure && Verify ? Loc.Format("Erase_WillWriteAndRead", size) : Loc.Format("Erase_WillWrite", size);
        }
    }

    /// <summary>How a FAT32 label will really be stored, when that differs from the input.</summary>
    public string? LabelNote
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Label) || SelectedDevice is not { HasMedia: true } device
                || TryPlan(device, out _)?.FileSystem != FileSystemType.Fat32)
            {
                return null;
            }

            var stored = Fat32Formatter.SanitizeLabel(Label);
            return stored == Label.Trim() ? null : Loc.Format("Erase_LabelAs", stored);
        }
    }

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
    {
        _rememberedDevice?.OnSelectionChanged(value);
        OnPlanChanged();
        OnPropertyChanged(nameof(WorkText));
    }

    partial void OnSelectedPatternChanged(LocalizedOption<ErasePattern> value)
    {
        _settings.Settings.ErasePattern = value.Value;
        _settings.Save();
    }

    partial void OnSelectedFileSystemChanged(LocalizedOption<EraseFileSystem> value)
    {
        _settings.Settings.EraseFileSystem = value.Value;
        _settings.Save();
        OnPropertyChanged(nameof(CreatesFileSystem));
        OnPlanChanged();
    }

    partial void OnSelectedSchemeChanged(LocalizedOption<ErasePartitionScheme> value)
    {
        _settings.Settings.ErasePartitionScheme = value.Value;
        _settings.Save();
        OnPlanChanged();
    }

    partial void OnLabelChanged(string value)
    {
        _settings.Settings.EraseLabel = value;
        _settings.Save();
        OnPropertyChanged(nameof(LabelNote));
    }

    private bool CanStart()
        => SelectedDevice is { CanBeWritten: true }
           && PlanError == null
           && !Operation.IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var device = SelectedDevice!;
        var message = Loc.Format("Erase_ConfirmMessage", device.Name, Formatting.Size(device.Info.Size), ModeName(Mode));
        if (!await _dialogs.ConfirmAsync(Loc.Get("Erase_ConfirmTitle"), message, Loc.Get("Erase_ConfirmButton")))
        {
            return;
        }

        var options = BuildOptions() with { IoRetry = _ioErrors.Policy };
        var token = Operation.Begin();
        var failed = false;
        Result.Hide();
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
                options,
                progress,
                token);

            Result.Show(InfoBarSeverity.Success, Loc.Get("Erase_Success"), DescribeResult(result));
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
            _ = Devices.RefreshAsync();
        }
    }

    [RelayCommand]
    private void Cancel() => Operation.Cancel();

    private EraseOptions BuildOptions() => new()
    {
        Mode = Mode,
        Pattern = SelectedPattern.Value,
        Verify = Verify,
        FileSystem = SelectedFileSystem.Value,
        PartitionScheme = SelectedScheme.Value,
        Label = Label.Trim(),
    };

    private EraseLayout? TryPlan(DeviceItemViewModel device, out string? error)
    {
        error = null;
        try
        {
            return EraseDeviceOperation.Plan(device.Info.Size, device.Info.SectorSize, BuildOptions());
        }
        catch (FlashToolException ex)
        {
            error = Formatting.Error(ex);
            return null;
        }
    }

    private static string DescribeResult(EraseResult result)
    {
        var lines = new List<string> { Loc.Format("Erase_SuccessDetails", Formatting.Duration(result.Elapsed)) };
        if (result.BytesOverwritten > 0)
        {
            lines.Add(Loc.Format("Erase_Overwritten", Formatting.Size(result.BytesOverwritten)));
        }

        if (result.Verified)
        {
            lines.Add(Loc.Get("Erase_Verified"));
        }

        if (result.Layout is { } layout)
        {
            lines.Add(Loc.Format("Erase_Formatted", Formatting.FileSystem(layout.FileSystem), Formatting.Size(layout.PartitionLength)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ModeName(EraseMode mode) => Loc.Get($"Erase_{mode}");

    private void SetModeIf(bool selected, EraseMode mode)
    {
        if (selected)
        {
            Mode = mode;
        }
    }

    private void OnPlanChanged()
    {
        OnPropertyChanged(nameof(PlanText));
        OnPropertyChanged(nameof(PlanError));
        OnPropertyChanged(nameof(LabelNote));
        StartCommand.NotifyCanExecuteChanged();
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            StartCommand.NotifyCanExecuteChanged();
        }
    }
}
