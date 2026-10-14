namespace FlashTool.Core.Devices;

/// <summary>Mirror of the Windows STORAGE_BUS_TYPE enumeration.</summary>
public enum DiskBusType
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    Raid = 8,
    IScsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
    Scm = 18,
    Ufs = 19,
}

/// <summary>Visual/behavioral category of a disk.</summary>
public enum DiskKind
{
    /// <summary>USB flash drive.</summary>
    UsbFlash,

    /// <summary>SD/microSD/MMC card (native slot or USB card reader).</summary>
    SdCard,

    /// <summary>External (USB/FireWire) hard drive or SSD.</summary>
    ExternalDrive,

    /// <summary>Internal fixed disk (SATA/NVMe/...). Hidden by default.</summary>
    InternalDrive,
}

/// <summary>
/// Snapshot of a physical disk as reported by the OS.
/// </summary>
public sealed record DiskInfo
{
    /// <summary>OS disk number (\\.\PhysicalDriveN on Windows).</summary>
    public required int Number { get; init; }

    /// <summary>Path used to open the raw device.</summary>
    public required string DevicePath { get; init; }

    public string Vendor { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string SerialNumber { get; init; } = string.Empty;

    public long Size { get; init; }

    public int SectorSize { get; init; } = 512;

    public DiskBusType BusType { get; init; }

    public bool IsRemovableMedia { get; init; }

    public DiskKind Kind { get; init; }

    /// <summary>Mounted volumes, e.g. "E:\".</summary>
    public IReadOnlyList<string> MountPoints { get; init; } = [];

    /// <summary>The disk holding the running OS; writing to it is always refused.</summary>
    public bool IsSystemDisk { get; init; }

    /// <summary>False for an empty card reader slot.</summary>
    public bool HasMedia { get; init; } = true;

    /// <summary>Hardware write-protection (e.g. the lock switch of an SD card).</summary>
    public bool IsWriteProtected { get; init; }

    public string DisplayName
    {
        get
        {
            var name = string.Join(' ', new[] { Vendor, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(name) ? $"Disk {Number}" : name;
        }
    }

    /// <summary>Removable/external devices are listed by default; internal disks only on request.</summary>
    public bool IsShownByDefault => Kind != DiskKind.InternalDrive && !IsSystemDisk;

    public bool CanBeWritten => !IsSystemDisk && HasMedia && !IsWriteProtected && Size > 0;
}
