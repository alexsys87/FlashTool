using System.ComponentModel;
using System.Windows;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.App.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FlashTool.App.Views;

public partial class MainWindow : FluentWindow
{
    private static readonly Type[] Pages = [typeof(WritePage), typeof(ReadPage), typeof(VerifyPage), typeof(SettingsPage)];

    private readonly OperationState _operation;
    private readonly DialogService _dialogs;
    private readonly SettingsService _settings;
    private bool _closeConfirmed;

    public MainWindow(IServiceProvider services, OperationState operation, IContentDialogService contentDialogs, DialogService dialogs, SettingsService settings)
    {
        _operation = operation;
        _dialogs = dialogs;
        _settings = settings;
        DataContext = operation;
        InitializeComponent();

        contentDialogs.SetDialogHost(RootContentDialogHost);
        RootNavigation.SetServiceProvider(services);
        RestorePlacement();

        Loaded += (_, _) =>
        {
            RootNavigation.IsPaneOpen = _settings.Settings.NavigationPaneOpen;
            RootNavigation.Navigate(Pages.FirstOrDefault(p => p.Name == _settings.Settings.LastPage) ?? typeof(WritePage));
            RootNavigation.Navigated += (_, e) =>
            {
                _settings.Settings.LastPage = e.Page.GetType().Name;
                _settings.Save();
            };
            RootNavigation.PaneOpened += (_, _) => SavePaneState(true);
            RootNavigation.PaneClosed += (_, _) => SavePaneState(false);
        };
    }

    /// <summary>Opens a specific page (used when an image is passed on the command line).</summary>
    public void ShowPage(Type page) => RootNavigation.Navigate(page);

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_operation.IsBusy || _closeConfirmed)
        {
            SavePlacement();
            return;
        }

        // Never leave a half-written device silently: ask, cancel, wait, then close.
        e.Cancel = true;
        if (await _dialogs.ConfirmAsync(Loc.Get("Exit_BusyTitle"), Loc.Get("Exit_BusyMessage"), Loc.Get("Exit_Confirm")))
        {
            await _operation.CancelAndWaitAsync();
            _closeConfirmed = true;
            Close();
        }
    }

    private void SavePaneState(bool open)
    {
        _settings.Settings.NavigationPaneOpen = open;
        _settings.Save();
    }

    /// <summary>Applies the saved size/position if it is still (mostly) on a connected screen.</summary>
    private void RestorePlacement()
    {
        if (_settings.Settings.Window is not { } p || p.Width < MinWidth || p.Height < MinHeight)
        {
            return;
        }

        var bounds = new Rect(p.Left, p.Top, p.Width, p.Height);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(bounds, screen);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 100)
        {
            // The monitor it was on is gone: keep the default centered placement but the size.
            Width = Math.Min(p.Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(p.Height, SystemParameters.WorkArea.Height);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
            Width = p.Width;
            Height = p.Height;
        }

        if (p.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        // RestoreBounds holds the normal-state bounds while maximized or minimized.
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Width))
        {
            return;
        }

        _settings.Settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        _settings.Save();
    }
}
