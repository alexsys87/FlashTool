using System.ComponentModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Bmap;
using FlashTool.Core.Sources;

namespace FlashTool.App.ViewModels;

/// <summary>
/// The .bmap of the image on a page ("Write image", "Verify"): looked for next to the image when the
/// feature is on in the settings, shown with what it covers, and used unless the user turns it off.
/// </summary>
public sealed partial class BmapSelectionViewModel : LocalizedViewModel
{
    private readonly ImageSourceViewModel _image;
    private readonly BmapFeature _feature;
    private readonly HttpClient _http;
    private CancellationTokenSource? _search;
    private IStreamSource? _searchedFor;
    private bool _searching;
    private BmapLookup _lookup = BmapLookup.NotFound;

    /// <summary>The switch on the page; a new image starts with it on, as bmaptool uses a .bmap it finds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Effective))]
    private bool _useFound = true;

    public BmapSelectionViewModel(ImageSourceViewModel image, BmapFeature feature, HttpClient http)
    {
        _image = image;
        _feature = feature;
        _http = http;
        image.Loading += (_, _) => Reset();
        image.PropertyChanged += OnImageChanged;
        feature.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BmapFeature.UseBmap))
            {
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(Effective));
                StartSearchIfNeeded();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Raised when <see cref="Effective"/> may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The feature is on in the settings: the card is shown.</summary>
    public bool IsAvailable => _feature.UseBmap;

    /// <summary>A usable .bmap that matches the selected image.</summary>
    public bool CanUse => _lookup.Bmap is { } bmap && !SizeDiffers(bmap);

    /// <summary>What the operation uses: null writes and compares the whole image.</summary>
    public BmapFile? Effective => IsAvailable && UseFound && CanUse ? _lookup.Bmap : null;

    public string StatusText
    {
        get
        {
            if (_searching)
            {
                return Loc.Get("Bmap_Searching");
            }

            if (!_lookup.Found)
            {
                return _searchedFor != null ? Loc.Get("Bmap_NotFound") : string.Empty;
            }

            var name = _lookup.Origin!;
            if (_lookup.Bmap is not { } bmap)
            {
                return Loc.Format("Bmap_Unusable", name, Loc.Get($"BmapProblem_{_lookup.Problem}"));
            }

            if (SizeDiffers(bmap))
            {
                return Loc.Format("Bmap_SizeDiffers", name, Formatting.Size(bmap.ImageSize), Formatting.Size(_image.SelectedEntry!.Entry.Length));
            }

            return Loc.Format("Bmap_Found", name, Formatting.Size(bmap.MappedBytes), Formatting.Size(bmap.ImageSize));
        }
    }

    partial void OnUseFoundChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    private bool SizeDiffers(BmapFile bmap) => _image.SelectedEntry?.Entry.Length is { } length && length != bmap.ImageSize;

    private void OnImageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageSourceViewModel.SelectedEntry))
        {
            Refresh();
        }

        if (e.PropertyName == nameof(ImageSourceViewModel.IsLoading))
        {
            StartSearchIfNeeded();
        }
    }

    private void Reset()
    {
        _search?.Cancel();
        _searchedFor = null;
        _searching = false;
        _lookup = BmapLookup.NotFound;
        UseFound = true;
        Refresh();
    }

    private void StartSearchIfNeeded()
    {
        if (!IsAvailable || _image.IsLoading || _image.Container is not { } container || ReferenceEquals(container.Source, _searchedFor))
        {
            return;
        }

        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        _searchedFor = container.Source;
        _searching = true;
        Refresh();
        _ = SearchAsync(container.Source, cts);
    }

    private async Task SearchAsync(IStreamSource source, CancellationTokenSource cts)
    {
        var found = BmapLookup.NotFound;
        try
        {
            if (!source.IsRemote)
            {
                found = await Task.Run(() => BmapDiscovery.FindForFile(source.Location), cts.Token);
            }
            else if (Uri.TryCreate(source.Location, UriKind.Absolute, out var uri))
            {
                found = await BmapDiscovery.FindForUrlAsync(_http, uri, cts.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Looking for a .bmap is a convenience: a failure, or a newer image that cancelled this search,
            // just means nothing was found.
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        _lookup = found;
        _searching = false;
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(Effective));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
