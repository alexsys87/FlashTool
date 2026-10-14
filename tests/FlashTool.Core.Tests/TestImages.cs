using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace FlashTool.Core.Tests;

/// <summary>Builds small synthetic disk images for tests.</summary>
internal static class TestImages
{
    public const int Sector = 512;

    /// <summary>
    /// MBR image: partition 1 FAT32 (LBA 2048, 2048 sectors), partition 2
    /// ext4 (LBA 4096, <paramref name="secondPartitionSectors"/> sectors).
    /// Remaining bytes are pseudo-random so compression and hashing get real work.
    /// </summary>
    public static byte[] CreateMbrImage(int secondPartitionSectors = 4096, int trailingSectors = 0, int seed = 1)
    {
        var totalSectors = 4096 + secondPartitionSectors + trailingSectors;
        var data = new byte[totalSectors * Sector];
        new Random(seed).NextBytes(data.AsSpan(2048 * Sector));

        data[510] = 0x55;
        data[511] = 0xAA;
        WriteMbrEntry(data, 0, 0x0C, 2048, 2048);
        WriteMbrEntry(data, 1, 0x83, 4096, (uint)secondPartitionSectors);

        WriteFat32BootSector(data.AsSpan(2048 * Sector, Sector), 2048);
        WriteExt4Superblock(data.AsSpan(4096 * Sector, 4096));
        return data;
    }

    /// <summary>GPT image (512-byte sectors) with two partitions and a valid backup GPT at its end.</summary>
    public static byte[] CreateGptImage(long totalSectors = 16384, int seed = 2)
    {
        var data = new byte[totalSectors * Sector];
        new Random(seed).NextBytes(data.AsSpan(2048 * Sector, (int)((totalSectors - 2048 - 33) * Sector)));
        var lastLba = totalSectors - 1;

        // Protective MBR.
        data[510] = 0x55;
        data[511] = 0xAA;
        WriteMbrEntry(data, 0, 0xEE, 1, (uint)lastLba);

        var entries = new byte[128 * 128];
        WriteGptEntry(entries, 0, new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"), 2048, 4095, "EFI");
        WriteGptEntry(entries, 1, new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4"), 4096, lastLba - 34, "root");
        WriteFat32BootSector(data.AsSpan(2048 * Sector, Sector), 2048);
        WriteExt4Superblock(data.AsSpan(4096 * Sector, 4096));

        var entriesCrc = Crc32.HashToUInt32(entries);
        entries.CopyTo(data, 2 * Sector);
        entries.CopyTo(data, (lastLba - 32) * Sector);
        WriteGptHeader(data.AsSpan(Sector, Sector), 1, lastLba, 34, lastLba - 33, 2, entriesCrc);
        WriteGptHeader(data.AsSpan((int)(lastLba * Sector), Sector), lastLba, 1, 34, lastLba - 33, lastLba - 32, entriesCrc);
        return data;
    }

    public static void WriteMbrEntry(byte[] data, int slot, byte type, uint start, uint count)
    {
        var e = data.AsSpan(446 + (slot * 16), 16);
        e[0] = 0;
        e[4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], start);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], count);
    }

    private static void WriteGptEntry(byte[] entries, int index, Guid type, long first, long last, string name)
    {
        var e = entries.AsSpan(index * 128, 128);
        type.TryWriteBytes(e);
        Guid.NewGuid().TryWriteBytes(e[16..]);
        BinaryPrimitives.WriteInt64LittleEndian(e[32..], first);
        BinaryPrimitives.WriteInt64LittleEndian(e[40..], last);
        Encoding.Unicode.GetBytes(name).CopyTo(e[56..]);
    }

    private static void WriteGptHeader(Span<byte> s, long my, long alternate, long firstUsable, long lastUsable, long entriesLba, uint entriesCrc)
    {
        s.Clear();
        "EFI PART"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 92);
        BinaryPrimitives.WriteInt64LittleEndian(s[24..], my);
        BinaryPrimitives.WriteInt64LittleEndian(s[32..], alternate);
        BinaryPrimitives.WriteInt64LittleEndian(s[40..], firstUsable);
        BinaryPrimitives.WriteInt64LittleEndian(s[48..], lastUsable);
        Guid.NewGuid().TryWriteBytes(s[56..]);
        BinaryPrimitives.WriteInt64LittleEndian(s[72..], entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(s[88..], entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], Crc32.HashToUInt32(s[..92]));
    }

    private static void WriteFat32BootSector(Span<byte> s, uint sectors)
    {
        s.Clear();
        s[0] = 0xEB;
        s[1] = 0x58;
        s[2] = 0x90;
        "MSDOS5.0"u8.CopyTo(s[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[11..], 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], sectors);
        "FAT32   "u8.CopyTo(s[82..]);
        s[510] = 0x55;
        s[511] = 0xAA;
    }

    private static void WriteExt4Superblock(Span<byte> s)
    {
        s[..2048].Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(s[(1024 + 56)..], 0xEF53);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(1024 + 92)..], 0x4);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(1024 + 96)..], 0x40 | 0x200);
    }
}

/// <summary>Temporary directory removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flashtool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
