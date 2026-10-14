using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core;
using FlashTool.Core.Containers;
using FlashTool.Core.Devices;
using FlashTool.Core.FileSystems.Ext4;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Partitioning;
using FlashTool.Core.Sources;
using FlashTool.Platform.Windows;
using Wpf.Ui.Controls;

namespace FlashTool.App.ViewModels;

/// <summary>"Write image" page: pick an image (file, archive or URL) and a device.</summary>
public sealed partial class WritePageViewModel : LocalizedViewModel
{
    private const int MaxEntriesToProbe = 32;

    private readonly ContainerRegistry _registry;
    private readonly IDiskManager _diskManager;
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private IImageContainer? _container;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(LoadSourceCommand), nameof(BrowseCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _sourceError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource), nameof(FormatText), nameof(IsRemote), nameof(EntriesSummary), nameof(HasSeveralEntries))]
    private IStreamSource? _source;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    [NotifyPropertyChangedFor(nameof(SizeWarning))]
    private ImageEntryViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    [NotifyPropertyChangedFor(nameof(SizeWarning))]
    private DeviceItemViewModel? _selectedDevice;

    public WritePageViewModel(
        ContainerRegistry registry,
        IDiskManager diskManager,
        HttpClient http,
        SettingsService settings,
        DialogService dialogs,
        DeviceListService devices,
        OperationState operation)
    {
        _registry = registry;
        _diskManager = diskManager;
        _http = http;
        _settings = settings;
        _dialogs = dialogs;
        Devices = devices;
        Operation = operation;
        Operation.PropertyChanged += OnOperationChanged;
    }

    public DeviceListService Devices { get; }

    public OperationState Operation { get; }

    public ProgressViewModel Progress { get; } = new();

    public ResultViewModel Result { get; } = new();

    public ObservableCollection<ImageEntryViewModel> Entries { get; } = [];

    public bool HasSource => Source != null;

    public bool IsRemote => Source?.IsRemote == true;

    public bool HasSeveralEntries => Entries.Count > 1;

    public string FormatText => _container is { } c ? c.Format.ToString().ToUpperInvariant() : string.Empty;

    public string EntriesSummary => Loc.Format("Image_EntriesSummary", Entries.Count, Entries.Count(e => e.IsDiskImage));

    public string? SizeWarning
        => SelectedEntry?.Entry.Length is { } length && SelectedDevice is { HasMedia: true } device && length > device.Info.Size
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

    private bool CanChangeSource => !IsLoading && !Operation.IsBusy;

    [RelayCommand(CanExecute = nameof(CanChangeSource))]
    private async Task BrowseAsync()
    {
        var path = _dialogs.PickImageFile(_settings.Settings.LastImageFolder);
        if (path != null)
        {
            SourceText = path;
            await LoadSourceAsync();
        }
    }

    /// <summary>Opens the file/URL in <see cref="SourceText"/>, lists entries and inspects them.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSource))]
    public async Task LoadSourceAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        CloseSource();
        SourceError = null;
        Result.Hide();

        var input = SourceText.Trim().Trim('"');
        if (input.Length == 0)
        {
            return;
        }

        IsLoading = true;
        try
        {
            IStreamSource source;
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                source = await HttpStreamSource.CreateAsync(_http, uri, cts.Token);
            }
            else if (File.Exists(input))
            {
                source = new FileStreamSource(input);
                _settings.Settings.LastImageFolder = Path.GetDirectoryName(source.Location);
                _settings.Save();
            }
            else
            {
                SourceError = Path.IsPathRooted(input) ? Loc.Format("Error_FileNotFound", input) : Loc.Get("Error_InvalidSource");
                return;
            }

            var container = await Task.Run(() => _registry.Open(source, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
            {
                container.Dispose();
                return;
            }

            if (container.Entries.Count == 0)
            {
                container.Dispose();
                throw new FlashToolException(FlashErrorCode.ArchiveHasNoImages);
            }

            _container = container;
            foreach (var entry in container.Entries)
            {
                Entries.Add(new ImageEntryViewModel(entry));
            }

            Source = source;
            OnPropertyChanged(nameof(HasSeveralEntries));

            await ProbeEntriesAsync(container, cts.Token);
            SelectedEntry = Entries.FirstOrDefault(e => e.IsDiskImage) ?? Entries.FirstOrDefault(e => ContainerNaming.HasImageExtension(e.Name)) ?? Entries[0];
            OnPropertyChanged(nameof(EntriesSummary));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CloseSource();
            SourceError = Formatting.Error(ex);
        }
        finally
        {
            if (_loadCts == cts)
            {
                IsLoading = false;
            }
        }
    }

    private async Task ProbeEntriesAsync(IImageContainer container, CancellationToken ct)
    {
        // Inspect plausible candidates only: archives may contain docs, checksums, etc.
        var candidates = Entries
            .Where(e => Entries.Count == 1 || ContainerNaming.HasImageExtension(e.Name) || (!container.Source.IsRemote && e.Entry.Length is null or >= 1024 * 1024))
            .Take(MaxEntriesToProbe)
            .ToList();

        foreach (var item in candidates)
        {
            item.IsChecking = true;
            try
            {
                item.Info = await Task.Run(() => Probe(container, item.Entry, ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Info = ImageInfo.Unknown;
                if (Entries.Count == 1)
                {
                    throw;
                }
            }
            finally
            {
                item.IsChecking = false;
            }
        }

        foreach (var item in Entries.Where(e => e.Info == null))
        {
            item.Info = ImageInfo.Unknown;
        }
    }

    private static ImageInfo Probe(IImageContainer container, ImageEntry entry, CancellationToken ct)
    {
        byte[] head;
        using (var reader = container.OpenEntry(entry, ct))
        {
            head = new byte[ImageInspector.HeadSize];
            Array.Resize(ref head, reader.Data.ReadUpTo(head));
        }

        // A local uncompressed image allows random access: file systems of all partitions can be detected.
        if (container.Format == ContainerFormat.Raw && !container.Source.IsRemote)
        {
            using var file = container.Source.Open(ct);
            return ImageInspector.Inspect(head, (offset, count) =>
            {
                if (offset < 0 || offset + count > file.Length)
                {
                    return null;
                }

                var buffer = new byte[count];
                file.Position = offset;
                file.ReadExactly(buffer);
                return buffer;
            });
        }

        return ImageInspector.Inspect(head);
    }

    private bool CanWrite()
        => SelectedEntry is { IsChecking: false }
           && SelectedDevice is { CanBeWritten: true }
           && SizeWarning == null
           && !IsLoading
           && !Operation.IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        var entry = SelectedEntry!;
        var device = SelectedDevice!;
        var container = _container!;

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

    private void CloseSource()
    {
        SelectedEntry = null;
        Entries.Clear();
        _container?.Dispose();
        _container = null;
        Source = null;
        OnPropertyChanged(nameof(HasSeveralEntries));
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            WriteCommand.NotifyCanExecuteChanged();
            LoadSourceCommand.NotifyCanExecuteChanged();
            BrowseCommand.NotifyCanExecuteChanged();
        }
    }
}
