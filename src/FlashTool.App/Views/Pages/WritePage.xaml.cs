using System.Windows.Controls;
using FlashTool.App.ViewModels;

namespace FlashTool.App.Views.Pages;

public partial class WritePage : Page
{
    public WritePage(WritePageViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
