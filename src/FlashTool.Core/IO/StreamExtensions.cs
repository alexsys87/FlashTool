namespace FlashTool.Core.IO;

public static class StreamExtensions
{
    /// <summary>
    /// Reads until <paramref name="buffer"/> is full or the stream ends.
    /// Returns the number of bytes read (less than the buffer size only at EOF).
    /// </summary>
    public static int ReadUpTo(this Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    public static long AlignDown(long value, long alignment) => value / alignment * alignment;
}
