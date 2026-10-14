using System.Buffers.Binary;

namespace FlashTool.Core.Containers;

/// <summary>
/// Reads the uncompressed size of an .xz file from its index (stored at the
/// end of every xz stream), without decompressing anything.
/// </summary>
internal static class XzIndexReader
{
    private const int FooterSize = 12;
    private const int MaxIndexSize = 64 * 1024 * 1024;

    public static long? TryGetUncompressedSize(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return null;
        }

        try
        {
            long total = 0;
            var end = stream.Length;
            var footer = new byte[FooterSize];

            while (end > 0)
            {
                // Skip stream padding (multiples of four zero bytes).
                while (end >= 4)
                {
                    var pad = ReadAt(stream, end - 4, 4);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(pad) != 0)
                    {
                        break;
                    }

                    end -= 4;
                }

                if (end < FooterSize * 2)
                {
                    return null;
                }

                footer = ReadAt(stream, end - FooterSize, FooterSize);
                if (footer[10] != (byte)'Y' || footer[11] != (byte)'Z')
                {
                    return null;
                }

                var indexSize = ((long)BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)) + 1) * 4;
                if (indexSize > MaxIndexSize || indexSize > end - FooterSize)
                {
                    return null;
                }

                var indexStart = end - FooterSize - indexSize;
                var index = ReadAt(stream, indexStart, (int)indexSize);
                if (index[0] != 0)
                {
                    return null;
                }

                var pos = 1;
                var records = ReadVarInt(index, ref pos);
                long blocksSize = 0;
                for (ulong i = 0; i < records; i++)
                {
                    var unpadded = ReadVarInt(index, ref pos);
                    var uncompressed = ReadVarInt(index, ref pos);
                    blocksSize += (long)((unpadded + 3) & ~3UL);
                    total += (long)uncompressed;
                }

                var streamSize = FooterSize + blocksSize + indexSize + FooterSize;
                end -= streamSize;
                if (end < 0)
                {
                    return null;
                }

                // Every stream must start with the xz magic.
                if (!ReadAt(stream, end, 6).AsSpan().SequenceEqual(XzContainerHandler.Magic))
                {
                    return null;
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        stream.Position = offset;
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static ulong ReadVarInt(byte[] data, ref int pos)
    {
        ulong value = 0;
        for (var shift = 0; shift < 63; shift += 7)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new OverflowException("Invalid xz variable-length integer.");
    }
}
