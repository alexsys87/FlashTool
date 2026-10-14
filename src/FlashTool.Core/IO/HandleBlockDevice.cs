using Microsoft.Win32.SafeHandles;

namespace FlashTool.Core.IO;

/// <summary>
/// <see cref="IBlockDevice"/> on top of an OS file handle. Works for regular
/// files (tests, virtual targets) and for raw disk handles on Windows.
/// </summary>
public class HandleBlockDevice : IBlockDevice
{
    private readonly bool _ownsHandle;
    private bool _disposed;

    public HandleBlockDevice(SafeFileHandle handle, long length, int sectorSize, bool canWrite, bool ownsHandle = true)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (sectorSize <= 0 || (sectorSize & (sectorSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Sector size must be a power of two.");
        }

        Handle = handle;
        Length = length;
        SectorSize = sectorSize;
        CanWrite = canWrite;
        _ownsHandle = ownsHandle;
    }

    protected SafeFileHandle Handle { get; }

    public long Length { get; }

    public int SectorSize { get; }

    public bool CanWrite { get; }

    /// <summary>
    /// Opens (or creates) a regular file as a block device. Used by tests and
    /// for "write image to file" scenarios.
    /// </summary>
    public static HandleBlockDevice OpenFile(string path, long? createWithLength = null, int sectorSize = 512, bool writable = true)
    {
        var mode = createWithLength.HasValue ? FileMode.Create : FileMode.Open;
        var access = writable ? FileAccess.ReadWrite : FileAccess.Read;
        var handle = File.OpenHandle(path, mode, access, FileShare.Read);
        if (createWithLength.HasValue)
        {
            RandomAccess.SetLength(handle, createWithLength.Value);
        }

        return new HandleBlockDevice(handle, RandomAccess.GetLength(handle), sectorSize, writable);
    }

    public virtual void Read(long offset, Span<byte> buffer)
    {
        ThrowIfDisposed();
        CheckRange(offset, buffer.Length);
        var done = 0;
        while (done < buffer.Length)
        {
            var n = RandomAccess.Read(Handle, buffer[done..], offset + done);
            if (n <= 0)
            {
                throw new FlashToolException(FlashErrorCode.DeviceIoError, $"Unexpected end of device at offset {offset + done}.");
            }

            done += n;
        }
    }

    public virtual void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (!CanWrite)
        {
            throw new InvalidOperationException("Device is opened read-only.");
        }

        CheckRange(offset, buffer.Length);
        RandomAccess.Write(Handle, buffer, offset);
    }

    public virtual void Flush()
    {
        ThrowIfDisposed();
        if (CanWrite)
        {
            RandomAccess.FlushToDisk(Handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && _ownsHandle)
        {
            Handle.Dispose();
        }
    }

    private void CheckRange(long offset, int count)
    {
        if (offset < 0 || offset % SectorSize != 0 || count % SectorSize != 0)
        {
            throw new ArgumentException($"I/O must be aligned to {SectorSize} bytes (offset {offset}, count {count}).");
        }

        if (offset + count > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"I/O beyond end of device (offset {offset}, count {count}, length {Length}).");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
