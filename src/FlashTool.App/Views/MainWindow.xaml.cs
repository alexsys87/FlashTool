using System.ComponentModel;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.App.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FlashTool.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly OperationState _operation;
    private readonly DialogService _dialogs;
    private bool _closeConfirmed;

    public MainWindow(IServiceProvider services, OperationState operation, IContentDialogService contentDialogs, DialogService dialogs)
    {
        _operation = operation;
        _dialogs = dialogs;
        DataContext = operation;
        InitializeComponent();

        contentDialogs.SetDialogHost(RootContentDialogHost);
        RootNavigation.SetServiceProvider(services);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(WritePage));
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_operation.IsBusy || _closeConfirmed)
        {
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
}
