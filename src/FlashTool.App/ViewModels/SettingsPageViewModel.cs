using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Containers;

namespace FlashTool.App.ViewModels;

public sealed partial class SettingsPageViewModel : LocalizedViewModel
{
    private readonly SettingsService _settings;
    private readonly AppearanceService _appearance;
    private readonly ContainerRegistry _registry;

    [ObservableProperty]
    private LocalizedOption<ThemeMode> _selectedTheme;

    [ObservableProperty]
    private LocalizedOption<LanguageMode> _selectedLanguage;

    public SettingsPageViewModel(SettingsService settings, AppearanceService appearance, ContainerRegistry registry, DeviceListService devices)
    {
        _settings = settings;
        _appearance = appearance;
        _registry = registry;
        Devices = devices;

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
