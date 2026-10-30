using System.Windows.Controls;
using FlashTool.App.ViewModels;

namespace FlashTool.App.Views.Pages;

public partial class ErasePage : Page
{
    public ErasePage(ErasePageViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
