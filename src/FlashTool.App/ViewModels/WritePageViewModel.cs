using System.ComponentModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Checksums;
using FlashTool.Core.Containers;
using FlashTool.Core.Devices;
using FlashTool.Core.FileSystems.Ext4;
using FlashTool.Core.Operations;
using FlashTool.Core.Partitioning;
using FlashTool.Core.Sources;
using FlashTool.Platform.Windows;
using Wpf.Ui.Controls;

namespace FlashTool.App.ViewModels;

/// <summary>"Write image" page: pick an image (file, archive or URL) and a device.</summary>
public sealed partial class WritePageViewModel : LocalizedViewModel
{
    private enum ChecksumSearchState
    {
        Idle,
        Searching,
        Found,
        NotFound,
    }

    private readonly IDiskManager _diskManager;
    private readonly IoErrorPrompt _ioErrors;
    private readonly RememberedDevice? _rememberedDevice;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly HttpClient _http;
    private CancellationTokenSource? _checksumSearch;
    private IStreamSource? _checksumSearchedFor;
    private ChecksumSearchState _checksumSearchState;
    private string? _foundChecksumOrigin;
    private bool _fillingFoundChecksum;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    [NotifyPropertyChangedFor(nameof(SizeWarning))]
    private DeviceItemViewModel? _selectedDevice;

    /// <summary>SHA-256 of the image file: typed by the user or found next to the image.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private string _expectedChecksum = string.Empty;

    public WritePageViewModel(
        ImageSourceViewModel image,
        IDiskManager diskManager,
        SettingsService settings,
        DialogService dialogs,
        DeviceListService devices,
        OperationState operation,
        IoErrorPrompt ioErrors,
        HttpClient http)
    {
        Image = image;
        _diskManager = diskManager;
        _ioErrors = ioErrors;
        _settings = settings;
        _dialogs = dialogs;
        _http = http;
        Devices = devices;
        Operation = operation;
        Operation.PropertyChanged += OnOperationChanged;
        Image.Loading += (_, _) =>
        {
            Result.Hide();
            ResetChecksum();
        };
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

    public bool EjectAfterWrite
    {
        get => _settings.Settings.EjectAfterWrite;
        set
        {
            _settings.Settings.EjectAfterWrite = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    private bool CanWrite()
        => Image.SelectedEntry is { IsChecking: false }
           && Image.Container != null
           && SelectedDevice is { CanBeWritten: true }
           && SizeWarning == null
           && !HasChecksumError
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
            ExpectedSourceSha256 = Sha256Hex.TryParse(ExpectedChecksum, out var expectedChecksum) ? expectedChecksum : null,
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

            var details = DescribeResult(result);
            var severity = InfoBarSeverity.Success;
            if (EjectAfterWrite)
            {
                // The operation has closed the disk by now, so nothing of ours keeps it busy.
                Progress.Update(new OperationProgress(OperationPhase.Ejecting, 0, null, 0, 0, null));
                try
                {
                    await Task.Run(() => _diskManager.Eject(device.Info), CancellationToken.None);
                    details += Environment.NewLine + Loc.Get("Write_Ejected");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    severity = InfoBarSeverity.Warning;
                    details += Environment.NewLine + Formatting.Error(ex);
                }
            }

            Result.Show(severity, Loc.Get("Write_Success"), details);
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

        switch (result.SourceChecksum)
        {
            case SourceChecksumStatus.Verified:
                lines.Add(Loc.Get("Write_ChecksumVerified"));
                break;
            case SourceChecksumStatus.Unverifiable:
                lines.Add(Loc.Get("Write_ChecksumNotVerified"));
                break;
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
            OnPropertyChanged(nameof(ChecksumStatus));
            WriteCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(ImageSourceViewModel.IsLoading) && !Image.IsLoading && Image.Container is { } container && !ReferenceEquals(container.Source, _checksumSearchedFor))
        {
            StartChecksumSearch(container.Source);
        }
    }

    /// <summary>One line under the checksum box: what was found, how the file will be checked, or what is wrong.</summary>
    public string ChecksumStatus
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ExpectedChecksum))
            {
                if (HasChecksumError)
                {
                    return Loc.Get("Write_ChecksumInvalid");
                }

                if (Image.Container is { Source.IsRemote: true, Format: ContainerFormat.Zip or ContainerFormat.SevenZip })
                {
                    return Loc.Get("Write_ChecksumUnverifiableFormat");
                }

                var when = Loc.Get(Image.Container is { Source.IsRemote: true } ? "Write_ChecksumWhileDownloading" : "Write_ChecksumBeforeWriting");
                return _foundChecksumOrigin is { } origin ? Loc.Format("Write_ChecksumFound", origin, when) : when;
            }

            return _checksumSearchState switch
            {
                ChecksumSearchState.Searching => Loc.Get("Write_ChecksumSearching"),
                ChecksumSearchState.NotFound => Loc.Get("Write_ChecksumNotFound"),
                _ => string.Empty,
            };
        }
    }

    private bool HasChecksumError => !string.IsNullOrWhiteSpace(ExpectedChecksum) && !Sha256Hex.TryParse(ExpectedChecksum, out _);

    partial void OnExpectedChecksumChanged(string value)
    {
        // Anything that is not the value we filled in ourselves is the user's.
        if (!_fillingFoundChecksum)
        {
            _foundChecksumOrigin = null;
        }

        OnPropertyChanged(nameof(ChecksumStatus));
    }

    /// <summary>The checksum belongs to the image: a new image starts with an empty box.</summary>
    private void ResetChecksum()
    {
        _checksumSearch?.Cancel();
        _checksumSearchedFor = null;
        _checksumSearchState = ChecksumSearchState.Idle;
        _foundChecksumOrigin = null;
        ExpectedChecksum = string.Empty;
        OnPropertyChanged(nameof(ChecksumStatus));
    }

    private void StartChecksumSearch(IStreamSource source)
    {
        _checksumSearch?.Cancel();
        var cts = _checksumSearch = new CancellationTokenSource();
        _checksumSearchedFor = source;
        _checksumSearchState = ChecksumSearchState.Searching;
        OnPropertyChanged(nameof(ChecksumStatus));
        _ = SearchChecksumAsync(source, cts);
    }

    private async Task SearchChecksumAsync(IStreamSource source, CancellationTokenSource cts)
    {
        FoundChecksum? found = null;
        try
        {
            if (!source.IsRemote)
            {
                found = await Task.Run(() => ChecksumDiscovery.FindForFile(source.Location), cts.Token);
            }
            else if (Uri.TryCreate(source.Location, UriKind.Absolute, out var uri))
            {
                found = await ChecksumDiscovery.FindForUrlAsync(_http, uri, cts.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Looking for a checksum is a convenience: a failure, or a newer image that cancelled this search,
            // just means nothing was found.
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        if (found != null && string.IsNullOrWhiteSpace(ExpectedChecksum))
        {
            _foundChecksumOrigin = found.Origin;
            _fillingFoundChecksum = true;
            try
            {
                ExpectedChecksum = Sha256Hex.Format(found.Sha256);
            }
            finally
            {
                _fillingFoundChecksum = false;
            }
        }

        _checksumSearchState = found != null ? ChecksumSearchState.Found : ChecksumSearchState.NotFound;
        OnPropertyChanged(nameof(ChecksumStatus));
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            WriteCommand.NotifyCanExecuteChanged();
        }
    }
}
