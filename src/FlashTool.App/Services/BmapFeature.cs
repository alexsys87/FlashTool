using CommunityToolkit.Mvvm.ComponentModel;

namespace FlashTool.App.Services;

/// <summary>
/// The .bmap options from the settings, both off by default: using a .bmap found next to an image when
/// writing and verifying, and saving one next to every created image.
/// </summary>
public sealed class BmapFeature : ObservableObject
{
    private readonly SettingsService _settings;

    public BmapFeature(SettingsService settings) => _settings = settings;

    public bool UseBmap
    {
        get => _settings.Settings.UseBmap;
        set
        {
            if (_settings.Settings.UseBmap != value)
            {
                _settings.Settings.UseBmap = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool CreateBmap
    {
        get => _settings.Settings.CreateBmap;
        set
        {
            if (_settings.Settings.CreateBmap != value)
            {
                _settings.Settings.CreateBmap = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }
}
