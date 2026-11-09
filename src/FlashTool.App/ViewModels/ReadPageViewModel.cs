using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Containers;
using FlashTool.Core.Devices;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using Wpf.Ui.Controls;

namespace FlashTool.App.ViewModels;

/// <summary>"Create image" page: read a device into a (compressed) image file.</summary>
public sealed partial class ReadPageViewModel : LocalizedViewModel
{
    private readonly ContainerRegistry _registry;
    private readonly IDiskManager _diskManager;
    private readonly IoErrorPrompt _ioErrors;
    private readonly RememberedDevice? _rememberedDevice;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly FreeBlockSkipping _freeBlocks;
    private readonly BmapFeature _bmap;
    private ImageInfo? _deviceLayout;
    private string? _layoutError;
    private int _layoutRequest;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    private LocalizedOption<ContainerFormat> _selectedFormat;

    [ObservableProperty]
    private LocalizedOption<CompressionPreset> _selectedCompression;

    public ReadPageViewModel(
        ContainerRegistry registry,
        IDiskManager diskManager,
        SettingsService settings,
        DialogService dialogs,
        DeviceListService devices,
        OperationState operation,
        IoErrorPrompt ioErrors,
        FreeBlockSkipping freeBlocks,
        BmapFeature bmap)
    {
        _registry = registry;
        _diskManager = diskManager;
        _ioErrors = ioErrors;
        _settings = settings;
        _dialogs = dialogs;
        _freeBlocks = freeBlocks;
        _freeBlocks.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SkipFreeBlocks));
        _bmap = bmap;
        _bmap.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CreateBmap));
        Devices = devices;
        Operation = operation;
        Operation.PropertyChanged += OnOperationChanged;

        Formats = registry.Handlers
            .Where(h => h.CanCreate && h.IsAvailable)
            .OrderBy(h => h.Format)
            .Select(h => new LocalizedOption<ContainerFormat>(h.Format, $"Format_{h.Format}"))
            .ToList();
        Compressions = Enum.GetValues<CompressionPreset>()
            .Select(p => new LocalizedOption<CompressionPreset>(p, $"Compression_{p}"))
            .ToList();

        _destinationPath = settings.Settings.ReadDestination ?? string.Empty;
        _rememberedDevice = new RememberedDevice(
            devices,
            () => settings.Settings.ReadDeviceKey,
            key =>
            {
                settings.Settings.ReadDeviceKey = key;
                settings.Save();
            },
            () => SelectedDevice,
            d => SelectedDevice = d,
            d => d.CanBeRead);

        _selectedFormat = Formats.FirstOrDefault(f => f.Value == settings.Settings.ReadFormat) ?? Formats[0];
        _selectedCompression = Compressions.FirstOrDefault(c => c.Value == settings.Settings.ReadCompression) ?? Compressions[2];
    }

    public DeviceListService Devices { get; }

    public OperationState Operation { get; }

    public ProgressViewModel Progress { get; } = new();

    public ResultViewModel Result { get; } = new();

    public IReadOnlyList<LocalizedOption<ContainerFormat>> Formats { get; }

    public IReadOnlyList<LocalizedOption<CompressionPreset>> Compressions { get; }

    public bool IsCompressed => SelectedFormat.Value != ContainerFormat.Raw;

    public bool OnlyAllocatedPartitions
    {
        get => _settings.Settings.OnlyAllocatedPartitions;
        set
        {
            _settings.Settings.OnlyAllocatedPartitions = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(WillReadText));
        }
    }

    /// <summary>Turned on in the settings; shown on the page so the user knows the image will not be an exact copy.</summary>
    public bool SkipFreeBlocks => _freeBlocks.IsEnabled;

    /// <summary>A .bmap is saved next to the image (turned on in the settings).</summary>
    public bool CreateBmap => _bmap.CreateBmap;

    public bool Verify
    {
        get => _settings.Settings.VerifyAfterRead;
        set
        {
            _settings.Settings.VerifyAfterRead = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool CreateChecksumFile
    {
        get => _settings.Settings.CreateChecksumFile;
        set
        {
            _settings.Settings.CreateChecksumFile = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string LayoutText
    {
        get
        {
            if (_layoutError != null)
            {
                return Loc.Format("Read_DeviceLayoutError", _layoutError);
            }

            if (SelectedDevice == null || _deviceLayout == null)
            {
                return string.Empty;
            }

            return _deviceLayout.UsedLength is { } used && _deviceLayout.IsDiskImage
                ? Loc.Format("Read_DeviceLayout", Formatting.ImageKind(_deviceLayout.Kind), Formatting.Size(used), Formatting.Size(SelectedDevice.Info.Size))
                : Loc.Get("Read_DeviceLayoutUnknown");
        }
    }

    public string WillReadText
    {
        get
        {
            if (SelectedDevice == null)
            {
                return string.Empty;
            }

            var length = SelectedDevice.Info.Size;
            if (OnlyAllocatedPartitions && _deviceLayout?.UsedLength is { } used && used > 0)
            {
                length = Math.Min(length, used);
            }

            return Loc.Format("Read_WillRead", Formatting.Size(length));
        }
    }

    partial void OnDestinationPathChanged(string value)
    {
        _settings.Settings.ReadDestination = value;
        _settings.Save();
    }

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
    {
        _rememberedDevice?.OnSelectionChanged(value);
        _deviceLayout = null;
        _layoutError = null;
        OnPropertyChanged(nameof(LayoutText));
        OnPropertyChanged(nameof(WillReadText));
        if (value is { CanBeRead: true })
        {
            if (string.IsNullOrWhiteSpace(DestinationPath))
            {
                DestinationPath = SuggestPath(value);
            }

            _ = LoadLayoutAsync(value);
        }
    }

    partial void OnSelectedFormatChanged(LocalizedOption<ContainerFormat> value)
    {
        _settings.Settings.ReadFormat = value.Value;
        _settings.Save();
        OnPropertyChanged(nameof(IsCompressed));
        if (!string.IsNullOrWhiteSpace(DestinationPath))
        {
            DestinationPath = ApplyExtension(DestinationPath, value.Value);
        }
    }

    partial void OnSelectedCompressionChanged(LocalizedOption<CompressionPreset> value)
    {
        _settings.Settings.ReadCompression = value.Value;
        _settings.Save();
    }

    private async Task LoadLayoutAsync(DeviceItemViewModel device)
    {
        var request = ++_layoutRequest;
        try
        {
            var layout = await Task.Run(() =>
            {
                using var disk = _diskManager.Open(device.Info, DiskAccessMode.Read);
                var length = (int)Math.Min(ImageInspector.HeadSize, StreamExtensions.AlignDown(disk.Length, disk.SectorSize));
                var head = disk.ReadBytes(0, length) ?? [];
                return ImageInspector.Inspect(head, (o, c) => disk.ReadBytes(o, c));
            });

            if (request == _layoutRequest)
            {
                _deviceLayout = layout;
            }
        }
        catch (Exception ex)
        {
            if (request == _layoutRequest)
            {
                _layoutError = Formatting.Error(ex);
            }
        }

        if (request == _layoutRequest)
        {
            OnPropertyChanged(nameof(LayoutText));
            OnPropertyChanged(nameof(WillReadText));
        }
    }

    [RelayCommand]
    private void SaveAs()
    {
        var extension = GetExtension(SelectedFormat.Value);
        var path = _dialogs.PickSaveFile(
            string.IsNullOrWhiteSpace(DestinationPath) && SelectedDevice != null ? SuggestPath(SelectedDevice) : DestinationPath,
            SelectedFormat.Text,
            extension);
        if (path != null)
        {
            // The format follows the chosen extension, if it names one.
            var handler = _registry.FromFileName(path);
            var option = Formats.FirstOrDefault(f => f.Value == handler.Format);
            if (option != null && handler.Format != ContainerFormat.Raw)
            {
                SelectedFormat = option;
            }

            DestinationPath = ApplyExtension(path, SelectedFormat.Value);
        }
    }

    /// <summary>How much free space was skipped, and which file systems were copied in full and why.</summary>
    private static string FreeSpaceSummary(ReadImageResult result)
    {
        var text = string.Empty;
        if (result.SkippedBytes > 0)
        {
            text += Environment.NewLine + Loc.Format("Read_FreeBlocksSkipped", Formatting.Size(result.SkippedBytes));
        }

        foreach (var fs in result.FreeSpace.Where(f => f.Status != FreeSpaceStatus.Skipped))
        {
            var where = fs.PartitionNumber > 0 ? Loc.Format("Read_FreeBlocksPartition", fs.PartitionNumber) : Loc.Get("Read_FreeBlocksWholeDevice");
            text += Environment.NewLine + Loc.Format("Read_FreeBlocksNotSkipped", where, Loc.Get($"FreeSpace_{fs.Status}"));
        }

        return text;
    }

    private bool CanStart()
        => SelectedDevice is { CanBeRead: true }
           && !string.IsNullOrWhiteSpace(DestinationPath)
           && !Operation.IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var device = SelectedDevice!;
        var path = Path.GetFullPath(DestinationPath.Trim().Trim('"'));
        if (File.Exists(path)
            && !await _dialogs.ConfirmAsync(Loc.Get("Read_OverwriteTitle"), Loc.Format("Read_OverwriteMessage", path), Loc.Get("Replace")))
        {
            return;
        }

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
            _settings.Settings.LastSaveFolder = folder;
            _settings.Save();
        }

        var options = new ReadImageOptions
        {
            Verify = Verify,
            WriteChecksumFile = CreateChecksumFile,
            OnlyAllocatedPartitions = OnlyAllocatedPartitions,
            SkipFreeBlocks = SkipFreeBlocks,
            CreateBmapFile = CreateBmap,
            Format = SelectedFormat.Value,
            Compression = new CompressionSettings(SelectedCompression.Value),
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

            var result = await ReadImageOperation.RunAsync(
                () => _diskManager.Open(device.Info, DiskAccessMode.Read),
                path,
                _registry,
                options,
                progress,
                token);

            var details = Loc.Format("Read_SuccessDetails", Formatting.Size(result.BytesRead), Formatting.Duration(result.Elapsed), Formatting.Size(result.FileSize));
            if (result.Verified)
            {
                details += Environment.NewLine + Loc.Get("Write_Verified");
            }

            details += FreeSpaceSummary(result);

            var severity = InfoBarSeverity.Success;
            if (result.ChecksumFilePath is { } checksumFile)
            {
                details += Environment.NewLine + Loc.Format("Read_ChecksumFileSaved", Path.GetFileName(checksumFile));
            }
            else if (result.ChecksumFileError is { } checksumError)
            {
                // The image itself is fine, so this is a warning rather than a failure.
                details += Environment.NewLine + Loc.Format("Read_ChecksumFileFailed", checksumError);
                severity = InfoBarSeverity.Warning;
            }

            if (result.BmapFilePath is { } bmapFile)
            {
                details += Environment.NewLine + Loc.Format("Read_BmapSaved", Path.GetFileName(bmapFile));
            }
            else if (result.BmapFileError is { } bmapError)
            {
                details += Environment.NewLine + Loc.Format("Read_BmapFailed", bmapError);
                severity = InfoBarSeverity.Warning;
            }

            Result.Show(severity, Loc.Get("Read_Success"), details + Environment.NewLine + result.Path);
        }
        catch (OperationCanceledException)
        {
            Result.Show(InfoBarSeverity.Warning, Loc.Get("Cancelled"), string.Empty);
        }
        catch (Exception ex)
        {
            failed = true;
            Result.Show(InfoBarSeverity.Error, Loc.Get("Read_Failed"), Formatting.Error(ex));
        }
        finally
        {
            Progress.Stop();
            Operation.End(failed);
        }
    }

    [RelayCommand]
    private void Cancel() => Operation.Cancel();

    private string SuggestPath(DeviceItemViewModel device)
    {
        var folder = _settings.Settings.LastSaveFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var name = string.Concat(device.Name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        return ApplyExtension(Path.Combine(folder, $"{name}_{DateTime.Now:yyyyMMdd-HHmm}.img"), SelectedFormat.Value);
    }

    private string GetExtension(ContainerFormat format)
        => format == ContainerFormat.Raw ? ".img" : _registry.Get(format).Extensions[0];

    /// <summary>"x.img.zip" + xz -> "x.img.xz"; "x.img.xz" + raw -> "x.img".</summary>
    private string ApplyExtension(string path, ContainerFormat format)
    {
        var current = _registry.FromFileName(path);
        if (current.Format != ContainerFormat.Raw)
        {
            path = path[..^Path.GetExtension(path).Length];
        }

        if (string.IsNullOrEmpty(Path.GetExtension(path)))
        {
            path += ".img";
        }

        return format == ContainerFormat.Raw ? path : path + GetExtension(format);
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            StartCommand.NotifyCanExecuteChanged();
        }
    }
}
