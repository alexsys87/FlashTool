using System.Windows.Controls;
using FlashTool.App.ViewModels;

namespace FlashTool.App.Views.Pages;

public partial class VerifyPage : Page
{
    public VerifyPage(VerifyPageViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
