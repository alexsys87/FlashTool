using FlashTool.Core.IO;

namespace FlashTool.Core.FileSystems;

/// <summary>
/// Byte-addressed view of one partition on a block device. Unaligned
/// accesses are handled with read-modify-write of whole sectors.
/// </summary>
internal sealed class PartitionAccess
{
    private const int MaxChunk = 4 * 1024 * 1024;

    private readonly IBlockDevice _device;

    public PartitionAccess(IBlockDevice device, long offset, long length)
    {
        _device = device;
        Offset = offset;
        Length = length;
    }

    public long Offset { get; }

    public long Length { get; }

    public byte[] Read(long position, int count)
    {
        CheckRange(position, count);
        return _device.ReadBytes(Offset + position, count)
            ?? throw new FlashToolException(FlashErrorCode.DeviceIoError, "Read beyond the end of the device.");
    }

    public void Write(long position, ReadOnlySpan<byte> data)
    {
        CheckRange(position, data.Length);
        for (var done = 0; done < data.Length; done += MaxChunk)
        {
            var chunk = data.Slice(done, Math.Min(MaxChunk, data.Length - done));
            _device.WriteBytes(Offset + position + done, chunk);
        }
    }

    public void Zero(long position, long count, CancellationToken cancellationToken)
    {
        CheckRange(position, count);
        var zeros = new byte[(int)Math.Min(MaxChunk, count)];
        for (long done = 0; done < count; done += zeros.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = (int)Math.Min(zeros.Length, count - done);
            _device.WriteBytes(Offset + position + done, zeros.AsSpan(0, n));
        }
    }

    public void Flush() => _device.Flush();

    private void CheckRange(long position, long count)
    {
        if (position < 0 || count < 0 || position + count > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), $"Access outside the partition ({position}+{count} > {Length}).");
        }
    }
}
