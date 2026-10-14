#if DEBUG
using System.IO;
using FlashTool.Core.Devices;
using FlashTool.Core.IO;

namespace FlashTool.App.Services;

/// <summary>
/// Debug-only stand-in for <see cref="IDiskManager"/> (enabled with FLASHTOOL_DEMO=1):
/// lists one device of each kind backed by sparse files in %TEMP%\FlashToolDemo,
/// so the UI and the full write/read pipeline can be exercised without real hardware.
/// </summary>
public sealed class DemoDiskManager : IDiskManager
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "FlashToolDemo");

    private static readonly DiskInfo[] Disks =
    [
        Disk(1, "SanDisk", "Ultra USB 3.0", 8 * GiB, DiskBusType.Usb, removable: true),
        Disk(2, "Generic", "STORAGE DEVICE SD/MMC", 2 * GiB, DiskBusType.Usb, removable: true),
        Disk(3, "WD", "My Passport 25E2", 16 * GiB, DiskBusType.Usb, removable: false) with { MountPoints = ["F:\\"] },
        Disk(0, "Samsung", "SSD 980 PRO", 64 * GiB, DiskBusType.Nvme, removable: false) with { IsSystemDisk = true, MountPoints = ["C:\\"] },
    ];

    public IReadOnlyList<DiskInfo> GetDisks() => Disks;

    public IBlockDevice Open(DiskInfo disk, DiskAccessMode mode)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"disk{disk.Number}.bin");
        if (!File.Exists(path))
        {
            using var file = File.Create(path);
            file.SetLength(disk.Size);
        }

        var device = HandleBlockDevice.OpenFile(path, writable: mode == DiskAccessMode.Write);

        // FLASHTOOL_DEMO_FAULT=N: the first N writes touching 64 MiB of the USB stick fail like a weak sector.
        return disk.Number == 1 && int.TryParse(Environment.GetEnvironmentVariable("FLASHTOOL_DEMO_FAULT"), out var faults) && faults > 0
            ? new FaultyDevice(device, 64L * 1024 * 1024, faults)
            : device;
    }

    private sealed class FaultyDevice(IBlockDevice inner, long badOffset, int faults) : IBlockDevice
    {
        private int _remaining = faults;

        public long Length => inner.Length;

        public int SectorSize => inner.SectorSize;

        public bool CanWrite => inner.CanWrite;

        public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            if (badOffset >= offset && badOffset < offset + buffer.Length && _remaining > 0)
            {
                _remaining--;
                throw new IOException("The request could not be performed because of an I/O device error.");
            }

            inner.Write(offset, buffer);
        }

        public void Flush() => inner.Flush();

        public void Dispose() => inner.Dispose();
    }

    private static DiskInfo Disk(int number, string vendor, string model, long size, DiskBusType bus, bool removable) => new()
    {
        Number = number,
        DevicePath = $"demo://{number}",
        Vendor = vendor,
        Model = model,
        Size = size,
        BusType = bus,
        IsRemovableMedia = removable,
        Kind = DiskClassifier.Classify(bus, removable, vendor, model),
    };
}
#endif
