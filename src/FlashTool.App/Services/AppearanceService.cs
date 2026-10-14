using System.Globalization;
using System.Windows;
using FlashTool.App.Localization;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FlashTool.App.Services;

/// <summary>
/// Applies theme and language. In "System" mode the theme follows Windows
/// live, and the language follows the Windows display language.
/// </summary>
public sealed class AppearanceService
{
    private Window? _window;
    private bool _watching;

    public void Attach(Window window) => _window = window;

    public void ApplyTheme(ThemeMode mode)
    {
        // UnWatch throws for a window that is not loaded yet, and is only needed after Watch.
        if (_window != null && _watching && _window.IsLoaded)
        {
            SystemThemeWatcher.UnWatch(_window);
            _watching = false;
        }

        switch (mode)
        {
            case ThemeMode.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica);
                break;
            case ThemeMode.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
                if (_window != null && !_watching)
                {
                    // Watch() itself defers the hook until the window is loaded.
                    SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica, updateAccents: true);
                    _watching = true;
                }

                break;
        }
    }

    public static void ApplyLanguage(LanguageMode mode)
    {
        var culture = mode switch
        {
            LanguageMode.English => CultureInfo.GetCultureInfo("en"),
            LanguageMode.Russian => CultureInfo.GetCultureInfo("ru"),
            _ => ResolveSystemLanguage(),
        };

        Loc.Instance.SetCulture(culture);
    }

    /// <summary>Russian for Russian-language systems, English otherwise.</summary>
    private static CultureInfo ResolveSystemLanguage()
        => Loc.SystemCulture.TwoLetterISOLanguageName == "ru"
            ? CultureInfo.GetCultureInfo("ru")
            : CultureInfo.GetCultureInfo("en");
}
