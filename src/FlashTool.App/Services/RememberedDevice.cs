using System.Collections.Specialized;
using FlashTool.App.ViewModels;

namespace FlashTool.App.Services;

/// <summary>
/// Remembers the device a page had selected (across runs) and selects it again
/// as soon as it is present — at startup or when it is plugged back in.
/// </summary>
public sealed class RememberedDevice
{
    private readonly DeviceListService _devices;
    private readonly Func<string?> _load;
    private readonly Action<string> _save;
    private readonly Func<DeviceItemViewModel?> _getSelected;
    private readonly Action<DeviceItemViewModel> _select;
    private readonly Func<DeviceItemViewModel, bool> _eligible;

    public RememberedDevice(
        DeviceListService devices,
        Func<string?> load,
        Action<string> save,
        Func<DeviceItemViewModel?> getSelected,
        Action<DeviceItemViewModel> select,
        Func<DeviceItemViewModel, bool> eligible)
    {
        _devices = devices;
        _load = load;
        _save = save;
        _getSelected = getSelected;
        _select = select;
        _eligible = eligible;
        _devices.Devices.CollectionChanged += OnDevicesChanged;
    }

    /// <summary>Call when the page's selection changes.</summary>
    public void OnSelectionChanged(DeviceItemViewModel? device)
    {
        // A device disappearing clears the selection; keep remembering it so it is reselected on re-plug.
        if (device != null && device.Key != _load())
        {
            _save(device.Key);
        }
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_getSelected() != null || _load() is not { } key)
        {
            return;
        }

        var match = _devices.Devices.FirstOrDefault(d => d.Key == key && _eligible(d));
        if (match != null)
        {
            _select(match);
        }
    }
}
