using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Containers;

namespace FlashTool.App.ViewModels;

public sealed partial class SettingsPageViewModel : LocalizedViewModel
{
    private readonly SettingsService _settings;
    private readonly AppearanceService _appearance;
    private readonly ContainerRegistry _registry;
    private readonly EraseFeature _erase;
    private readonly DialogService _dialogs;
    private readonly OperationState _operation;

    [ObservableProperty]
    private LocalizedOption<ThemeMode> _selectedTheme;

    [ObservableProperty]
    private LocalizedOption<LanguageMode> _selectedLanguage;

    public SettingsPageViewModel(
        SettingsService settings,
        AppearanceService appearance,
        ContainerRegistry registry,
        DeviceListService devices,
        EraseFeature erase,
        DialogService dialogs,
        OperationState operation)
    {
        _settings = settings;
        _appearance = appearance;
        _registry = registry;
        _erase = erase;
        _dialogs = dialogs;
        _operation = operation;
        Devices = devices;
        operation.PropertyChanged += OnOperationChanged;

        Themes = Enum.GetValues<ThemeMode>().Select(t => new LocalizedOption<ThemeMode>(t, $"Theme_{t}")).ToList();
        Languages =
        [
            new LocalizedOption<LanguageMode>(LanguageMode.System, "Language_System"),
            new LocalizedOption<LanguageMode>(LanguageMode.English, "Language_English"),
            new LocalizedOption<LanguageMode>(LanguageMode.Russian, "Language_Russian"),
        ];

        _selectedTheme = Themes.First(t => t.Value == settings.Settings.Theme);
        _selectedLanguage = Languages.First(l => l.Value == settings.Settings.Language);
    }

    public DeviceListService Devices { get; }

    public IReadOnlyList<LocalizedOption<ThemeMode>> Themes { get; }

    public IReadOnlyList<LocalizedOption<LanguageMode>> Languages { get; }

    /// <summary>State of the switch; it is changed only through <see cref="ToggleEraseCommand"/>.</summary>
    public bool EraseEnabled => _erase.IsEnabled;

    public string VersionText => Loc.Format(
        "Settings_Version",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?",
        RuntimeInformation.FrameworkDescription.Replace(".NET ", string.Empty, StringComparison.Ordinal));

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string AdminText => Loc.Get(IsAdministrator ? "Settings_Admin" : "Settings_NotAdmin");

    public string FormatsText => Loc.Format(
        "Settings_Formats",
        string.Join(", ", _registry.Handlers.Where(h => h.IsAvailable).Select(h => h.Format == ContainerFormat.Raw ? "img/iso" : h.Extensions[0].TrimStart('.'))));

    private bool CanToggleErase() => !_operation.IsBusy;

    /// <summary>Turning the "Erase" page on needs a confirmation; turning it off does not.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleErase))]
    private async Task ToggleEraseAsync()
    {
        _erase.IsEnabled = !_erase.IsEnabled
            && await _dialogs.ConfirmAsync(Loc.Get("Settings_EraseConfirmTitle"), Loc.Get("Settings_EraseConfirmMessage"), Loc.Get("Settings_EraseConfirmButton"));

        // The switch has already flipped itself; bring it back in line when the dialog was cancelled.
        OnPropertyChanged(nameof(EraseEnabled));
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            ToggleEraseCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedThemeChanged(LocalizedOption<ThemeMode> value)
    {
        _settings.Settings.Theme = value.Value;
        _settings.Save();
        _appearance.ApplyTheme(value.Value);
    }

    partial void OnSelectedLanguageChanged(LocalizedOption<LanguageMode> value)
    {
        _settings.Settings.Language = value.Value;
        _settings.Save();
        AppearanceService.ApplyLanguage(value.Value);
    }
}
