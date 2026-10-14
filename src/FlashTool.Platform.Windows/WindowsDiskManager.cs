using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using FlashTool.Core;
using FlashTool.Core.Devices;
using FlashTool.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace FlashTool.Platform.Windows;

/// <summary>
/// Lists physical disks and opens them for raw access (\\.\PhysicalDriveN).
/// Enumeration works without elevation; reading/writing requires administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskManager : IDiskManager
{
    private const int MaxDiskNumber = 128;

    public IReadOnlyList<DiskInfo> GetDisks()
    {
        var volumes = VolumeEnumerator.GetVolumes();
        var systemDisks = GetSystemDiskNumbers(volumes);
        var disks = new List<DiskInfo>();

        for (var number = 0; number < MaxDiskNumber; number++)
        {
            var info = TryQueryDisk(number, volumes, systemDisks);
            if (info != null)
            {
                disks.Add(info);
            }
        }

        return disks;
    }

    public IBlockDevice Open(DiskInfo disk, DiskAccessMode mode)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (mode == DiskAccessMode.Write && disk.IsSystemDisk)
        {
            throw new FlashToolException(FlashErrorCode.TargetIsSystemDisk, "Refusing to write to the system disk.");
        }

        var lockedVolumes = new List<SafeFileHandle>();
        try
        {
            if (mode == DiskAccessMode.Write)
            {
                foreach (var volume in VolumeEnumerator.GetVolumes().Where(v => v.DiskNumbers.Contains(disk.Number)))
                {
                    lockedVolumes.Add(LockAndDismount(volume));
                }
            }

            var access = mode == DiskAccessMode.Write
                ? NativeMethods.GenericRead | NativeMethods.GenericWrite
                : NativeMethods.GenericRead;
            var handle = NativeMethods.CreateFile(
                disk.DevicePath,
                access,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                0,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagNoBuffering | NativeMethods.FileFlagWriteThrough,
                0);
            if (handle.IsInvalid)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw error switch
                {
                    NativeMethods.ErrorAccessDenied => new FlashToolException(FlashErrorCode.DeviceAccessDenied, "Access denied. Run as administrator.", new Win32Exception(error)),
                    NativeMethods.ErrorFileNotFound => new FlashToolException(FlashErrorCode.DeviceNotFound, "The device is gone.", new Win32Exception(error)),
                    _ => new FlashToolException(FlashErrorCode.DeviceIoError, new Win32Exception(error).Message, new Win32Exception(error)),
                };
            }

            try
            {
                var length = GetLength(handle);
                var sectorSize = GetGeometry(handle)?.SectorSize ?? disk.SectorSize;

                if (mode == DiskAccessMode.Write && !NativeMethods.Ioctl(handle, NativeMethods.IoctlDiskIsWritable, default, default, out _, out var err)
                    && err == NativeMethods.ErrorWriteProtect)
                {
                    throw new FlashToolException(FlashErrorCode.DeviceWriteProtected, "The media is write protected.");
                }

                return new WindowsDiskDevice(handle, length, sectorSize, mode == DiskAccessMode.Write, lockedVolumes);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            foreach (var v in lockedVolumes)
            {
                v.Dispose();
            }

            throw;
        }
    }

    private static DiskInfo? TryQueryDisk(int number, List<VolumeInfo> volumes, HashSet<int> systemDisks)
    {
        var path = $@"\\.\PhysicalDrive{number}";
        using var handle = NativeMethods.CreateFile(path, 0, NativeMethods.FileShareRead | NativeMethods.FileShareWrite, 0, NativeMethods.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var descriptor = QueryDescriptor(handle);
        var geometry = GetGeometry(handle);
        var writeProtected = !NativeMethods.Ioctl(handle, NativeMethods.IoctlDiskIsWritable, default, default, out _, out var error)
            && error == NativeMethods.ErrorWriteProtect;

        var vendor = descriptor?.Vendor ?? string.Empty;
        var model = descriptor?.Product ?? string.Empty;
        var bus = descriptor?.BusType ?? DiskBusType.Unknown;
        var removable = descriptor?.Removable ?? geometry?.RemovableMediaType ?? false;

        return new DiskInfo
        {
            Number = number,
            DevicePath = path,
            Vendor = vendor,
            Model = model,
            SerialNumber = descriptor?.Serial ?? string.Empty,
            BusType = bus,
            IsRemovableMedia = removable,
            Kind = DiskClassifier.Classify(bus, removable, vendor, model),
            Size = geometry?.Size ?? 0,
            SectorSize = geometry?.SectorSize ?? 512,
            HasMedia = geometry != null,
            IsWriteProtected = writeProtected,
            IsSystemDisk = systemDisks.Contains(number),
            MountPoints = volumes
                .Where(v => v.DiskNumbers.Contains(number))
                .SelectMany(v => v.MountPoints)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static HashSet<int> GetSystemDiskNumbers(List<VolumeInfo> volumes)
    {
        var roots = new[] { Path.GetPathRoot(Environment.SystemDirectory), Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) }
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r!)
            .ToList();

        return volumes
            .Where(v => v.MountPoints.Any(m => roots.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .SelectMany(v => v.DiskNumbers)
            .ToHashSet();
    }

    private sealed record Descriptor(string Vendor, string Product, string Serial, DiskBusType BusType, bool Removable);

    private static Descriptor? QueryDescriptor(SafeFileHandle handle)
    {
        // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery }.
        Span<byte> query = stackalloc byte[12];
        query.Clear();
        var output = new byte[1024];
        if (!NativeMethods.Ioctl(handle, NativeMethods.IoctlStorageQueryProperty, query, output, out var returned, out _) || returned < 36)
        {
            return null;
        }

        string ReadString(int fieldOffset)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(fieldOffset));
            if (offset <= 0 || offset >= returned)
            {
                return string.Empty;
            }

            var end = Array.IndexOf(output, (byte)0, offset);
            if (end < 0)
            {
                end = returned;
            }

            return Encoding.ASCII.GetString(output, offset, end - offset).Trim();
        }

        return new Descriptor(
            ReadString(12),
            ReadString(16),
            ReadString(24),
            (DiskBusType)BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(28)),
            output[10] != 0);
    }

    private sealed record Geometry(long Size, int SectorSize, bool RemovableMediaType);

    private static Geometry? GetGeometry(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX { DISK_GEOMETRY (24 bytes), LARGE_INTEGER DiskSize, ... }.
        var output = new byte[256];
        if (!NativeMethods.Ioctl(handle, NativeMethods.IoctlDiskGetDriveGeometryEx, default, output, out var returned, out _) || returned < 32)
        {
            return null;
        }

        const int removableMedia = 11;
        var mediaType = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8));
        var sectorSize = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(20));
        var size = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24));
        return new Geometry(size, sectorSize is 512 or 1024 or 2048 or 4096 ? sectorSize : 512, mediaType == removableMedia);
    }

    private static long GetLength(SafeFileHandle handle)
    {
        Span<byte> output = stackalloc byte[8];
        NativeMethods.IoctlOrThrow(handle, NativeMethods.IoctlDiskGetLengthInfo, default, output);
        return BinaryPrimitives.ReadInt64LittleEndian(output);
    }

    /// <summary>
    /// Locks and dismounts a volume so Windows lets us overwrite its sectors.
    /// The returned handle keeps the lock until it is closed.
    /// </summary>
    private static SafeFileHandle LockAndDismount(VolumeInfo volume)
    {
        var handle = NativeMethods.CreateFile(
            volume.VolumeName,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0,
            NativeMethods.OpenExisting,
            0,
            0);
        if (handle.IsInvalid)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error == NativeMethods.ErrorAccessDenied)
            {
                throw new FlashToolException(FlashErrorCode.DeviceAccessDenied, "Access denied. Run as administrator.", new Win32Exception(error));
            }

            // Volume vanished in the meantime: nothing to lock.
            return handle;
        }

        var locked = false;
        for (var attempt = 0; attempt < 20 && !locked; attempt++)
        {
            locked = NativeMethods.Ioctl(handle, NativeMethods.FsctlLockVolume, default, default, out _, out _);
            if (!locked)
            {
                Thread.Sleep(250);
            }
        }

        if (!locked)
        {
            // Some process keeps files open (Explorer, antivirus). A forced dismount
            // invalidates their handles, after which the lock normally succeeds.
            NativeMethods.Ioctl(handle, NativeMethods.FsctlDismountVolume, default, default, out _, out _);
            locked = NativeMethods.Ioctl(handle, NativeMethods.FsctlLockVolume, default, default, out _, out _);
        }

        if (!locked)
        {
            handle.Dispose();
            throw new FlashToolException(
                FlashErrorCode.DeviceLockFailed,
                $"Volume {string.Join(", ", volume.MountPoints)} is in use.",
                null,
                string.Join(", ", volume.MountPoints.DefaultIfEmpty(volume.VolumeName)));
        }

        NativeMethods.Ioctl(handle, NativeMethods.FsctlDismountVolume, default, default, out _, out _);
        return handle;
    }
}
