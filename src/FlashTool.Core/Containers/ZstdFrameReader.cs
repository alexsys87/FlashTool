using System.Buffers.Binary;

namespace FlashTool.Core.Containers;

/// <summary>
/// Reads the uncompressed size of a .zst file from its frame headers without
/// decompressing anything. Walks every frame (pzstd and zstd --long write
/// several) by skipping over block headers; skippable frames are ignored.
/// </summary>
internal static class ZstdFrameReader
{
    public const uint FrameMagic = 0xFD2FB528;

    private const uint SkippableMagic = 0x184D2A50;
    private const uint SkippableMagicMask = 0xFFFFFFF0;
    private const int MaxBlockSize = 128 * 1024;

    /// <summary>
    /// A zstd frame, or a skippable frame followed by one (pzstd starts every
    /// frame with a skippable frame holding its compressed size).
    /// </summary>
    public static bool IsMatch(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic == FrameMagic)
        {
            return true;
        }

        if ((magic & SkippableMagicMask) != SkippableMagic || header.Length < 8)
        {
            return false;
        }

        // Skippable frames alone are not specific to zstd (lz4 uses the same range).
        var next = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        return next + 4 <= header.Length && BinaryPrimitives.ReadUInt32LittleEndian(header[(int)next..]) == FrameMagic;
    }

    /// <summary>
    /// Sum of the content sizes of all frames, or null when any frame does not
    /// declare its size (streamed input) or the file is not well formed.
    /// </summary>
    public static long? TryGetContentSize(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return null;
        }

        try
        {
            var end = stream.Length;
            var buffer = new byte[8];
            long position = 0;
            long total = 0;
            var frames = 0;

            while (position < end)
            {
                var magic = BinaryPrimitives.ReadUInt32LittleEndian(ReadAt(stream, position, buffer, 4));
                if ((magic & SkippableMagicMask) == SkippableMagic)
                {
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(ReadAt(stream, position + 4, buffer, 4));
                    position += 8L + size;
                    continue;
                }

                if (magic != FrameMagic)
                {
                    return null;
                }

                var descriptor = ReadAt(stream, position + 4, buffer, 1)[0];
                var fcsFlag = descriptor >> 6;
                var singleSegment = (descriptor & 0x20) != 0;
                var hasChecksum = (descriptor & 0x04) != 0;
                if ((descriptor & 0x08) != 0)
                {
                    // Reserved bit must be zero.
                    return null;
                }

                var dictIdSize = (descriptor & 0x03) switch
                {
                    0 => 0,
                    1 => 1,
                    2 => 2,
                    _ => 4,
                };
                var fcsSize = fcsFlag switch
                {
                    0 => singleSegment ? 1 : 0,
                    1 => 2,
                    2 => 4,
                    _ => 8,
                };
                if (fcsSize == 0)
                {
                    return null;
                }

                var fcsOffset = position + 5 + (singleSegment ? 0 : 1) + dictIdSize;
                var fcs = ReadAt(stream, fcsOffset, buffer, fcsSize);
                var contentSize = fcsSize switch
                {
                    1 => fcs[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(fcs) + 256L,
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(fcs),
                    _ => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(fcs)),
                };

                position = fcsOffset + fcsSize;
                bool last;
                do
                {
                    var blockHeader = ReadAt(stream, position, buffer, 3);
                    var value = blockHeader[0] | (blockHeader[1] << 8) | (blockHeader[2] << 16);
                    last = (value & 1) != 0;
                    var blockType = (value >> 1) & 3;
                    var blockSize = value >> 3;
                    if (blockType == 3 || blockSize > MaxBlockSize)
                    {
                        return null;
                    }

                    // An RLE block stores a single byte regardless of its regenerated size.
                    position += 3 + (blockType == 1 ? 1 : blockSize);
                }
                while (!last);

                if (hasChecksum)
                {
                    position += 4;
                }

                total = checked(total + contentSize);
                frames++;
            }

            return frames > 0 && position == end ? total : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        {
            return null;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static Span<byte> ReadAt(Stream stream, long offset, byte[] buffer, int count)
    {
        stream.Position = offset;
        stream.ReadExactly(buffer, 0, count);
        return buffer.AsSpan(0, count);
    }
}
