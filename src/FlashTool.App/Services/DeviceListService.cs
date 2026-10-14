using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.ViewModels;
using FlashTool.Core.Devices;
using Wpf.Ui.Controls;

namespace FlashTool.App.Services;

/// <summary>
/// Shared, auto-refreshing list of disks. Item view models are updated in
/// place so selections on the pages survive a refresh.
/// </summary>
public sealed partial class DeviceListService : ObservableObject
{
    private readonly IDiskManager _diskManager;
    private readonly SettingsService _settings;
    private readonly OperationState _operation;
    private readonly List<DeviceItemViewModel> _all = [];

    [ObservableProperty]
    private bool _isRefreshing;

    public DeviceListService(IDiskManager diskManager, SettingsService settings, DeviceChangeMonitor monitor, OperationState operation)
    {
        _diskManager = diskManager;
        _settings = settings;
        _operation = operation;
        monitor.DevicesChanged += (_, _) => _ = RefreshAsync();
        operation.PropertyChanged += OnOperationChanged;
    }

    /// <summary>Outcome of the last "safely remove" request, shown under the device lists.</summary>
    public ResultViewModel EjectResult { get; } = new();

    /// <summary>Devices visible with the current filter.</summary>
    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];

    public bool HasDevices => Devices.Count > 0;

    public bool ShowAllDisks
    {
        get => _settings.Settings.ShowNonRemovableDisks;
        set
        {
            if (_settings.Settings.ShowNonRemovableDisks == value)
            {
                return;
            }

            _settings.Settings.ShowNonRemovableDisks = value;
            _settings.Save();
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var disks = await Task.Run(_diskManager.GetDisks);
            Merge(disks);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep the previous list; enumeration failures are transient (device being removed).
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// "Safely remove hardware" for one device. Counts as an operation, so writing,
    /// reading and verifying cannot start while it runs and it cannot start during them.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEject))]
    private async Task EjectAsync(DeviceItemViewModel? device)
    {
        if (device == null)
        {
            return;
        }

        _ = _operation.Begin();
        var failed = false;
        EjectResult.Hide();
        try
        {
            await Task.Run(() => _diskManager.Eject(device.Info), CancellationToken.None);
            EjectResult.Show(InfoBarSeverity.Success, Loc.Format("Eject_Done", device.Name), string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failed = true;
            EjectResult.Show(InfoBarSeverity.Error, Loc.Get("Eject_FailedTitle"), Formatting.Error(ex));
        }
        finally
        {
            _operation.End(failed);
            await RefreshAsync();
        }
    }

    private bool CanEject(DeviceItemViewModel? device) => device is { CanBeEjected: true } && !_operation.IsBusy;

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            // A new operation makes the last eject message stale.
            if (_operation.IsBusy)
            {
                EjectResult.Hide();
            }

            EjectCommand.NotifyCanExecuteChanged();
        }
    }

    private void Merge(IReadOnlyList<DiskInfo> disks)
    {
        var remaining = _all.ToList();
        foreach (var disk in disks)
        {
            var existing = remaining.FirstOrDefault(d => d.Matches(disk));
            if (existing != null)
            {
                existing.Update(disk);
                remaining.Remove(existing);
            }
            else
            {
                _all.Add(new DeviceItemViewModel(disk));
            }
        }

        foreach (var gone in remaining)
        {
            _all.Remove(gone);
        }

        _all.Sort((a, b) => a.Info.Number.CompareTo(b.Info.Number));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var visible = _all.Where(d => ShowAllDisks || d.Info.IsShownByDefault).ToList();

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!visible.Contains(Devices[i]))
            {
                Devices.RemoveAt(i);
            }
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var index = Devices.IndexOf(visible[i]);
            if (index < 0)
            {
                Devices.Insert(i, visible[i]);
            }
            else if (index != i)
            {
                Devices.Move(index, i);
            }
        }

        OnPropertyChanged(nameof(HasDevices));
    }
}
