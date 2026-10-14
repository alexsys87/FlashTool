using FlashTool.Core.Devices;

namespace FlashTool.Core.Tests;

public class DiskClassifierTests
{
    [Theory]
    [InlineData(DiskBusType.Sd, true, "", "SD Card", DiskKind.SdCard)]
    [InlineData(DiskBusType.Usb, true, "Generic", "STORAGE DEVICE SD/MMC", DiskKind.SdCard)]
    [InlineData(DiskBusType.Usb, true, "Generic-", "Multi-Card", DiskKind.SdCard)]
    [InlineData(DiskBusType.Usb, true, "SanDisk", "Cruzer Blade", DiskKind.UsbFlash)]
    [InlineData(DiskBusType.Usb, true, "Kingston", "DataTraveler 3.0", DiskKind.UsbFlash)]
    [InlineData(DiskBusType.Usb, false, "WD", "My Passport 25E2", DiskKind.ExternalDrive)]
    [InlineData(DiskBusType.Nvme, false, "Samsung", "SSD 980 PRO", DiskKind.InternalDrive)]
    [InlineData(DiskBusType.Sata, false, "", "ST2000DM008", DiskKind.InternalDrive)]
    public void Disks_are_classified(DiskBusType bus, bool removable, string vendor, string model, DiskKind expected)
        => Assert.Equal(expected, DiskClassifier.Classify(bus, removable, vendor, model));

    [Fact]
    public void Internal_and_system_disks_are_hidden_by_default()
    {
        var external = new DiskInfo { Number = 1, DevicePath = "x", Kind = DiskKind.UsbFlash };
        var internalDisk = external with { Kind = DiskKind.InternalDrive };
        var system = external with { IsSystemDisk = true };

        Assert.True(external.IsShownByDefault);
        Assert.False(internalDisk.IsShownByDefault);
        Assert.False(system.IsShownByDefault);
        Assert.False(system.CanBeWritten);
    }
}
