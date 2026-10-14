using System.Windows;
using System.Windows.Controls;
using FlashTool.App.ViewModels;

namespace FlashTool.App.Views.Pages;

public partial class WritePage : Page
{
    private readonly WritePageViewModel _viewModel;

    public WritePage(WritePageViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && _viewModel.LoadSourceCommand.CanExecute(null))
        {
            _viewModel.SourceText = files[0];
            await _viewModel.LoadSourceAsync();
        }
    }
}
