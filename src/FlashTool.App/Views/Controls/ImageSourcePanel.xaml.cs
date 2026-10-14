using System.Windows.Controls;

namespace FlashTool.App.Views.Controls;

/// <summary>
/// Image picker card (path/URL box, archive entries, partition preview).
/// Files dropped onto the window are handled by <see cref="MainWindow"/>.
/// </summary>
public partial class ImageSourcePanel : UserControl
{
    public ImageSourcePanel() => InitializeComponent();
}
