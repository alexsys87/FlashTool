using System.Buffers.Binary;
using System.Numerics;

namespace FlashTool.Core.FileSystems.Ext4;

/// <summary>
/// Checksum primitives with the exact semantics used by the Linux ext4 driver:
/// raw CRC32C updates (no pre/post inversion) and CRC16 (poly 0x8005, reflected).
/// </summary>
internal static class Ext4Checksums
{
    private static readonly ushort[] Crc16Table = BuildCrc16Table();

    /// <summary>Raw CRC32C update, equivalent to the kernel's crc32c_le().</summary>
    public static uint Crc32C(uint crc, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (var b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }

        return crc;
    }

    public static uint Crc32C(uint crc, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return Crc32C(crc, buffer);
    }

    /// <summary>CRC16 as in lib/crc16.c (used by the uninit_bg / gdt_csum feature).</summary>
    public static ushort Crc16(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = (ushort)((crc >> 8) ^ Crc16Table[(crc ^ b) & 0xFF]);
        }

        return crc;
    }

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
