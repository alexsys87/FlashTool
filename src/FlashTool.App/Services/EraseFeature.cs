using CommunityToolkit.Mvvm.ComponentModel;

namespace FlashTool.App.Services;

/// <summary>
/// Whether the "Erase" page is available. It is an explicit opt-in in the settings,
/// so a device cannot be wiped by someone who only wanted to write an image.
/// </summary>
public sealed class EraseFeature : ObservableObject
{
    private readonly SettingsService _settings;

    public EraseFeature(SettingsService settings) => _settings = settings;

    public bool IsEnabled
    {
        get => _settings.Settings.EraseEnabled;
        set
        {
            if (_settings.Settings.EraseEnabled == value)
            {
                return;
            }

            _settings.Settings.EraseEnabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }
}
