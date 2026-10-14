using System.Buffers.Binary;

namespace FlashTool.Core.Partitioning;

/// <summary>
/// Classic DOS/MBR partition table.
/// </summary>
public sealed class MbrPartitionTable
{
    public const int SectorSize = 512;
    public const int EntriesOffset = 446;
    public const int EntrySize = 16;
    public const byte GptProtectiveType = 0xEE;

    private MbrPartitionTable(IReadOnlyList<MbrEntry> entries, uint diskSignature)
    {
        Entries = entries;
        DiskSignature = diskSignature;
    }

    /// <summary>The four primary slots (empty slots are skipped).</summary>
    public IReadOnlyList<MbrEntry> Entries { get; }

    public uint DiskSignature { get; }

    public bool IsProtectiveMbr => Entries.Any(e => e.Type == GptProtectiveType);

    public static bool HasBootSignature(ReadOnlySpan<byte> sector0)
        => sector0.Length >= SectorSize && sector0[510] == 0x55 && sector0[511] == 0xAA;

    /// <summary>
    /// Parses sector 0. Returns null when the sector does not look like a
    /// plausible MBR (for instance a FAT boot sector of a "superfloppy").
    /// </summary>
    /// <param name="sector0">The first 512 bytes.</param>
    /// <param name="lenient">
    /// Accept partitions that start at LBA 0 or overlap each other. Hybrid ISO
    /// images legitimately do both, so this is enabled when an ISO 9660 volume is present.
    /// </param>
    public static MbrPartitionTable? TryParse(ReadOnlySpan<byte> sector0, bool lenient = false)
    {
        if (!HasBootSignature(sector0))
        {
            return null;
        }

        var entries = new List<MbrEntry>(4);
        for (var i = 0; i < 4; i++)
        {
            var e = sector0.Slice(EntriesOffset + (i * EntrySize), EntrySize);
            var status = e[0];
            var type = e[4];
            var start = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);

            if (type == 0 && start == 0 && count == 0)
            {
                continue;
            }

            // Status must be 0x00 or 0x80; anything else means this is not a partition table
            // (e.g. boot code or a BPB overlapping the table area).
            if (status != 0x00 && status != 0x80)
            {
                return null;
            }

            if (type == 0 || count == 0 || (start == 0 && !lenient))
            {
                return null;
            }

            entries.Add(new MbrEntry(i, status == 0x80, type, start, count));
        }

        if (entries.Count == 0)
        {
            return null;
        }

        // Overlapping primary partitions are a strong sign of garbage.
        var sorted = entries.OrderBy(e => e.FirstLba).ToList();
        for (var i = 1; i < sorted.Count && !lenient; i++)
        {
            if (sorted[i].FirstLba < sorted[i - 1].EndLba && sorted[i].Type != GptProtectiveType)
            {
                return null;
            }
        }

        return new MbrPartitionTable(entries, BinaryPrimitives.ReadUInt32LittleEndian(sector0[440..]));
    }

    public static bool IsExtendedType(byte type) => type is 0x05 or 0x0F or 0x85;

    /// <summary>
    /// Builds the partition list. Logical partitions are resolved when
    /// <paramref name="readSector"/> can provide the EBR sectors.
    /// </summary>
    public IReadOnlyList<PartitionInfo> GetPartitions(Func<long, byte[]?>? readSector = null)
    {
        var result = new List<PartitionInfo>();
        foreach (var e in Entries)
        {
            var extended = IsExtendedType(e.Type);
            result.Add(new PartitionInfo(
                e.Slot + 1,
                (long)e.FirstLba * SectorSize,
                (long)e.SectorCount * SectorSize,
                DescribeType(e.Type),
                IsExtendedContainer: extended)
            {
                MbrType = e.Type,
            });

            if (extended && readSector != null)
            {
                result.AddRange(ReadLogicalPartitions(e.FirstLba, readSector));
            }
        }

        return result;
    }

    private static List<PartitionInfo> ReadLogicalPartitions(uint extendedStart, Func<long, byte[]?> readSector)
    {
        var result = new List<PartitionInfo>();
        long ebrLba = extendedStart;
        var number = 5;
        var visited = new HashSet<long>();

        while (number < 5 + 128 && visited.Add(ebrLba))
        {
            var ebr = readSector(ebrLba);
            if (ebr == null || !HasBootSignature(ebr))
            {
                break;
            }

            var first = ebr.AsSpan(EntriesOffset, EntrySize);
            var type = first[4];
            var start = BinaryPrimitives.ReadUInt32LittleEndian(first[8..]);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(first[12..]);
            if (type != 0 && count != 0)
            {
                result.Add(new PartitionInfo(
                    number++,
                    (ebrLba + start) * SectorSize,
                    (long)count * SectorSize,
                    DescribeType(type),
                    IsLogical: true)
                {
                    MbrType = type,
                });
            }

            var next = ebr.AsSpan(EntriesOffset + EntrySize, EntrySize);
            var nextStart = BinaryPrimitives.ReadUInt32LittleEndian(next[8..]);
            if (next[4] == 0 || nextStart == 0)
            {
                break;
            }

            ebrLba = extendedStart + nextStart;
        }

        return result;
    }

    public static string DescribeType(byte type) => type switch
    {
        0x01 => "FAT12",
        0x04 or 0x06 => "FAT16",
        0x0E => "FAT16 LBA",
        0x05 => "Extended",
        0x0F => "Extended LBA",
        0x07 => "NTFS/exFAT",
        0x0B => "FAT32",
        0x0C => "FAT32 LBA",
        0x11 or 0x14 or 0x16 or 0x1B or 0x1C or 0x1E => "Hidden FAT",
        0x17 => "Hidden NTFS",
        0x27 => "Windows RE",
        0x82 => "Linux swap",
        0x83 => "Linux",
        0x85 => "Linux extended",
        0x8E => "Linux LVM",
        0xA5 => "FreeBSD",
        0xA6 => "OpenBSD",
        0xA9 => "NetBSD",
        0xAF => "HFS+",
        0xDA => "Non-FS data",
        0xEE => "GPT protective",
        0xEF => "EFI System",
        0xFD => "Linux RAID",
        _ => $"0x{type:X2}",
    };
}

/// <summary>A primary MBR slot.</summary>
public sealed record MbrEntry(int Slot, bool Bootable, byte Type, uint FirstLba, uint SectorCount)
{
    public long EndLba => (long)FirstLba + SectorCount;
}
