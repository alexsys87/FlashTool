using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Devices;
using FlashTool.Core.FileSystems.Ext4;
using FlashTool.Core.Operations;
using FlashTool.Core.Partitioning;
using FlashTool.Platform.Windows;
using Wpf.Ui.Controls;

namespace FlashTool.App.ViewModels;

/// <summary>"Write image" page: pick an image (file, archive or URL) and a device.</summary>
public sealed partial class WritePageViewModel : LocalizedViewModel
{
    private readonly IDiskManager _diskManager;
    private readonly IoErrorPrompt _ioErrors;
    private readonly RememberedDevice? _rememberedDevice;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    [NotifyPropertyChangedFor(nameof(SizeWarning))]
    private DeviceItemViewModel? _selectedDevice;

    public WritePageViewModel(
        ImageSourceViewModel image,
        IDiskManager diskManager,
        SettingsService settings,
        DialogService dialogs,
        DeviceListService devices,
        OperationState operation,
        IoErrorPrompt ioErrors)
    {
        Image = image;
        _diskManager = diskManager;
        _ioErrors = ioErrors;
        _settings = settings;
        _dialogs = dialogs;
        Devices = devices;
        Operation = operation;
        Operation.PropertyChanged += OnOperationChanged;
        Image.Loading += (_, _) => Result.Hide();
        Image.PropertyChanged += OnImageChanged;

        var selection = settings.Settings.WriteSelection;
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
            d => d.CanBeWritten);
    }

    /// <summary>Reopens the image used last time.</summary>
    public Task RestoreAsync() => Image.RestoreAsync(_settings.Settings.WriteSelection.Source, _settings.Settings.WriteSelection.Entry);

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value) => _rememberedDevice?.OnSelectionChanged(value);

    /// <summary>The image to write.</summary>
    public ImageSourceViewModel Image { get; }

    public DeviceListService Devices { get; }

    public OperationState Operation { get; }

    public ProgressViewModel Progress { get; } = new();

    public ResultViewModel Result { get; } = new();

    public string? SizeWarning
        => Image.SelectedEntry?.Entry.Length is { } length && SelectedDevice is { HasMedia: true } device && length > device.Info.Size
            ? Loc.Format("Image_TooLarge", Formatting.Size(length), Formatting.Size(device.Info.Size))
            : null;

    public bool Verify
    {
        get => _settings.Settings.VerifyAfterWrite;
        set
        {
            _settings.Settings.VerifyAfterWrite = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool ExpandLastPartition
    {
        get => _settings.Settings.ExpandLastPartition;
        set
        {
            _settings.Settings.ExpandLastPartition = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    private bool CanWrite()
        => Image.SelectedEntry is { IsChecking: false }
           && Image.Container != null
           && SelectedDevice is { CanBeWritten: true }
           && SizeWarning == null
           && !Image.IsLoading
           && !Operation.IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        var entry = Image.SelectedEntry!;
        var device = SelectedDevice!;
        var container = Image.Container!;

        var requireImage = true;
        if (!entry.IsDiskImage)
        {
            if (!await _dialogs.ConfirmAsync(Loc.Get("Write_NotImageTitle"), Loc.Format("Write_NotImageMessage", entry.Name), Loc.Get("Write_WriteAnyway")))
            {
                return;
            }

            requireImage = false;
        }

        var message = Loc.Format("Write_ConfirmMessage", device.Name, Formatting.Size(device.Info.Size));
        if (!await _dialogs.ConfirmAsync(Loc.Get("Write_ConfirmTitle"), message, Loc.Get("Write_ConfirmButton")))
        {
            return;
        }

        var options = new WriteImageOptions
        {
            Verify = Verify,
            ExpandLastPartition = ExpandLastPartition,
            RequireRecognizedImage = requireImage,
            FileSystemResizers = [new Ext4Resizer(), new NtfsVolumeExtender(device.Info.Number)],
            IoRetry = _ioErrors.Policy,
        };

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

            var result = await WriteImageOperation.RunAsync(
                container,
                entry.Entry,
                () => _diskManager.Open(device.Info, DiskAccessMode.Write),
                options,
                progress,
                token);

            Result.Show(InfoBarSeverity.Success, Loc.Get("Write_Success"), DescribeResult(result));
        }
        catch (OperationCanceledException)
        {
            Result.Show(InfoBarSeverity.Warning, Loc.Get("Cancelled"), string.Empty);
        }
        catch (Exception ex)
        {
            failed = true;
            Result.Show(InfoBarSeverity.Error, Loc.Get("Write_Failed"), Formatting.Error(ex));
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

    private static string DescribeResult(WriteImageResult result)
    {
        var lines = new List<string> { Loc.Format("Write_SuccessDetails", Formatting.Size(result.BytesWritten), Formatting.Duration(result.Elapsed)) };
        if (result.Verified)
        {
            lines.Add(Loc.Get("Write_Verified"));
        }

        if (result.Fixups is { } f)
        {
            if (f.GptBackupRelocated)
            {
                lines.Add(Loc.Get("Write_GptRelocated"));
            }

            if (f.PartitionExtended && f.Partition is { } p)
            {
                lines.Add(Loc.Format("Write_PartitionExtended", p.Number, p.TypeDescription, Formatting.Size(f.OldLength), Formatting.Size(f.NewLength)));
                if (f.FileSystemResizeError != null)
                {
                    lines.Add(Loc.Format("Write_FsResizeFailed", p.FileSystem, f.FileSystemResizeError));
                }
                else if (f.FileSystemResized)
                {
                    lines.Add(Loc.Format("Write_FsResized", p.FileSystem));
                }
                else if (p.FileSystem != FileSystemType.Unknown)
                {
                    lines.Add(Loc.Format("Write_FsNotResized", p.FileSystem));
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void OnImageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageSourceViewModel.SelectedEntry) or nameof(ImageSourceViewModel.IsLoading))
        {
            OnPropertyChanged(nameof(SizeWarning));
            WriteCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            WriteCommand.NotifyCanExecuteChanged();
        }
    }
}
