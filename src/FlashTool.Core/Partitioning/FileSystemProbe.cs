using System.Buffers.Binary;

namespace FlashTool.Core.Partitioning;

/// <summary>
/// Detects a file system from the first bytes of a partition.
/// </summary>
public static class FileSystemProbe
{
    /// <summary>Bytes from the partition start needed for a reliable detection.</summary>
    public const int RequiredBytes = 64 * 1024 + 4096;

    public static FileSystemType Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 512)
        {
            return FileSystemType.Unknown;
        }

        if (data.Slice(3, 8).SequenceEqual("NTFS    "u8))
        {
            return FileSystemType.Ntfs;
        }

        if (data.Slice(3, 8).SequenceEqual("EXFAT   "u8))
        {
            return FileSystemType.ExFat;
        }

        if (data.Slice(82, 8).SequenceEqual("FAT32   "u8))
        {
            return FileSystemType.Fat32;
        }

        if (data.Slice(54, 8).SequenceEqual("FAT16   "u8))
        {
            return FileSystemType.Fat16;
        }

        if (data.Slice(54, 8).SequenceEqual("FAT12   "u8))
        {
            return FileSystemType.Fat12;
        }

        if (data[..4].SequenceEqual("XFSB"u8))
        {
            return FileSystemType.Xfs;
        }

        if (data[..4].SequenceEqual("hsqs"u8))
        {
            return FileSystemType.Squashfs;
        }

        // ext2/3/4: superblock at 1024, magic 0xEF53 at offset 56 inside it.
        if (data.Length >= 2048 && BinaryPrimitives.ReadUInt16LittleEndian(data[(1024 + 56)..]) == 0xEF53)
        {
            var compat = BinaryPrimitives.ReadUInt32LittleEndian(data[(1024 + 92)..]);
            var incompat = BinaryPrimitives.ReadUInt32LittleEndian(data[(1024 + 96)..]);
            const uint hasJournal = 0x4;
            const uint extents = 0x40;
            const uint flexBg = 0x200;
            if ((incompat & (extents | flexBg)) != 0)
            {
                return FileSystemType.Ext4;
            }

            return (compat & hasJournal) != 0 ? FileSystemType.Ext3 : FileSystemType.Ext2;
        }

        // F2FS: magic 0xF2F52010 at 1024.
        if (data.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(data[1024..]) == 0xF2F52010)
        {
            return FileSystemType.F2fs;
        }

        if (data.Length >= 4096 && data.Slice(4096 - 10, 10).SequenceEqual("SWAPSPACE2"u8))
        {
            return FileSystemType.Swap;
        }

        // btrfs: "_BHRfS_M" at 64 KiB + 64.
        if (data.Length >= 0x10040 + 8 && data.Slice(0x10040, 8).SequenceEqual("_BHRfS_M"u8))
        {
            return FileSystemType.Btrfs;
        }

        if (data.Length >= 0x8006 && data.Slice(0x8001, 5).SequenceEqual("CD001"u8))
        {
            return FileSystemType.Iso9660;
        }

        return FileSystemType.Unknown;
    }
}
