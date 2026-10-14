using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace FlashTool.App.Localization;

/// <summary>
/// Localized strings with live switching: XAML binds to the indexer
/// (<c>{loc:Tr Key}</c>) and refreshes when the culture changes.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("FlashTool.App.Localization.Strings", typeof(Loc).Assembly);

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the UI culture has changed.</summary>
    public event EventHandler? CultureChanged;

    public static Loc Instance { get; } = new();

    /// <summary>The Windows display language captured at startup.</summary>
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    public string this[string key] => Resources.GetString(key, Culture) ?? $"[{key}]";

    public static string Get(string key) => Instance[key];

    public static string Format(string key, params object?[] args) => string.Format(Instance.Culture, Instance[key], args);

    public void SetCulture(CultureInfo culture)
    {
        Culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }
}
