using System.Buffers.Binary;
using FlashTool.Core.Operations;

namespace FlashTool.Core.FileSystems.Ext4;

/// <summary>
/// Finds the free blocks of an ext2/3/4 file system from its block bitmaps,
/// so that creating an image can store them as zeros without reading them.
/// Strictly read-only and conservative: anything that is not provably free
/// (metadata, journal, groups whose bitmap cannot be trusted) counts as used,
/// and a file system that is not cleanly unmounted or whose checksums do not
/// match is not used at all.
/// </summary>
internal sealed class Ext4FreeSpace
{
    private const int SuperblockOffset = 1024;
    private const int MaxDescriptorTableBytes = 64 * 1024 * 1024;
    private const int MaxBitmapBatchBytes = 1024 * 1024;

    private readonly PartitionAccess _volume;
    private readonly Ext4Superblock _sb;
    private readonly int _bs;
    private readonly long _fdb;
    private readonly long _bpg;
    private readonly long _blocks;
    private readonly long _groups;
    private readonly int _descSize;
    private readonly int _descPerBlock;
    private readonly long _minRunBlocks;
    private readonly List<(long Offset, long Length)> _runs = [];
    private long _runStart = -1;

    private Ext4FreeSpace(PartitionAccess volume, Ext4Superblock sb, long minRunBytes)
    {
        _volume = volume;
        _sb = sb;
        _bs = sb.BlockSize;
        _fdb = sb.FirstDataBlock;
        _bpg = sb.BlocksPerGroup;
        _blocks = (long)sb.BlocksCount;
        _groups = (_blocks - _fdb + _bpg - 1) / _bpg;
        _descSize = sb.DescriptorSize;
        _descPerBlock = _bs / _descSize;
        _minRunBlocks = Math.Max(1, (minRunBytes + _bs - 1) / _bs);
    }

    /// <summary>Uninitialized groups (uninit_bg/metadata_csum) have no bitmap on disk.</summary>
    private bool HonorsBlockUninit => _sb.HasMetadataCsum || _sb.HasGdtCsum;

    /// <summary>
    /// Free runs of at least <paramref name="minRunBytes"/>, as byte ranges
    /// relative to the start of the volume, in ascending order. Empty unless
    /// the status is <see cref="FreeSpaceStatus.Skipped"/>.
    /// </summary>
    public static (FreeSpaceStatus Status, List<(long Offset, long Length)> Runs) Scan(PartitionAccess volume, long minRunBytes, CancellationToken cancellationToken)
    {
        try
        {
            var sb = new Ext4Superblock(volume.Read(SuperblockOffset, Ext4Superblock.Size));
            var refusal = Check(sb, volume.Length);
            if (refusal != FreeSpaceStatus.Skipped)
            {
                return (refusal, []);
            }

            var scanner = new Ext4FreeSpace(volume, sb, minRunBytes);
            var status = scanner.Run(cancellationToken);
            return (status, status == FreeSpaceStatus.Skipped ? scanner._runs : []);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A block number pointed outside the partition.
            return (FreeSpaceStatus.InconsistentMetadata, []);
        }
    }

    private static FreeSpaceStatus Check(Ext4Superblock sb, long volumeLength)
    {
        const ushort validFs = 0x1;
        const ushort errorFs = 0x2;
        if (!sb.IsValidMagic || !sb.IsChecksumValid)
        {
            return FreeSpaceStatus.InconsistentMetadata;
        }

        if ((sb.State & validFs) == 0 || (sb.State & errorFs) != 0)
        {
            return FreeSpaceStatus.NotCleanlyUnmounted;
        }

        // Until the journal is replayed the bitmaps on disk may not show blocks that are already in use.
        if ((sb.FeatureIncompat & Ext4Superblock.IncompatRecover) != 0)
        {
            return FreeSpaceStatus.JournalNeedsRecovery;
        }

        var bs = sb.BlockSize;
        var descSize = sb.DescriptorSize;
        var unsupported =
            (sb.FeatureIncompat & Ext4Superblock.IncompatJournalDev) != 0
            || (sb.FeatureRoCompat & Ext4Superblock.RoCompatBigAlloc) != 0
            || (sb.HasMetadataCsum && sb.ChecksumType != 1)
            || bs is < 1024 or > 65536
            || sb.BlocksPerGroup == 0 || sb.BlocksPerGroup > bs * 8 || sb.BlocksPerGroup % 8 != 0
            || sb.ClustersPerGroup != sb.BlocksPerGroup
            || descSize is < 32 or > 1024 || (descSize & (descSize - 1)) != 0;
        if (unsupported)
        {
            return FreeSpaceStatus.UnsupportedFeature;
        }

        var blocks = sb.BlocksCount;
        if (blocks == 0 || sb.FirstDataBlock >= blocks || blocks > (ulong)(volumeLength / bs))
        {
            // Also a file system larger than its partition (a truncated copy).
            return FreeSpaceStatus.InconsistentMetadata;
        }

        var groups = (blocks - sb.FirstDataBlock + sb.BlocksPerGroup - 1) / sb.BlocksPerGroup;
        return groups * (ulong)descSize > MaxDescriptorTableBytes ? FreeSpaceStatus.UnsupportedFeature : FreeSpaceStatus.Skipped;
    }

    private FreeSpaceStatus Run(CancellationToken ct)
    {
        var gdt = ReadDescriptorTable();
        if (!DescriptorsAreValid(gdt))
        {
            return FreeSpaceStatus.InconsistentMetadata;
        }

        // Bitmaps, inode bitmaps and inode tables of every group, for groups whose bitmap is not on disk.
        var metadata = CollectMetadata(gdt);
        if (metadata == null)
        {
            return FreeSpaceStatus.InconsistentMetadata;
        }

        var bitmapBytes = (int)(_bpg / 8);
        var group = 0L;
        while (group < _groups)
        {
            ct.ThrowIfCancellationRequested();
            if (IsBlockUninit(gdt, group))
            {
                ScanGroup(group, BuildUninitBitmap(group, metadata));
                group++;
                continue;
            }

            // With flex_bg the bitmaps of neighbouring groups are adjacent: read them in one go.
            var first = (long)Descriptor(gdt, group).BlockBitmap;
            var count = 1;
            while (group + count < _groups
                && count * _bs < MaxBitmapBatchBytes
                && !IsBlockUninit(gdt, group + count)
                && (long)Descriptor(gdt, group + count).BlockBitmap == first + count)
            {
                count++;
            }

            var batch = _volume.Read(first * _bs, count * _bs);
            for (var i = 0; i < count; i++)
            {
                var bitmap = batch.AsSpan(i * _bs, bitmapBytes);
                if (!BitmapChecksumMatches(Descriptor(gdt, group + i), bitmap))
                {
                    return FreeSpaceStatus.InconsistentMetadata;
                }

                ScanGroup(group + i, bitmap);
            }

            group += count;
        }

        EndRun(_blocks);
        return FreeSpaceStatus.Skipped;
    }

    private byte[] ReadDescriptorTable()
    {
        var tableBlocks = (_groups + _descPerBlock - 1) / _descPerBlock;
        var gdt = new byte[tableBlocks * _bs];
        if (!_sb.HasMetaBg)
        {
            _volume.Read((_fdb + 1) * _bs, gdt.Length).CopyTo(gdt, 0);
            return gdt;
        }

        // meta_bg: past s_first_meta_bg each descriptor block lives in the first group of its meta group.
        for (var d = 0L; d < tableBlocks; d++)
        {
            long block;
            if (d < _sb.FirstMetaBg)
            {
                block = _fdb + 1 + d;
            }
            else
            {
                var owner = d * _descPerBlock;
                block = _fdb + (owner * _bpg) + (_sb.HasBackupInGroup(owner) ? 1 : 0);
            }

            _volume.Read(block * _bs, _bs).CopyTo(gdt, (int)(d * _bs));
        }

        return gdt;
    }

    /// <summary>Descriptor checksums match (nothing to check without uninit_bg/metadata_csum).</summary>
    private bool DescriptorsAreValid(byte[] gdt)
    {
        if (!HonorsBlockUninit)
        {
            return true;
        }

        Span<byte> copy = stackalloc byte[_descSize];
        for (var g = 0L; g < _groups; g++)
        {
            var descriptor = gdt.AsSpan((int)(g * _descSize), _descSize);
            descriptor.CopyTo(copy);
            new Ext4GroupDescriptor(copy).UpdateChecksum(_sb, (uint)g);
            if (!copy.SequenceEqual(descriptor))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Metadata extents per group they touch; null when one points outside the file system.</summary>
    private Dictionary<long, List<(long Start, long End)>>? CollectMetadata(byte[] gdt)
    {
        var inodeTableBlocks = (((long)_sb.InodesPerGroup * _sb.InodeSize) + _bs - 1) / _bs;
        var byGroup = new Dictionary<long, List<(long, long)>>();
        for (var g = 0L; g < _groups; g++)
        {
            var desc = Descriptor(gdt, g);
            foreach (var (start, length) in new[] { ((long)desc.BlockBitmap, 1L), ((long)desc.InodeBitmap, 1L), ((long)desc.InodeTable, inodeTableBlocks) })
            {
                if (start < _fdb || start + length > _blocks)
                {
                    return null;
                }

                for (var owner = GroupOf(start); owner <= GroupOf(start + length - 1); owner++)
                {
                    if (!byGroup.TryGetValue(owner, out var list))
                    {
                        byGroup[owner] = list = [];
                    }

                    list.Add((start, start + length));
                }
            }
        }

        return byGroup;
    }

    /// <summary>
    /// What the kernel assumes for a BLOCK_UNINIT group (ext4_init_block_bitmap):
    /// free except its superblock and descriptor blocks, plus any group's
    /// bitmaps and inode table that happen to lie in it.
    /// </summary>
    private byte[] BuildUninitBitmap(long group, Dictionary<long, List<(long Start, long End)>> metadata)
    {
        var bitmap = new byte[_bpg / 8];
        var start = GroupStart(group);
        SetBits(bitmap, 0, Math.Min(_bpg, BaseMetadataBlocks(group)));
        if (metadata.TryGetValue(group, out var extents))
        {
            foreach (var (from, to) in extents)
            {
                SetBits(bitmap, Math.Max(from, start) - start, Math.Min(to, start + _bpg) - start);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Blocks at the start of a group taken by the superblock and descriptor
    /// copies, as ext4_num_base_meta_clusters counts them.
    /// </summary>
    private long BaseMetadataBlocks(long group)
    {
        var hasSuper = _sb.HasBackupInGroup(group) ? 1L : 0L;
        if (!_sb.HasMetaBg || group < (long)_sb.FirstMetaBg * _descPerBlock)
        {
            var tableBlocks = _sb.HasMetaBg ? _sb.FirstMetaBg : (_groups + _descPerBlock - 1) / _descPerBlock;
            return hasSuper == 0 ? 0 : 1 + tableBlocks + _sb.ReservedGdtBlocks;
        }

        // meta_bg: the descriptor block of a meta group sits in its first, second and last group.
        var first = group / _descPerBlock * _descPerBlock;
        var holdsDescriptors = group == first || group == first + 1 || group == first + _descPerBlock - 1;
        return hasSuper + (holdsDescriptors ? 1 : 0);
    }

    private void ScanGroup(long group, ReadOnlySpan<byte> bitmap)
    {
        var start = GroupStart(group);
        var count = Math.Min(_bpg, _blocks - start);
        var bit = 0L;
        while (bit < count)
        {
            // Whole bytes first: most of a bitmap is either all used or all free.
            if ((bit & 7) == 0 && bit + 8 <= count)
            {
                var b = bitmap[(int)(bit >> 3)];
                if (b == 0x00)
                {
                    StartRun(start + bit);
                    bit += 8;
                    continue;
                }

                if (b == 0xFF)
                {
                    EndRun(start + bit);
                    bit += 8;
                    continue;
                }
            }

            if ((bitmap[(int)(bit >> 3)] & (1 << (int)(bit & 7))) == 0)
            {
                StartRun(start + bit);
            }
            else
            {
                EndRun(start + bit);
            }

            bit++;
        }
    }

    private void StartRun(long block)
    {
        if (_runStart < 0)
        {
            _runStart = block;
        }
    }

    private void EndRun(long block)
    {
        if (_runStart >= 0 && block - _runStart >= _minRunBlocks)
        {
            _runs.Add((_runStart * _bs, (block - _runStart) * _bs));
        }

        _runStart = -1;
    }

    private bool BitmapChecksumMatches(Ext4GroupDescriptor desc, ReadOnlySpan<byte> bitmap)
    {
        if (!_sb.HasMetadataCsum)
        {
            return true;
        }

        var crc = Ext4Checksums.Crc32C(_sb.ChecksumSeed, bitmap);
        var raw = desc.RawBlockBitmapChecksum;
        return _descSize >= 0x3A ? raw == crc : raw == (crc & 0xFFFF);
    }

    private bool IsBlockUninit(byte[] gdt, long group)
        => HonorsBlockUninit && (Descriptor(gdt, group).Flags & Ext4GroupDescriptor.FlagBlockUninit) != 0;

    private Ext4GroupDescriptor Descriptor(byte[] gdt, long group) => new(gdt.AsSpan((int)(group * _descSize), _descSize));

    private long GroupStart(long group) => _fdb + (group * _bpg);

    private long GroupOf(long block) => (block - _fdb) / _bpg;

    private static void SetBits(byte[] bitmap, long from, long to)
    {
        for (var bit = from; bit < to; bit++)
        {
            bitmap[bit >> 3] |= (byte)(1 << (int)(bit & 7));
        }
    }
}
