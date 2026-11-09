using CommunityToolkit.Mvvm.ComponentModel;

namespace FlashTool.App.Services;

/// <summary>
/// Experimental differential writing: compare with what the device already holds and write only the
/// blocks that differ. Off by default; it costs a full read of the device in exchange.
/// </summary>
public sealed class DifferentialWriteFeature : ObservableObject
{
    private readonly SettingsService _settings;

    public DifferentialWriteFeature(SettingsService settings) => _settings = settings;

    public bool IsEnabled
    {
        get => _settings.Settings.DifferentialWrite;
        set
        {
            if (_settings.Settings.DifferentialWrite == value)
            {
                return;
            }

            _settings.Settings.DifferentialWrite = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }
}
