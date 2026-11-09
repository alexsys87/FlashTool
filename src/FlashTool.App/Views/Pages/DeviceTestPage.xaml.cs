using System.IO;
using System.Windows.Controls;
using FlashTool.App.ViewModels;

namespace FlashTool.App.Views.Pages;

public partial class DeviceTestPage : Page
{
    public DeviceTestPage(DeviceTestPageViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // The picture is made from the chart on screen, which only the view has.
        viewModel.PngExportRequested += (_, path) =>
        {
            try
            {
                Chart.SavePng(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                viewModel.ReportExportError(ex.Message);
            }
        };
    }
}
