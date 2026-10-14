using System.Buffers.Binary;

namespace FlashTool.Core.FileSystems.Ext4;

/// <summary>
/// The fields of the ext2/3/4 superblock needed for resizing, kept on top of
/// the raw 1024 bytes so that unknown fields are preserved verbatim.
/// </summary>
internal sealed class Ext4Superblock
{
    public const int Size = 1024;
    public const ushort Magic = 0xEF53;

    // Feature flags.
    public const uint CompatHasJournal = 0x4;
    public const uint CompatResizeInode = 0x10;
    public const uint CompatExcludeBitmap = 0x100;
    public const uint CompatSparseSuper2 = 0x200;

    public const uint IncompatRecover = 0x4;
    public const uint IncompatJournalDev = 0x8;
    public const uint IncompatMetaBg = 0x10;
    public const uint Incompat64Bit = 0x80;
    public const uint IncompatMmp = 0x100;
    public const uint IncompatCsumSeed = 0x2000;

    public const uint RoCompatSparseSuper = 0x1;
    public const uint RoCompatGdtCsum = 0x10;
    public const uint RoCompatBigAlloc = 0x200;
    public const uint RoCompatMetadataCsum = 0x400;
    public const uint RoCompatOrphanPresent = 0x10000;

    private readonly byte[] _raw;

    public Ext4Superblock(byte[] raw)
    {
        if (raw.Length != Size)
        {
            throw new ArgumentException("A superblock is 1024 bytes.", nameof(raw));
        }

        _raw = raw;
    }

    public ReadOnlySpan<byte> Raw => _raw;

    public bool IsValidMagic => U16(0x38) == Magic;

    public uint InodesCount
    {
        get => U32(0x00);
        set => SetU32(0x00, value);
    }

    public ulong BlocksCount
    {
        get => U32(0x04) | (Is64Bit ? (ulong)U32(0x150) << 32 : 0);
        set => SetSplit(0x04, 0x150, value);
    }

    public ulong ReservedBlocksCount
    {
        get => U32(0x08) | (Is64Bit ? (ulong)U32(0x154) << 32 : 0);
        set => SetSplit(0x08, 0x154, value);
    }

    public ulong FreeBlocksCount
    {
        get => U32(0x0C) | (Is64Bit ? (ulong)U32(0x158) << 32 : 0);
        set => SetSplit(0x0C, 0x158, value);
    }

    public uint FreeInodesCount
    {
        get => U32(0x10);
        set => SetU32(0x10, value);
    }

    public uint FirstDataBlock => U32(0x14);

    public int BlockSize => 1024 << (int)U32(0x18);

    public uint BlocksPerGroup => U32(0x20);

    public uint ClustersPerGroup => U32(0x24);

    public uint InodesPerGroup => U32(0x28);

    public ushort State => U16(0x3A);

    public int InodeSize => U32(0x4C) == 0 ? 128 : U16(0x58);

    public ushort BlockGroupNumber
    {
        get => U16(0x5A);
        set => SetU16(0x5A, value);
    }

    public uint FeatureCompat => U32(0x5C);

    public uint FeatureIncompat => U32(0x60);

    public uint FeatureRoCompat => U32(0x64);

    public ReadOnlySpan<byte> Uuid => _raw.AsSpan(0x68, 16);

    public ushort ReservedGdtBlocks
    {
        get => (FeatureCompat & CompatResizeInode) != 0 ? U16(0xCE) : (ushort)0;
        set => SetU16(0xCE, value);
    }

    public int DescriptorSize => Is64Bit ? Math.Max((int)U16(0xFE), 64) : 32;

    public uint OverheadClusters
    {
        get => U32(0x248);
        set => SetU32(0x248, value);
    }

    public byte ChecksumType => _raw[0x175];

    public bool Is64Bit => (FeatureIncompat & Incompat64Bit) != 0;

    public bool HasMetadataCsum => (FeatureRoCompat & RoCompatMetadataCsum) != 0;

    public bool HasGdtCsum => (FeatureRoCompat & RoCompatGdtCsum) != 0;

    public bool HasSparseSuper => (FeatureRoCompat & RoCompatSparseSuper) != 0;

    public bool HasResizeInode => (FeatureCompat & CompatResizeInode) != 0;

    /// <summary>Seed for all metadata_csum checksums.</summary>
    public uint ChecksumSeed => (FeatureIncompat & IncompatCsumSeed) != 0
        ? U32(0x270)
        : Ext4Checksums.Crc32C(0xFFFFFFFF, Uuid);

    public Ext4Superblock Clone() => new((byte[])_raw.Clone());

    /// <summary>Returns the raw bytes with an up-to-date checksum.</summary>
    public byte[] Serialize()
    {
        if (HasMetadataCsum)
        {
            SetU32(0x3FC, Ext4Checksums.Crc32C(0xFFFFFFFF, _raw.AsSpan(0, 0x3FC)));
        }

        return (byte[])_raw.Clone();
    }

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_raw.AsSpan(offset));

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_raw.AsSpan(offset));

    private void SetU16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(_raw.AsSpan(offset), value);

    private void SetU32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_raw.AsSpan(offset), value);

    private void SetSplit(int lo, int hi, ulong value)
    {
        if (!Is64Bit && value > uint.MaxValue)
        {
            throw new InvalidOperationException("Value does not fit a 32-bit file system.");
        }

        SetU32(lo, (uint)value);
        if (Is64Bit)
        {
            SetU32(hi, (uint)(value >> 32));
        }
    }
}
