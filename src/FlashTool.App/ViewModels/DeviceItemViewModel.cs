using FlashTool.App.Localization;
using FlashTool.Core.Devices;

namespace FlashTool.App.ViewModels;

/// <summary>One disk in the device lists.</summary>
public sealed class DeviceItemViewModel : LocalizedViewModel
{
    public DeviceItemViewModel(DiskInfo info) => Info = info;

    public DiskInfo Info { get; private set; }

    public DiskKind Kind => Info.Kind;

    public string Name => Info.DisplayName;

    public string SizeText => Info.HasMedia ? Formatting.Size(Info.Size) : Loc.Get("Devices_NoMedia");

    public string Details
    {
        get
        {
            var parts = new List<string> { Formatting.DiskKind(Info.Kind), Loc.Format("Devices_DiskNumber", Info.Number) };
            if (Info.MountPoints.Count > 0)
            {
                parts.Add(string.Join(" ", Info.MountPoints));
            }

            return string.Join(" · ", parts);
        }
    }

    public bool IsSystemDisk => Info.IsSystemDisk;

    public bool IsWriteProtected => Info.IsWriteProtected;

    public bool HasMedia => Info.HasMedia;

    public bool CanBeWritten => Info.CanBeWritten;

    public bool CanBeRead => Info.HasMedia && Info.Size > 0;

    /// <summary>Identity across refreshes (disk numbers may shift after re-plugging).</summary>
    public bool Matches(DiskInfo other)
        => other.DevicePath == Info.DevicePath
           && other.Model == Info.Model
           && other.SerialNumber == Info.SerialNumber;

    public void Update(DiskInfo info)
    {
        Info = info;
        OnPropertyChanged(string.Empty);
    }

    public override string ToString() => $"{Name} ({SizeText})";
}
