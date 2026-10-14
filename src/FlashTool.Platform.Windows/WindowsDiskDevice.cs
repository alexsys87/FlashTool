using System.Runtime.Versioning;
using FlashTool.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace FlashTool.Platform.Windows;

/// <summary>
/// Raw disk opened by <see cref="WindowsDiskManager"/>. Holds the volume
/// locks for its lifetime and makes Windows re-read the partition table
/// when it is closed after writing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDiskDevice : HandleBlockDevice, IAbortableIo
{
    /// <summary>A single request that takes longer than this is treated as a hung device.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly List<SafeFileHandle> _volumeLocks;
    private readonly IoWatchdog _watchdog = new(RequestTimeout);

    public WindowsDiskDevice(SafeFileHandle handle, long length, int sectorSize, bool canWrite, List<SafeFileHandle> volumeLocks)
        : base(handle, length, sectorSize, canWrite)
    {
        _volumeLocks = volumeLocks;
    }

    public void SetAbortToken(CancellationToken token) => _watchdog.SetAbortToken(token);

    public override void Read(long offset, Span<byte> buffer)
    {
        using var request = _watchdog.Arm();
        try
        {
            base.Read(offset, buffer);
        }
        catch (Exception ex) when (request.Fired && ex is OperationCanceledException or IOException)
        {
            throw request.ToException(RequestTimeout);
        }
    }

    public override void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        using var request = _watchdog.Arm();
        try
        {
            base.Write(offset, buffer);
        }
        catch (Exception ex) when (request.Fired && ex is OperationCanceledException or IOException)
        {
            throw request.ToException(RequestTimeout);
        }
    }

    public override void Flush()
    {
        // The handle is opened with FILE_FLAG_WRITE_THROUGH; FlushFileBuffers additionally
        // flushes the drive cache.
        base.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchdog.Dispose();

            if (CanWrite && !Handle.IsInvalid && !Handle.IsClosed)
            {
                NativeMethods.Ioctl(Handle, NativeMethods.IoctlDiskUpdateProperties, default, default, out _, out _);
            }

            base.Dispose(disposing);

            foreach (var volume in _volumeLocks)
            {
                if (!volume.IsInvalid && !volume.IsClosed)
                {
                    NativeMethods.Ioctl(volume, NativeMethods.FsctlUnlockVolume, default, default, out _, out _);
                }

                volume.Dispose();
            }
        }
        else
        {
            base.Dispose(disposing);
        }
    }
}
