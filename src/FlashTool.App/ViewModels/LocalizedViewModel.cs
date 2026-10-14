using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using FlashTool.App.Localization;

namespace FlashTool.App.ViewModels;

/// <summary>
/// Base for view models exposing computed, localized text: all bindings are
/// refreshed when the language changes.
/// </summary>
public abstract class LocalizedViewModel : ObservableObject
{
    protected LocalizedViewModel()
    {
        // Weak subscription: short-lived items (archive entries) must not be kept alive by the singleton.
        WeakEventManager<Loc, EventArgs>.AddHandler(Loc.Instance, nameof(Loc.CultureChanged), OnCultureChangedHandler);
    }

    private void OnCultureChangedHandler(object? sender, EventArgs e) => OnCultureChanged();

    protected virtual void OnCultureChanged() => OnPropertyChanged(string.Empty);
}

/// <summary>An option in a combo box whose caption is a localization key.</summary>
public sealed class LocalizedOption<T> : LocalizedViewModel
{
    private readonly string _key;

    public LocalizedOption(T value, string key)
    {
        Value = value;
        _key = key;
    }

    public T Value { get; }

    public string Text => Loc.Get(_key);

    public override string ToString() => Text;
}
