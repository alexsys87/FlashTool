using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using FlashTool.Core.IO;

namespace FlashTool.Core.Partitioning;

/// <summary>
/// Writes a fresh partition table holding a single partition (used when a
/// device is erased and formatted).
/// </summary>
public static class PartitionTableWriter
{
    public static readonly Guid BasicDataPartitionType = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    private const int GptEntryCount = 128;
    private const int GptEntrySize = 128;

    /// <summary>Largest sector count an MBR can describe.</summary>
    public const long MaxMbrSectors = uint.MaxValue;

    /// <summary>Writes sector 0 with one primary partition.</summary>
    public static void WriteMbr(IBlockDevice device, long firstLba, long sectorCount, byte type, uint diskSignature)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (firstLba + sectorCount > MaxMbrSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "The partition does not fit into an MBR.");
        }

        var sector = new byte[device.SectorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(440), diskSignature);
        WriteMbrEntry(sector, 0, type, firstLba, sectorCount);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        device.WriteBytes(0, sector);
    }

    /// <summary>
    /// Writes a protective MBR, the primary GPT at the start and the backup GPT
    /// at the end of the device, with one partition from <paramref name="firstLba"/>
    /// to <paramref name="lastLba"/> (inclusive).
    /// </summary>
    public static void WriteGpt(IBlockDevice device, long firstLba, long lastLba, Guid typeGuid, string name, Guid diskGuid, Guid partitionGuid)
    {
        ArgumentNullException.ThrowIfNull(device);
        var sectorSize = device.SectorSize;
        var (firstUsable, lastUsable) = GetGptUsableRange(device.Length, sectorSize);
        if (firstLba < firstUsable || lastLba > lastUsable || lastLba < firstLba)
        {
            throw new ArgumentOutOfRangeException(nameof(lastLba), "The partition is outside the usable GPT area.");
        }

        var lastSector = (device.Length / sectorSize) - 1;
        var entriesSectors = EntriesSectors(sectorSize);
        var entries = new byte[entriesSectors * sectorSize];
        var e = entries.AsSpan(0, GptEntrySize);
        typeGuid.TryWriteBytes(e);
        partitionGuid.TryWriteBytes(e[16..]);
        BinaryPrimitives.WriteInt64LittleEndian(e[32..], firstLba);
        BinaryPrimitives.WriteInt64LittleEndian(e[40..], lastLba);
        var nameBytes = Encoding.Unicode.GetBytes(name.Length > 36 ? name[..36] : name);
        nameBytes.CopyTo(e[56..]);

        var primary = new GptHeader
        {
            MyLba = 1,
            AlternateLba = lastSector,
            FirstUsableLba = firstUsable,
            LastUsableLba = lastUsable,
            DiskGuid = diskGuid,
            PartitionEntryLba = 2,
            NumberOfEntries = GptEntryCount,
            SizeOfEntry = GptEntrySize,
            EntriesCrc32 = Crc32.HashToUInt32(entries.AsSpan(0, GptEntryCount * GptEntrySize)),
        };
        var backup = primary.Clone();
        backup.MyLba = lastSector;
        backup.AlternateLba = 1;
        backup.PartitionEntryLba = lastSector - entriesSectors;

        // Backup first, primary next, the protective MBR that makes the disk GPT last.
        device.WriteBytes(backup.PartitionEntryLba * sectorSize, entries);
        device.WriteBytes(lastSector * sectorSize, backup.ToSector(sectorSize));
        device.WriteBytes(2L * sectorSize, entries);
        device.WriteBytes(sectorSize, primary.ToSector(sectorSize));

        var mbr = new byte[sectorSize];
        WriteMbrEntry(mbr, 0, MbrPartitionTable.GptProtectiveType, 1, Math.Min(lastSector, MaxMbrSectors));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        device.WriteBytes(0, mbr);
    }

    /// <summary>First and last LBA a GPT partition may use on a device of this size.</summary>
    public static (long FirstUsable, long LastUsable) GetGptUsableRange(long deviceLength, int sectorSize)
    {
        var entriesSectors = EntriesSectors(sectorSize);
        var lastSector = (deviceLength / sectorSize) - 1;
        return (2 + entriesSectors, lastSector - entriesSectors - 1);
    }

    private static long EntriesSectors(int sectorSize) => ((GptEntryCount * GptEntrySize) + sectorSize - 1) / sectorSize;

    private static void WriteMbrEntry(byte[] sector, int slot, byte type, long firstLba, long sectorCount)
    {
        var e = sector.AsSpan(MbrPartitionTable.EntriesOffset + (slot * MbrPartitionTable.EntrySize), MbrPartitionTable.EntrySize);
        WriteChs(e[1..], firstLba);
        e[4] = type;
        WriteChs(e[5..], firstLba + sectorCount - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)firstLba);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)sectorCount);
    }

    /// <summary>CHS address for a 255-head, 63-sector geometry; FE FF FF beyond its reach.</summary>
    private static void WriteChs(Span<byte> chs, long lba)
    {
        const int heads = 255;
        const int sectors = 63;
        var cylinder = lba / (heads * sectors);
        if (cylinder > 1023)
        {
            chs[0] = 0xFE;
            chs[1] = 0xFF;
            chs[2] = 0xFF;
            return;
        }

        var head = lba / sectors % heads;
        var sector = (lba % sectors) + 1;
        chs[0] = (byte)head;
        chs[1] = (byte)(sector | ((cylinder >> 2) & 0xC0));
        chs[2] = (byte)cylinder;
    }
}
