using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace FlashTool.Core.Partitioning;

/// <summary>
/// GUID Partition Table header (UEFI spec, chapter 5.3).
/// </summary>
public sealed class GptHeader
{
    public static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    public uint Revision { get; set; } = 0x00010000;

    public uint HeaderSize { get; set; } = 92;

    public long MyLba { get; set; }

    public long AlternateLba { get; set; }

    public long FirstUsableLba { get; set; }

    public long LastUsableLba { get; set; }

    public Guid DiskGuid { get; set; }

    public long PartitionEntryLba { get; set; }

    public uint NumberOfEntries { get; set; }

    public uint SizeOfEntry { get; set; }

    public uint EntriesCrc32 { get; set; }

    public int EntriesByteLength => checked((int)(NumberOfEntries * SizeOfEntry));

    public static bool HasSignature(ReadOnlySpan<byte> sector) => sector.Length >= 92 && sector[..8].SequenceEqual(Signature);

    /// <summary>Parses a header sector. Returns null when the signature or CRC is invalid.</summary>
    public static GptHeader? TryParse(ReadOnlySpan<byte> sector)
    {
        if (!HasSignature(sector))
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sector[12..]);
        if (headerSize < 92 || headerSize > sector.Length)
        {
            return null;
        }

        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector[16..]);
        var copy = sector[..(int)headerSize].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(16), 0);
        if (Crc32.HashToUInt32(copy) != storedCrc)
        {
            return null;
        }

        var header = new GptHeader
        {
            Revision = BinaryPrimitives.ReadUInt32LittleEndian(sector[8..]),
            HeaderSize = headerSize,
            MyLba = BinaryPrimitives.ReadInt64LittleEndian(sector[24..]),
            AlternateLba = BinaryPrimitives.ReadInt64LittleEndian(sector[32..]),
            FirstUsableLba = BinaryPrimitives.ReadInt64LittleEndian(sector[40..]),
            LastUsableLba = BinaryPrimitives.ReadInt64LittleEndian(sector[48..]),
            DiskGuid = new Guid(sector.Slice(56, 16)),
            PartitionEntryLba = BinaryPrimitives.ReadInt64LittleEndian(sector[72..]),
            NumberOfEntries = BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]),
            SizeOfEntry = BinaryPrimitives.ReadUInt32LittleEndian(sector[84..]),
            EntriesCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(sector[88..]),
        };

        if (header.SizeOfEntry < 128 || header.SizeOfEntry % 8 != 0 || header.NumberOfEntries == 0 || header.NumberOfEntries > 4096)
        {
            return null;
        }

        return header;
    }

    /// <summary>Serializes the header into a full sector with a freshly computed CRC.</summary>
    public byte[] ToSector(int sectorSize)
    {
        var buf = new byte[sectorSize];
        var s = buf.AsSpan();
        Signature.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 92);
        BinaryPrimitives.WriteInt64LittleEndian(s[24..], MyLba);
        BinaryPrimitives.WriteInt64LittleEndian(s[32..], AlternateLba);
        BinaryPrimitives.WriteInt64LittleEndian(s[40..], FirstUsableLba);
        BinaryPrimitives.WriteInt64LittleEndian(s[48..], LastUsableLba);
        DiskGuid.TryWriteBytes(s.Slice(56, 16));
        BinaryPrimitives.WriteInt64LittleEndian(s[72..], PartitionEntryLba);
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], NumberOfEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], SizeOfEntry);
        BinaryPrimitives.WriteUInt32LittleEndian(s[88..], EntriesCrc32);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], Crc32.HashToUInt32(s[..92]));
        return buf;
    }

    public GptHeader Clone() => (GptHeader)MemberwiseClone();
}

/// <summary>One GPT partition entry.</summary>
public sealed record GptEntry(int Index, Guid TypeGuid, Guid UniqueGuid, long FirstLba, long LastLba, ulong Attributes, string Name)
{
    public bool IsUsed => TypeGuid != Guid.Empty;
}

/// <summary>
/// A parsed GPT: header plus partition entry array.
/// </summary>
public sealed class GptPartitionTable
{
    public GptPartitionTable(GptHeader header, byte[] entriesRaw, int sectorSize)
    {
        Header = header;
        EntriesRaw = entriesRaw;
        SectorSize = sectorSize;
        Entries = ParseEntries(header, entriesRaw);
    }

    public GptHeader Header { get; }

    /// <summary>Raw partition entry array (kept to rewrite it byte-exact).</summary>
    public byte[] EntriesRaw { get; }

    public int SectorSize { get; }

    public IReadOnlyList<GptEntry> Entries { get; private set; }

    public bool EntriesCrcValid => Crc32.HashToUInt32(EntriesRaw) == Header.EntriesCrc32;

    public int EntriesSectorCount => (EntriesRaw.Length + SectorSize - 1) / SectorSize;

    /// <summary>
    /// Locates and parses the primary GPT. <paramref name="read"/> returns
    /// <c>count</c> bytes at a byte offset, or null when out of range.
    /// The sector size is detected by probing LBA 1 at 512 and 4096 bytes.
    /// </summary>
    public static GptPartitionTable? TryRead(Func<long, int, byte[]?> read, int? sectorSizeHint = null)
    {
        int[] candidates = sectorSizeHint is { } hint ? [hint] : [512, 4096];
        foreach (var sectorSize in candidates)
        {
            var headerSector = read(sectorSize, sectorSize);
            if (headerSector == null)
            {
                continue;
            }

            var header = GptHeader.TryParse(headerSector);
            if (header == null || header.MyLba != 1)
            {
                continue;
            }

            var entries = read(header.PartitionEntryLba * sectorSize, header.EntriesByteLength);
            if (entries == null)
            {
                continue;
            }

            return new GptPartitionTable(header, entries, sectorSize);
        }

        return null;
    }

    public void SetEntryLastLba(int index, long lastLba)
    {
        var offset = index * (int)Header.SizeOfEntry;
        BinaryPrimitives.WriteInt64LittleEndian(EntriesRaw.AsSpan(offset + 40), lastLba);
        Header.EntriesCrc32 = Crc32.HashToUInt32(EntriesRaw);
        Entries = ParseEntries(Header, EntriesRaw);
    }

    public void RecomputeEntriesCrc() => Header.EntriesCrc32 = Crc32.HashToUInt32(EntriesRaw);

    public IReadOnlyList<PartitionInfo> GetPartitions()
        => Entries.Where(e => e.IsUsed)
            .Select(e => new PartitionInfo(
                e.Index + 1,
                e.FirstLba * SectorSize,
                (e.LastLba - e.FirstLba + 1) * SectorSize,
                DescribeType(e.TypeGuid),
                e.Name)
            {
                GptType = e.TypeGuid,
            })
            .ToList();

    private static List<GptEntry> ParseEntries(GptHeader header, byte[] raw)
    {
        var list = new List<GptEntry>((int)header.NumberOfEntries);
        var size = (int)header.SizeOfEntry;
        for (var i = 0; i < header.NumberOfEntries && (i + 1) * size <= raw.Length; i++)
        {
            var e = raw.AsSpan(i * size, size);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty)
            {
                continue;
            }

            var name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
            list.Add(new GptEntry(
                i,
                type,
                new Guid(e.Slice(16, 16)),
                BinaryPrimitives.ReadInt64LittleEndian(e[32..]),
                BinaryPrimitives.ReadInt64LittleEndian(e[40..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[48..]),
                name));
        }

        return list;
    }

    private static readonly Dictionary<Guid, string> KnownTypes = new()
    {
        [new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B")] = "EFI System",
        [new Guid("21686148-6449-6E6F-744E-656564454649")] = "BIOS boot",
        [new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")] = "Microsoft basic data",
        [new Guid("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")] = "Microsoft reserved",
        [new Guid("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")] = "Windows RE",
        [new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4")] = "Linux filesystem",
        [new Guid("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F")] = "Linux swap",
        [new Guid("E6D6D379-F507-44C2-A23C-238F2A3DF928")] = "Linux LVM",
        [new Guid("A19D880F-05FC-4D3B-A006-743F0F84911E")] = "Linux RAID",
        [new Guid("4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709")] = "Linux root (x86-64)",
        [new Guid("B921B045-1DF0-41C3-AF44-4C6F280D3FAE")] = "Linux root (ARM64)",
        [new Guid("69DAD710-2CE4-4E3C-B16C-21A1D49ABED3")] = "Linux root (ARM)",
        [new Guid("BC13C2FF-59E6-4262-A352-B275FD6F7172")] = "Linux extended boot",
        [new Guid("933AC7E1-2EB4-4F13-B844-0E14E2AEF915")] = "Linux home",
        [new Guid("48465300-0000-11AA-AA11-00306543ECAC")] = "Apple HFS+",
        [new Guid("7C3457EF-0000-11AA-AA11-00306543ECAC")] = "Apple APFS",
        [new Guid("FE3A2A5D-4F32-41A7-B725-ACCC3285A309")] = "ChromeOS kernel",
        [new Guid("3CB8E202-3B7E-47DD-8A3C-7FF2A13CFCEC")] = "ChromeOS root",
    };

    public static string DescribeType(Guid type) => KnownTypes.TryGetValue(type, out var name) ? name : type.ToString("D").ToUpperInvariant();
}
