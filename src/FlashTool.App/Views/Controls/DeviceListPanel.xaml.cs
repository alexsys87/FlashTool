using System.Windows;
using System.Windows.Controls;

namespace FlashTool.App.Views.Controls;

/// <summary>Device picker shared by the write and read pages.</summary>
public partial class DeviceListPanel : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(DeviceListPanel), new PropertyMetadata(string.Empty));

    /// <summary>When true, devices that cannot be written (system disk, write protected) are disabled.</summary>
    public static readonly DependencyProperty RequireWritableProperty =
        DependencyProperty.Register(nameof(RequireWritable), typeof(bool), typeof(DeviceListPanel), new PropertyMetadata(false));

    public DeviceListPanel() => InitializeComponent();

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool RequireWritable
    {
        get => (bool)GetValue(RequireWritableProperty);
        set => SetValue(RequireWritableProperty, value);
    }
}
