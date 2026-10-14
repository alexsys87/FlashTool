using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FlashTool.App.Services;

/// <summary>
/// Listens for WM_DEVICECHANGE (USB sticks and cards being inserted or
/// removed) and raises a debounced <see cref="DevicesChanged"/> event.
/// </summary>
public sealed class DeviceChangeMonitor
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevNodesChanged = 0x0007;

    private readonly DispatcherTimer _debounce;

    public DeviceChangeMonitor()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? DevicesChanged;

    public void Attach(Window window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        source?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmDeviceChange && (int)wParam is DbtDeviceArrival or DbtDeviceRemoveComplete or DbtDevNodesChanged)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        return 0;
    }
}
