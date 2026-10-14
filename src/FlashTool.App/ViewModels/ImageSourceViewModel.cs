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
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Sources;

namespace FlashTool.App.ViewModels;

/// <summary>
/// Image picker shared by the write and verify pages: opens a file, archive or
/// URL, lists the files inside and inspects which of them are disk images.
/// Each page owns its own instance.
/// </summary>
public sealed partial class ImageSourceViewModel : LocalizedViewModel
{
    private const int MaxEntriesToProbe = 32;

    private readonly ContainerRegistry _registry;
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly OperationState _operation;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSourceCommand), nameof(BrowseCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _sourceError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource), nameof(FormatText), nameof(IsRemote), nameof(EntriesSummary), nameof(HasSeveralEntries))]
    private IStreamSource? _source;

    [ObservableProperty]
    private ImageEntryViewModel? _selectedEntry;

    public ImageSourceViewModel(ContainerRegistry registry, HttpClient http, SettingsService settings, DialogService dialogs, OperationState operation)
    {
        _registry = registry;
        _http = http;
        _settings = settings;
        _dialogs = dialogs;
        _operation = operation;
        _operation.PropertyChanged += OnOperationChanged;
    }

    /// <summary>Raised when a new image starts loading (pages hide their last result).</summary>
    public event EventHandler? Loading;

    /// <summary>Raised when an image was opened or another file inside it was chosen (to persist the choice).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The path/URL of the currently opened image.</summary>
    public string? LoadedSource { get; private set; }

    /// <summary>The opened container, null while nothing is loaded.</summary>
    public IImageContainer? Container { get; private set; }

    public ObservableCollection<ImageEntryViewModel> Entries { get; } = [];

    public bool HasSource => Source != null;

    public bool IsRemote => Source?.IsRemote == true;

    public bool HasSeveralEntries => Entries.Count > 1;

    public string FormatText => Container is { } c ? c.Format.ToString().ToUpperInvariant() : string.Empty;

    public string EntriesSummary => Loc.Format("Image_EntriesSummary", Entries.Count, Entries.Count(e => e.IsDiskImage));

    private bool CanChangeSource => !IsLoading && !_operation.IsBusy;

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
        Loading?.Invoke(this, EventArgs.Empty);

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

            Container = container;
            foreach (var entry in container.Entries)
            {
                Entries.Add(new ImageEntryViewModel(entry));
            }

            Source = source;
            OnPropertyChanged(nameof(HasSeveralEntries));

            await ProbeEntriesAsync(container, cts.Token);
            LoadedSource = input;
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

    /// <summary>
    /// Restores a previously used image. Local files are opened again; URLs are
    /// only put into the box so nothing is downloaded without the user asking.
    /// </summary>
    public async Task RestoreAsync(string? source, string? entryName)
    {
        if (string.IsNullOrWhiteSpace(source) || HasSource || IsLoading)
        {
            return;
        }

        SourceText = source;
        if (!File.Exists(source))
        {
            return;
        }

        await LoadSourceAsync();
        var entry = Entries.FirstOrDefault(e => e.Name == entryName);
        if (entry != null)
        {
            SelectedEntry = entry;
        }
    }

    partial void OnSelectedEntryChanged(ImageEntryViewModel? value)
    {
        if (value != null && LoadedSource != null)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CloseSource()
    {
        LoadedSource = null;
        SelectedEntry = null;
        Entries.Clear();
        Container?.Dispose();
        Container = null;
        Source = null;
        OnPropertyChanged(nameof(HasSeveralEntries));
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            LoadSourceCommand.NotifyCanExecuteChanged();
            BrowseCommand.NotifyCanExecuteChanged();
        }
    }
}
