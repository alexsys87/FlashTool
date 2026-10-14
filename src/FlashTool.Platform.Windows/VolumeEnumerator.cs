using System.Buffers.Binary;
using System.Runtime.Versioning;

namespace FlashTool.Platform.Windows;

/// <summary>A piece of a volume on one disk.</summary>
internal sealed record VolumeExtent(int DiskNumber, long Offset, long Length);

/// <summary>A mounted (or mountable) volume and the disks it lives on.</summary>
/// <param name="VolumeName">"\\?\Volume{GUID}" without the trailing backslash.</param>
internal sealed record VolumeInfo(string VolumeName, IReadOnlyList<VolumeExtent> Extents, IReadOnlyList<string> MountPoints)
{
    public IReadOnlyList<int> DiskNumbers => Extents.Select(e => e.DiskNumber).Distinct().ToList();
}

[SupportedOSPlatform("windows")]
internal static unsafe class VolumeEnumerator
{
    public static List<VolumeInfo> GetVolumes()
    {
        var result = new List<VolumeInfo>();
        var buffer = stackalloc char[512];
        var find = NativeMethods.FindFirstVolume(buffer, 512);
        if (find == -1)
        {
            return result;
        }

        try
        {
            do
            {
                var name = new string(buffer);
                var info = TryDescribe(name);
                if (info != null)
                {
                    result.Add(info);
                }
            }
            while (NativeMethods.FindNextVolume(find, buffer, 512));
        }
        finally
        {
            NativeMethods.FindVolumeClose(find);
        }

        return result;
    }

    private static VolumeInfo? TryDescribe(string volumeNameWithSlash)
    {
        var volumeName = volumeNameWithSlash.TrimEnd('\\');
        using var handle = NativeMethods.CreateFile(
            volumeName,
            0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            0,
            NativeMethods.OpenExisting,
            0,
            0);
        if (handle.IsInvalid)
        {
            return null;
        }

        // VOLUME_DISK_EXTENTS: DWORD count, (padding), DISK_EXTENT[] { DWORD disk, (padding), LONGLONG offset, LONGLONG length }.
        var output = new byte[8 + (24 * 32)];
        if (!NativeMethods.Ioctl(handle, NativeMethods.IoctlVolumeGetVolumeDiskExtents, default, output, out _, out _))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(output);
        var extents = new List<VolumeExtent>();
        for (var i = 0; i < count && 8 + ((i + 1) * 24) <= output.Length; i++)
        {
            var e = output.AsSpan(8 + (i * 24));
            extents.Add(new VolumeExtent(
                BinaryPrimitives.ReadInt32LittleEndian(e),
                BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(e[16..])));
        }

        return new VolumeInfo(volumeName, extents, GetMountPoints(volumeNameWithSlash));
    }

    private static List<string> GetMountPoints(string volumeNameWithSlash)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            if (!NativeMethods.GetVolumePathNamesForVolumeName(volumeNameWithSlash, p, (uint)buffer.Length, out var length))
            {
                return [];
            }

            // REG_MULTI_SZ style list.
            return new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }
    }
}
