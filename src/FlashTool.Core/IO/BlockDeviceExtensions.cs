namespace FlashTool.Core.IO;

public static class BlockDeviceExtensions
{
    /// <summary>Reads an arbitrary (unaligned) byte range; returns null when out of range.</summary>
    public static byte[]? ReadBytes(this IBlockDevice device, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > device.Length)
        {
            return null;
        }

        var start = StreamExtensions.AlignDown(offset, device.SectorSize);
        var end = StreamExtensions.AlignUp(offset + count, device.SectorSize);
        using var buffer = new AlignedBuffer(checked((int)(end - start)));
        device.Read(start, buffer.Span);
        return buffer.Span.Slice((int)(offset - start), count).ToArray();
    }

    /// <summary>Writes an arbitrary (unaligned) byte range using read-modify-write.</summary>
    public static void WriteBytes(this IBlockDevice device, long offset, ReadOnlySpan<byte> data)
    {
        var start = StreamExtensions.AlignDown(offset, device.SectorSize);
        var end = StreamExtensions.AlignUp(offset + data.Length, device.SectorSize);
        using var buffer = new AlignedBuffer(checked((int)(end - start)));
        if (start != offset || end != offset + data.Length)
        {
            device.Read(start, buffer.Span);
        }

        data.CopyTo(buffer.Span[(int)(offset - start)..]);
        device.Write(start, buffer.Span);
    }

    /// <summary>Fills a sector-aligned range with zeros.</summary>
    public static void Zero(this IBlockDevice device, long offset, long length, CancellationToken cancellationToken = default)
    {
        const int chunk = 1024 * 1024;
        using var zeros = new AlignedBuffer((int)Math.Min(chunk, Math.Max(length, device.SectorSize)));
        var end = offset + length;
        while (offset < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(zeros.Size, end - offset);
            device.Write(offset, zeros.Span[..count]);
            offset += count;
        }
    }
}
