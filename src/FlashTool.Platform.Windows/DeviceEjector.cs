using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FlashTool.Core;
using FlashTool.Core.Devices;
using Microsoft.Win32.SafeHandles;

namespace FlashTool.Platform.Windows;

/// <summary>
/// "Safely remove hardware" for a physical disk. Hot-pluggable devices (USB sticks,
/// USB card readers, external drives) are ejected through the PnP manager, which flushes
/// and dismounts their volumes first; built-in card readers get their media ejected instead.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class DeviceEjector
{
    private const int FileDeviceDisk = 7;

    /// <summary>Right after writing, Explorer and antivirus briefly open the freshly mounted volumes.</summary>
    private const int VetoAttempts = 5;

    private static readonly TimeSpan VetoDelay = TimeSpan.FromSeconds(1);

    private static readonly string[] VetoNames =
    [
        "unknown", "legacy device", "pending close", "application", "service", "open handles",
        "device", "driver", "illegal request", "insufficient power", "not disableable",
        "legacy driver", "insufficient rights", "already removed",
    ];

    public static void Eject(int diskNumber)
    {
        if (FindDiskDevNode(diskNumber) is { } disk)
        {
            // The USB/Thunderbolt device the disk belongs to (what the taskbar icon ejects),
            // or the disk itself when it is the hot-pluggable part (e.g. an SD card on the SD bus).
            var candidates = new List<uint>();
            if (TryGetParent(disk, out var parent) && IsRemovable(parent))
            {
                candidates.Add(parent);
            }

            if (IsRemovable(disk))
            {
                candidates.Add(disk);
            }

            if (candidates.Count > 0)
            {
                RequestEject(candidates);
                return;
            }
        }

        // Not hot-pluggable (e.g. a card reader built into the laptop): take the media offline.
        EjectMedia(diskNumber);
    }

    private static void RequestEject(List<uint> candidates)
    {
        string? failure = null;
        var vetoName = stackalloc char[NativeMethods.MaxPath];
        foreach (var devInst in candidates)
        {
            for (var attempt = 1; ; attempt++)
            {
                var vetoType = NativeMethods.PnpVetoTypeUnknown;
                vetoName[0] = '\0';
                var result = NativeMethods.CmRequestDeviceEject(devInst, &vetoType, vetoName, NativeMethods.MaxPath, 0);
                if (result == NativeMethods.CrSuccess && vetoType == NativeMethods.PnpVetoTypeUnknown)
                {
                    return;
                }

                if (vetoType == NativeMethods.PnpVetoTypeUnknown)
                {
                    // Not a veto (e.g. the node cannot be ejected): try the next candidate.
                    failure = $"CM error 0x{result:X}";
                    break;
                }

                var name = new string(vetoName);
                var kind = vetoType < VetoNames.Length ? VetoNames[vetoType] : vetoType.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var detail = string.IsNullOrEmpty(name) ? kind : $"{kind}: {name}";
                if (attempt >= VetoAttempts)
                {
                    throw new FlashToolException(FlashErrorCode.DeviceEjectFailed, $"Eject vetoed ({detail}).", null, EjectFailure.InUse, detail);
                }

                Thread.Sleep(VetoDelay);
            }
        }

        throw new FlashToolException(FlashErrorCode.DeviceEjectFailed, $"Eject failed ({failure}).", null, EjectFailure.Refused, failure);
    }

    private static void EjectMedia(int diskNumber)
    {
        var locks = new List<SafeFileHandle>();
        try
        {
            // Flush and dismount every volume so nothing is cached or open when the media goes away.
            foreach (var volume in VolumeEnumerator.GetVolumes().Where(v => v.DiskNumbers.Contains(diskNumber)))
            {
                try
                {
                    locks.Add(WindowsDiskManager.LockAndDismount(volume));
                }
                catch (FlashToolException ex) when (ex.Code == FlashErrorCode.DeviceLockFailed)
                {
                    var detail = string.Join(", ", volume.MountPoints.DefaultIfEmpty(volume.VolumeName));
                    throw new FlashToolException(FlashErrorCode.DeviceEjectFailed, ex.Message, ex, EjectFailure.InUse, detail);
                }
            }

            using var handle = NativeMethods.CreateFile(
                $@"\\.\PhysicalDrive{diskNumber}",
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                0,
                NativeMethods.OpenExisting,
                0,
                0);
            if (handle.IsInvalid)
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                throw new FlashToolException(FlashErrorCode.DeviceEjectFailed, error.Message, error, EjectFailure.Refused, error.Message);
            }

            // PREVENT_MEDIA_REMOVAL { PreventMediaRemoval = FALSE }; drives without a lock just ignore it.
            ReadOnlySpan<byte> allowRemoval = [0];
            NativeMethods.Ioctl(handle, NativeMethods.IoctlStorageMediaRemoval, allowRemoval, default, out _, out _);

            if (!NativeMethods.Ioctl(handle, NativeMethods.IoctlStorageEjectMedia, default, default, out _, out var ejectError))
            {
                var error = new Win32Exception(ejectError);
                throw new FlashToolException(FlashErrorCode.DeviceEjectFailed, error.Message, error, EjectFailure.NotSupported, error.Message);
            }
        }
        finally
        {
            // Closing the lock handles after the media is gone leaves the volumes dismounted.
            foreach (var volumeLock in locks)
            {
                volumeLock.Dispose();
            }
        }
    }

    private static bool TryGetParent(uint devInst, out uint parent)
    {
        uint result;
        var ok = NativeMethods.CmGetParent(&result, devInst, 0) == NativeMethods.CrSuccess;
        parent = result;
        return ok;
    }

    private static bool IsRemovable(uint devInst)
    {
        uint type;
        uint capabilities = 0;
        var length = (uint)sizeof(uint);
        return NativeMethods.CmGetDevNodeRegistryProperty(devInst, NativeMethods.CmDrpCapabilities, &type, &capabilities, &length, 0) == NativeMethods.CrSuccess
               && (capabilities & NativeMethods.CmDevcapRemovable) != 0;
    }

    /// <summary>Finds the PnP device node of \\.\PhysicalDriveN among the present disk interfaces.</summary>
    private static uint? FindDiskDevNode(int diskNumber)
    {
        var guid = NativeMethods.GuidDevInterfaceDisk;
        var set = NativeMethods.SetupDiGetClassDevs(&guid, null, 0, NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);
        if (set == -1)
        {
            return null;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new NativeMethods.SpDeviceInterfaceData { CbSize = (uint)sizeof(NativeMethods.SpDeviceInterfaceData) };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, null, &guid, index, &iface))
                {
                    return null;
                }

                uint required = 0;
                NativeMethods.SetupDiGetDeviceInterfaceDetail(set, &iface, null, 0, &required, null);
                if (required <= sizeof(uint))
                {
                    continue;
                }

                var detail = NativeMemory.Alloc(required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is the size of the fixed part: 8 on 64-bit, 6 on 32-bit.
                    *(uint*)detail = IntPtr.Size == 8 ? 8u : 6u;
                    var info = new NativeMethods.SpDevInfoData { CbSize = (uint)sizeof(NativeMethods.SpDevInfoData) };
                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(set, &iface, detail, required, null, &info))
                    {
                        continue;
                    }

                    var path = new string((char*)((byte*)detail + sizeof(uint)));
                    if (GetDiskNumber(path) == diskNumber)
                    {
                        return info.DevInst;
                    }
                }
                finally
                {
                    NativeMemory.Free(detail);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static int? GetDiskNumber(string interfacePath)
    {
        using var handle = NativeMethods.CreateFile(interfacePath, 0, NativeMethods.FileShareRead | NativeMethods.FileShareWrite, 0, NativeMethods.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        // STORAGE_DEVICE_NUMBER { DeviceType, DeviceNumber, PartitionNumber }.
        Span<byte> output = stackalloc byte[12];
        if (!NativeMethods.Ioctl(handle, NativeMethods.IoctlStorageGetDeviceNumber, default, output, out var returned, out _) || returned < 8)
        {
            return null;
        }

        return BinaryPrimitives.ReadInt32LittleEndian(output) == FileDeviceDisk
            ? BinaryPrimitives.ReadInt32LittleEndian(output[4..])
            : null;
    }
}
