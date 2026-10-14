using System.Windows;
using System.Windows.Controls;
using FlashTool.App.ViewModels;

namespace FlashTool.App.Views.Controls;

/// <summary>Image picker card (path/URL box, archive entries, partition preview).</summary>
public partial class ImageSourcePanel : UserControl
{
    public ImageSourcePanel() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is ImageSourceViewModel viewModel
            && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            && viewModel.LoadSourceCommand.CanExecute(null))
        {
            viewModel.SourceText = files[0];
            await viewModel.LoadSourceAsync();
        }
    }
}
