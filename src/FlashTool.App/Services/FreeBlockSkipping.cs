using CommunityToolkit.Mvvm.ComponentModel;

namespace FlashTool.App.Services;

/// <summary>
/// Whether creating an image stores the free blocks of ext2/3/4 file systems as zeros
/// instead of reading them. The result is not a byte-for-byte copy of the device, so it is
/// an explicit opt-in in the settings and off by default.
/// </summary>
public sealed class FreeBlockSkipping : ObservableObject
{
    private readonly SettingsService _settings;

    public FreeBlockSkipping(SettingsService settings) => _settings = settings;

    public bool IsEnabled
    {
        get => _settings.Settings.SkipFreeBlocks;
        set
        {
            if (_settings.Settings.SkipFreeBlocks == value)
            {
                return;
            }

            _settings.Settings.SkipFreeBlocks = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }
}
