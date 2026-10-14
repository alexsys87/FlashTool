using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlashTool.Platform.Windows;

/// <summary>Win32 declarations used for raw disk access.</summary>
internal static unsafe partial class NativeMethods
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint OpenExisting = 3;
    public const uint FileFlagNoBuffering = 0x20000000;
    public const uint FileFlagWriteThrough = 0x80000000;

    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlDiskGetLengthInfo = 0x0007405C;
    public const uint IoctlDiskIsWritable = 0x00070024;
    public const uint IoctlDiskUpdateProperties = 0x00070140;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    public const uint FsctlLockVolume = 0x00090018;
    public const uint FsctlUnlockVolume = 0x0009001C;
    public const uint FsctlDismountVolume = 0x00090020;
    public const uint FsctlGetNtfsVolumeData = 0x00090064;
    public const uint FsctlExtendVolume = 0x000900F0;

    public const int ErrorFileNotFound = 2;
    public const int ErrorAccessDenied = 5;
    public const int ErrorWriteProtect = 19;
    public const int ErrorNotReady = 21;
    public const int ErrorMoreData = 234;
    public const int ErrorNoMoreFiles = 18;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    public const uint ThreadTerminate = 0x0001;

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelSynchronousIo(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    public static partial nint FindFirstVolume(char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextVolume(nint findHandle, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindVolumeClose(nint findHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathNamesForVolumeName(string volumeName, char* volumePathNames, uint bufferLength, out uint returnLength);

    /// <summary>Calls DeviceIoControl with byte buffers. Returns false and the Win32 error on failure.</summary>
    public static bool Ioctl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output, out int returned, out int error)
    {
        fixed (byte* inPtr = input)
        fixed (byte* outPtr = output)
        {
            var ok = DeviceIoControl(
                handle,
                code,
                input.IsEmpty ? null : inPtr,
                (uint)input.Length,
                output.IsEmpty ? null : outPtr,
                (uint)output.Length,
                out var bytes,
                0);
            returned = (int)bytes;
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }
    }

    /// <summary>Same as <see cref="Ioctl"/> but throws <see cref="Win32Exception"/> on failure.</summary>
    public static int IoctlOrThrow(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (!Ioctl(handle, code, input, output, out var returned, out var error))
        {
            throw new Win32Exception(error);
        }

        return returned;
    }
}
