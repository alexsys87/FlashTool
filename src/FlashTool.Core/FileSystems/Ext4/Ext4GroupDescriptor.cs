using System.Buffers.Binary;

namespace FlashTool.Core.FileSystems.Ext4;

/// <summary>
/// Accessor for one group descriptor inside the descriptor table buffer
/// (32-byte layout, or 64-byte with the "64bit" feature).
/// </summary>
internal readonly ref struct Ext4GroupDescriptor
{
    public const ushort FlagInodeUninit = 0x1;
    public const ushort FlagBlockUninit = 0x2;
    public const ushort FlagInodeTableZeroed = 0x4;

    private readonly Span<byte> _d;

    public Ext4GroupDescriptor(Span<byte> descriptor) => _d = descriptor;

    private bool Wide => _d.Length >= 64;

    public ulong BlockBitmap
    {
        get => Get32(0x00, 0x20);
        set => Set32(0x00, 0x20, value);
    }

    public ulong InodeBitmap
    {
        get => Get32(0x04, 0x24);
        set => Set32(0x04, 0x24, value);
    }

    public ulong InodeTable
    {
        get => Get32(0x08, 0x28);
        set => Set32(0x08, 0x28, value);
    }

    public uint FreeBlocks
    {
        get => Get16(0x0C, 0x2C);
        set => Set16(0x0C, 0x2C, value);
    }

    public uint FreeInodes
    {
        get => Get16(0x0E, 0x2E);
        set => Set16(0x0E, 0x2E, value);
    }

    public uint UsedDirs
    {
        get => Get16(0x10, 0x30);
        set => Set16(0x10, 0x30, value);
    }

    public ushort Flags
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_d[0x12..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(_d[0x12..], value);
    }

    public uint ItableUnused
    {
        get => Get16(0x1C, 0x32);
        set => Set16(0x1C, 0x32, value);
    }

    public void SetBlockBitmapChecksum(uint csum)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_d[0x18..], (ushort)csum);
        if (_d.Length >= 0x3A)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_d[0x38..], (ushort)(csum >> 16));
        }
    }

    public void SetInodeBitmapChecksum(uint csum)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_d[0x1A..], (ushort)csum);
        if (_d.Length >= 0x3C)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_d[0x3A..], (ushort)(csum >> 16));
        }
    }

    /// <summary>Recomputes bg_checksum (metadata_csum or gdt_csum flavor).</summary>
    public void UpdateChecksum(Ext4Superblock sb, uint group)
    {
        const int checksumOffset = 0x1E;
        Span<byte> groupLe = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(groupLe, group);

        ushort checksum;
        if (sb.HasMetadataCsum)
        {
            var crc = Ext4Checksums.Crc32C(sb.ChecksumSeed, groupLe);
            crc = Ext4Checksums.Crc32C(crc, _d[..checksumOffset]);
            crc = Ext4Checksums.Crc32C(crc, [0, 0]);
            if (_d.Length > checksumOffset + 2)
            {
                crc = Ext4Checksums.Crc32C(crc, _d[(checksumOffset + 2)..]);
            }

            checksum = (ushort)crc;
        }
        else if (sb.HasGdtCsum)
        {
            var crc = Ext4Checksums.Crc16(0xFFFF, sb.Uuid);
            crc = Ext4Checksums.Crc16(crc, groupLe);
            crc = Ext4Checksums.Crc16(crc, _d[..checksumOffset]);
            if (sb.Is64Bit && _d.Length > checksumOffset + 2)
            {
                crc = Ext4Checksums.Crc16(crc, _d[(checksumOffset + 2)..]);
            }

            checksum = crc;
        }
        else
        {
            return;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(_d[checksumOffset..], checksum);
    }

    private ulong Get32(int lo, int hi)
        => BinaryPrimitives.ReadUInt32LittleEndian(_d[lo..]) | (Wide ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(_d[hi..]) << 32 : 0);

    private void Set32(int lo, int hi, ulong value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_d[lo..], (uint)value);
        if (Wide)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_d[hi..], (uint)(value >> 32));
        }
        else if (value > uint.MaxValue)
        {
            throw new InvalidOperationException("Block number does not fit a 32-bit descriptor.");
        }
    }

    private uint Get16(int lo, int hi)
        => BinaryPrimitives.ReadUInt16LittleEndian(_d[lo..]) | (Wide ? (uint)BinaryPrimitives.ReadUInt16LittleEndian(_d[hi..]) << 16 : 0);

    private void Set16(int lo, int hi, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_d[lo..], (ushort)value);
        if (Wide)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_d[hi..], (ushort)(value >> 16));
        }
    }
}
