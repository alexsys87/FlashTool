#if DEBUG
using System.IO;
using FlashTool.Core;
using FlashTool.Core.Devices;
using FlashTool.Core.IO;

namespace FlashTool.App.Services;

/// <summary>
/// Debug-only stand-in for <see cref="IDiskManager"/> (enabled with FLASHTOOL_DEMO=1):
/// lists one device of each kind backed by sparse files in %TEMP%\FlashToolDemo,
/// so the UI and the full write/read pipeline can be exercised without real hardware.
/// Like real disks, a disk opened for writing is exclusive, so a handle leaked by one
/// page shows up as a "disk is in use" error on the next operation.
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

    /// <summary>Ejected disks reappear after this long, as if they were plugged back in.</summary>
    private static readonly TimeSpan ReinsertDelay = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly Dictionary<int, (int Readers, bool Writer)> _open = [];
    private readonly Dictionary<int, DateTime> _ejected = [];

    public IReadOnlyList<DiskInfo> GetDisks()
    {
        lock (_sync)
        {
            return Disks.Where(d => !_ejected.TryGetValue(d.Number, out var at) || DateTime.UtcNow - at > ReinsertDelay).ToList();
        }
    }

    public void Eject(DiskInfo disk)
    {
        lock (_sync)
        {
            if (_open.ContainsKey(disk.Number))
            {
                throw new FlashToolException(FlashErrorCode.DeviceEjectFailed, "Demo disk is still open.", null, EjectFailure.InUse, disk.DevicePath);
            }

            _ejected[disk.Number] = DateTime.UtcNow;
        }
    }

    public IBlockDevice Open(DiskInfo disk, DiskAccessMode mode)
    {
        lock (_sync)
        {
            _open.TryGetValue(disk.Number, out var state);
            if (state.Writer || (mode == DiskAccessMode.Write && state.Readers > 0))
            {
                throw new FlashToolException(FlashErrorCode.DeviceLockFailed, "Demo disk is still open.", null, disk.DevicePath);
            }

            _open[disk.Number] = mode == DiskAccessMode.Write ? (0, true) : (state.Readers + 1, false);
        }

        try
        {
            return new TrackedDevice(OpenFile(disk, mode), () => Release(disk.Number, mode));
        }
        catch
        {
            Release(disk.Number, mode);
            throw;
        }
    }

    private void Release(int number, DiskAccessMode mode)
    {
        lock (_sync)
        {
            var state = _open[number];
            state = mode == DiskAccessMode.Write ? (state.Readers, false) : (state.Readers - 1, state.Writer);
            if (state is (0, false))
            {
                _open.Remove(number);
            }
            else
            {
                _open[number] = state;
            }
        }
    }

    private static IBlockDevice OpenFile(DiskInfo disk, DiskAccessMode mode)
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

    /// <summary>Reports when the device is closed; using it afterwards is a bug.</summary>
    private sealed class TrackedDevice(IBlockDevice inner, Action released) : IBlockDevice
    {
        private int _disposed;

        public long Length => inner.Length;

        public int SectorSize => inner.SectorSize;

        public bool CanWrite => inner.CanWrite;

        public void Read(long offset, Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            inner.Read(offset, buffer);
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            inner.Write(offset, buffer);
        }

        public void Flush() => inner.Flush();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                released();
            }
        }
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
