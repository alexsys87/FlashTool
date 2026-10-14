using System.Windows.Controls;
using FlashTool.App.ViewModels;

namespace FlashTool.App.Views.Pages;

public partial class ReadPage : Page
{
    public ReadPage(ReadPageViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
