using FlashTool.Core.IO;

namespace FlashTool.Core.Devices;

public enum DiskAccessMode
{
    /// <summary>Read the whole disk (image creation). Volumes stay mounted.</summary>
    Read,

    /// <summary>Exclusive write access: all volumes are locked and dismounted.</summary>
    Write,
}

/// <summary>
/// Platform service: lists disks and opens them for raw access.
/// </summary>
public interface IDiskManager
{
    IReadOnlyList<DiskInfo> GetDisks();

    /// <summary>
    /// Opens a disk. For <see cref="DiskAccessMode.Write"/> the implementation
    /// must lock/dismount the volumes and, when the returned device is
    /// disposed, ask the OS to re-read the partition table.
    /// </summary>
    IBlockDevice Open(DiskInfo disk, DiskAccessMode mode);

    /// <summary>
    /// Prepares the disk for unplugging ("safely remove hardware"): flushes and
    /// dismounts its volumes and takes the device or its media offline.
    /// Must not be called while the disk is open.
    /// </summary>
    /// <exception cref="FlashToolException"><see cref="FlashErrorCode.DeviceEjectFailed"/> when the OS refuses.</exception>
    void Eject(DiskInfo disk);
}
