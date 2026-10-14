using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FlashTool.Core;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Platform.Windows;

/// <summary>
/// Grows an NTFS volume to fill its (already enlarged) partition with
/// FSCTL_EXTEND_VOLUME, the same call diskpart's "extend filesystem" uses.
/// Runs after the raw device is released, once Windows has mounted the volume.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NtfsVolumeExtender : IFileSystemResizer
{
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(30);

    private readonly int _diskNumber;

    public NtfsVolumeExtender(int diskNumber) => _diskNumber = diskNumber;

    public bool RequiresOfflineDevice => false;

    public bool CanResize(FileSystemType fileSystem) => fileSystem == FileSystemType.Ntfs;

    public bool Resize(IBlockDevice? device, PartitionInfo partition, CancellationToken cancellationToken)
    {
        RescanDisk();
        var volume = WaitForVolume(partition, cancellationToken)
            ?? throw new FlashToolException(FlashErrorCode.FileSystemResizeFailed, "Windows did not mount the NTFS volume.");

        using var handle = NativeMethods.CreateFile(
            volume.VolumeName,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0,
            NativeMethods.OpenExisting,
            0,
            0);
        if (handle.IsInvalid)
        {
            throw new FlashToolException(FlashErrorCode.FileSystemResizeFailed, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        // NTFS_VOLUME_DATA_BUFFER: ... NumberSectors at 8, BytesPerSector at 40.
        var data = new byte[128];
        NativeMethods.IoctlOrThrow(handle, NativeMethods.FsctlGetNtfsVolumeData, default, data);
        var currentSectors = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        var bytesPerSector = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40));
        if (bytesPerSector == 0)
        {
            return false;
        }

        // Leave the last sector for NTFS's backup boot sector.
        var extent = volume.Extents.First(e => e.DiskNumber == _diskNumber && e.Offset == partition.Offset);
        var newSectors = (extent.Length / bytesPerSector) - 1;
        if (newSectors <= currentSectors)
        {
            return false;
        }

        Span<byte> input = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(input, newSectors);
        if (!NativeMethods.Ioctl(handle, NativeMethods.FsctlExtendVolume, input, default, out _, out var error))
        {
            throw new FlashToolException(FlashErrorCode.FileSystemResizeFailed, new Win32Exception(error).Message);
        }

        return true;
    }

    /// <summary>Waits until a volume covering the whole enlarged partition appears.</summary>
    private VolumeInfo? WaitForVolume(PartitionInfo partition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + MountTimeout;
        while (true)
        {
            var volume = VolumeEnumerator.GetVolumes().FirstOrDefault(v =>
                v.Extents.Any(e => e.DiskNumber == _diskNumber && e.Offset == partition.Offset && e.Length >= partition.Length));
            if (volume != null || DateTime.UtcNow > deadline)
            {
                return volume;
            }

            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void RescanDisk()
    {
        using var disk = NativeMethods.CreateFile(
            $@"\\.\PhysicalDrive{_diskNumber}",
            0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0,
            NativeMethods.OpenExisting,
            0,
            0);
        if (!disk.IsInvalid)
        {
            NativeMethods.Ioctl(disk, NativeMethods.IoctlDiskUpdateProperties, default, default, out _, out _);
        }
    }
}
