using CommunityToolkit.Mvvm.ComponentModel;

namespace FlashTool.App.Services;

/// <summary>
/// Whether the "Device test" page is available. Like the "Erase" page it is an opt-in in the settings,
/// off by default: most of its tests destroy everything on the device.
/// </summary>
public sealed class DeviceTestFeature : ObservableObject
{
    private readonly SettingsService _settings;

    public DeviceTestFeature(SettingsService settings) => _settings = settings;

    public bool IsEnabled
    {
        get => _settings.Settings.DeviceTestEnabled;
        set
        {
            if (_settings.Settings.DeviceTestEnabled == value)
            {
                return;
            }

            _settings.Settings.DeviceTestEnabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }
}
