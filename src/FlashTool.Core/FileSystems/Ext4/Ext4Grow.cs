using System.Buffers.Binary;

namespace FlashTool.Core.FileSystems.Ext4;

/// <summary>Why an ext2/3/4 file system was left untouched.</summary>
public enum Ext4GrowSkipReason
{
    None,
    NotExtFileSystem,
    AlreadyFillsPartition,
    NotCleanlyUnmounted,
    JournalNeedsRecovery,
    UnsupportedFeature,
    NoRoomForDescriptors,
    InconsistentMetadata,
}

public sealed record Ext4GrowResult(ulong OldBlocks, ulong NewBlocks, int BlockSize, Ext4GrowSkipReason SkipReason)
{
    public bool Grown => NewBlocks > OldBlocks && SkipReason == Ext4GrowSkipReason.None;
}

/// <summary>
/// Offline growth of an ext2/ext3/ext4 file system to fill its partition, in
/// the spirit of resize2fs: the last partial group is extended, new block
/// groups are appended (their bitmaps and inode table live inside the group),
/// the descriptor table grows into the reserved GDT blocks (resize_inode), and
/// checksums plus all superblock/descriptor backups are rewritten.
/// Layouts that would need moving existing data (meta_bg, bigalloc,
/// sparse_super2) are refused without writing anything.
/// </summary>
internal sealed class Ext4Grow
{
    private const uint ResizeInodeNumber = 7;
    private const int SuperblockOffset = 1024;

    private readonly PartitionAccess _volume;
    private readonly Ext4Superblock _sb;
    private readonly int _bs;
    private readonly uint _fdb;
    private readonly uint _bpg;
    private readonly uint _ipg;
    private readonly int _descSize;
    private readonly int _descPerBlock;

    private Ext4Grow(PartitionAccess volume, Ext4Superblock sb)
    {
        _volume = volume;
        _sb = sb;
        _bs = sb.BlockSize;
        _fdb = sb.FirstDataBlock;
        _bpg = sb.BlocksPerGroup;
        _ipg = sb.InodesPerGroup;
        _descSize = sb.DescriptorSize;
        _descPerBlock = _bs / _descSize;
    }

    private bool HasCsumFeature => _sb.HasMetadataCsum || _sb.HasGdtCsum;

    private long InodeTableBlocks => ((long)_ipg * _sb.InodeSize + _bs - 1) / _bs;

    public static Ext4GrowResult Grow(PartitionAccess volume, CancellationToken cancellationToken)
    {
        var sbRaw = volume.Read(SuperblockOffset, Ext4Superblock.Size);
        var sb = new Ext4Superblock(sbRaw);
        if (!sb.IsValidMagic)
        {
            return new Ext4GrowResult(0, 0, 0, Ext4GrowSkipReason.NotExtFileSystem);
        }

        return new Ext4Grow(volume, sb).Run(cancellationToken);
    }

    private Ext4GrowResult Run(CancellationToken ct)
    {
        var oldBlocks = _sb.BlocksCount;
        Ext4GrowResult Skip(Ext4GrowSkipReason reason) => new(oldBlocks, oldBlocks, _bs, reason);

        var refusal = CheckSupported();
        if (refusal != Ext4GrowSkipReason.None)
        {
            return Skip(refusal);
        }

        var oldGroups = GroupCount(oldBlocks);
        var oldGdtBlocks = GdtBlocks(oldGroups);
        var reserved = (long)_sb.ReservedGdtBlocks;

        var newBlocks = PlanNewSize(oldBlocks, oldGdtBlocks + reserved);
        if (newBlocks <= oldBlocks)
        {
            return Skip(newBlocks == 0 ? Ext4GrowSkipReason.NoRoomForDescriptors : Ext4GrowSkipReason.AlreadyFillsPartition);
        }

        var newGroups = GroupCount(newBlocks);
        var newGdtBlocks = GdtBlocks(newGroups);
        var consumed = newGdtBlocks - oldGdtBlocks;
        var newReserved = reserved - consumed;
        var gdtStart = (long)_fdb + 1;

        // ---- Read and validate everything before the first write. ----
        var gdt = new byte[newGdtBlocks * _bs];
        _volume.Read(gdtStart * _bs, (int)(oldGdtBlocks * _bs)).CopyTo(gdt, 0);

        ResizeInodePlan? resizePlan = null;
        if (_sb.HasResizeInode && reserved > 0)
        {
            resizePlan = PlanResizeInode(gdtStart, oldGdtBlocks, reserved, consumed, oldGroups, newGroups);
            if (resizePlan == null)
            {
                return Skip(Ext4GrowSkipReason.InconsistentMetadata);
            }
        }

        ulong freedBlocks = 0;
        LastGroupPlan? lastGroup = null;
        var lastIndex = oldGroups - 1;
        var lastStart = GroupStart(lastIndex);
        var oldLastCount = (long)oldBlocks - lastStart;
        var newLastCount = Math.Min(_bpg, (long)newBlocks - lastStart);
        if (newLastCount > oldLastCount)
        {
            var desc = Descriptor(gdt, lastIndex);
            if ((desc.Flags & Ext4GroupDescriptor.FlagBlockUninit) != 0)
            {
                return Skip(Ext4GrowSkipReason.UnsupportedFeature);
            }

            var bitmapBlock = (long)desc.BlockBitmap;
            var bitmap = _volume.Read(bitmapBlock * _bs, _bs);
            for (var bit = oldLastCount; bit < newLastCount; bit++)
            {
                bitmap[bit >> 3] &= (byte)~(1 << (int)(bit & 7));
            }

            var delta = (uint)(newLastCount - oldLastCount);
            desc.FreeBlocks += delta;
            SetBlockBitmapChecksum(desc, bitmap);
            desc.UpdateChecksum(_sb, (uint)lastIndex);
            freedBlocks += delta;
            lastGroup = new LastGroupPlan(bitmapBlock, bitmap);
        }

        // ---- Build the new groups (in memory descriptors, written later). ----
        var newGroupPlans = new List<NewGroupPlan>();
        for (var g = oldGroups; g < newGroups; g++)
        {
            ct.ThrowIfCancellationRequested();
            var start = GroupStart(g);
            var count = Math.Min(_bpg, (long)newBlocks - start);
            var backup = HasBackup(g);
            var meta = start + (backup ? 1 + newGdtBlocks + newReserved : 0);
            var used = meta - start + 2 + InodeTableBlocks;

            var blockBitmap = new byte[_bs];
            SetBits(blockBitmap, 0, used);
            SetBits(blockBitmap, count, (long)_bs * 8);
            var inodeBitmap = new byte[_bs];
            SetBits(inodeBitmap, _ipg, (long)_bs * 8);

            var desc = Descriptor(gdt, g);
            desc.BlockBitmap = (ulong)meta;
            desc.InodeBitmap = (ulong)(meta + 1);
            desc.InodeTable = (ulong)(meta + 2);
            desc.FreeBlocks = (uint)(count - used);
            desc.FreeInodes = _ipg;
            desc.UsedDirs = 0;
            desc.Flags = HasCsumFeature ? Ext4GroupDescriptor.FlagInodeUninit : (ushort)0;
            desc.ItableUnused = HasCsumFeature ? _ipg : 0;
            SetBlockBitmapChecksum(desc, blockBitmap);
            desc.SetInodeBitmapChecksum(0);
            desc.UpdateChecksum(_sb, (uint)g);

            freedBlocks += (ulong)(count - used);
            newGroupPlans.Add(new NewGroupPlan(g, start, backup, meta, blockBitmap, inodeBitmap));
        }

        // ---- New superblock. ----
        var addedInodes = (ulong)(newGroups - oldGroups) * _ipg;
        if (_sb.InodesCount + addedInodes > uint.MaxValue)
        {
            return Skip(Ext4GrowSkipReason.NoRoomForDescriptors);
        }

        var newSb = _sb.Clone();
        newSb.BlocksCount = newBlocks;
        newSb.FreeBlocksCount = _sb.FreeBlocksCount + freedBlocks;
        newSb.InodesCount = (uint)(_sb.InodesCount + addedInodes);
        newSb.FreeInodesCount = (uint)(_sb.FreeInodesCount + addedInodes);
        newSb.ReservedBlocksCount = (ulong)((decimal)_sb.ReservedBlocksCount * newBlocks / oldBlocks);
        newSb.ReservedGdtBlocks = (ushort)newReserved;
        newSb.OverheadClusters = 0;

        // ---- Writes: new space first, then metadata that the old superblock does not reference, superblock last. ----
        foreach (var plan in newGroupPlans)
        {
            ct.ThrowIfCancellationRequested();
            _volume.Write(plan.Meta * _bs, plan.BlockBitmap);
            _volume.Write((plan.Meta + 1) * _bs, plan.InodeBitmap);
            if (!HasCsumFeature)
            {
                // Without uninit_bg/metadata_csum the kernel trusts inode tables blindly.
                _volume.Zero((plan.Meta + 2) * _bs, InodeTableBlocks * _bs, ct);
            }

            if (plan.Backup && newReserved > 0)
            {
                _volume.Zero((plan.Start + 1 + newGdtBlocks) * _bs, newReserved * _bs, ct);
            }
        }

        if (lastGroup != null)
        {
            _volume.Write(lastGroup.BitmapBlock * _bs, lastGroup.Bitmap);
        }

        if (resizePlan != null)
        {
            WriteResizeInode(resizePlan, newSb);
        }

        for (var g = 1L; g < newGroups; g++)
        {
            if (!HasBackup(g))
            {
                continue;
            }

            var start = GroupStart(g);
            _volume.Write((start + 1) * _bs, gdt);
            var backupSb = newSb.Clone();
            backupSb.BlockGroupNumber = (ushort)Math.Min(g, ushort.MaxValue);
            _volume.Write(start * _bs, backupSb.Serialize());
        }

        _volume.Write(gdtStart * _bs, gdt);
        _volume.Flush();
        newSb.BlockGroupNumber = 0;
        _volume.Write(SuperblockOffset, newSb.Serialize());
        _volume.Flush();

        return new Ext4GrowResult(oldBlocks, newBlocks, _bs, Ext4GrowSkipReason.None);
    }

    private Ext4GrowSkipReason CheckSupported()
    {
        const ushort validFs = 0x1;
        const ushort errorFs = 0x2;
        if ((_sb.State & validFs) == 0 || (_sb.State & errorFs) != 0)
        {
            return Ext4GrowSkipReason.NotCleanlyUnmounted;
        }

        if ((_sb.FeatureIncompat & Ext4Superblock.IncompatRecover) != 0
            || (_sb.FeatureRoCompat & Ext4Superblock.RoCompatOrphanPresent) != 0)
        {
            return Ext4GrowSkipReason.JournalNeedsRecovery;
        }

        var unsupported =
            (_sb.FeatureIncompat & (Ext4Superblock.IncompatMetaBg | Ext4Superblock.IncompatJournalDev | Ext4Superblock.IncompatMmp)) != 0
            || (_sb.FeatureRoCompat & Ext4Superblock.RoCompatBigAlloc) != 0
            || (_sb.FeatureCompat & (Ext4Superblock.CompatSparseSuper2 | Ext4Superblock.CompatExcludeBitmap)) != 0
            || (_sb.HasMetadataCsum && _sb.ChecksumType != 1)
            || _bs is < 1024 or > 65536
            || _bpg == 0 || _ipg == 0 || _bpg > _bs * 8 || _ipg > _bs * 8
            || _sb.ClustersPerGroup != _bpg
            || _descSize is < 32 or > 1024 || (_descSize & (_descSize - 1)) != 0;
        return unsupported ? Ext4GrowSkipReason.UnsupportedFeature : Ext4GrowSkipReason.None;
    }

    /// <summary>
    /// Target block count: as large as the partition allows, limited by the
    /// descriptor table capacity and 32-bit block numbers, and without a
    /// trailing group too small to hold its own metadata.
    /// </summary>
    private ulong PlanNewSize(ulong oldBlocks, long maxGdtBlocks)
    {
        var target = (ulong)(_volume.Length / _bs);
        if (!_sb.Is64Bit)
        {
            target = Math.Min(target, uint.MaxValue);
        }

        var maxGroups = maxGdtBlocks * _descPerBlock;
        target = Math.Min(target, _fdb + (ulong)maxGroups * _bpg);

        while (target > oldBlocks)
        {
            var groups = GroupCount(target);
            var lastStart = GroupStart(groups - 1);
            if ((ulong)lastStart < oldBlocks)
            {
                break; // Only the existing last group grows.
            }

            var gdtBlocks = GdtBlocks(groups);
            var reserved = _sb.ReservedGdtBlocks - (gdtBlocks - GdtBlocks(GroupCount(oldBlocks)));
            var overhead = (HasBackup(groups - 1) ? 1 + gdtBlocks + reserved : 0) + 2 + InodeTableBlocks;
            if ((long)(target - (ulong)lastStart) >= overhead + 64)
            {
                break;
            }

            target = (ulong)lastStart;
        }

        return target;
    }

    private sealed record ResizeInodePlan(byte[] Inode, long InodeOffset, long DindBlock, byte[] Dind, List<(long Block, byte[] Data)> Indirect);

    private ResizeInodePlan? PlanResizeInode(long gdtStart, long oldGdtBlocks, long reserved, long consumed, long oldGroups, long newGroups)
    {
        var apb = _bs / 4;
        var gdt0 = _volume.Read(gdtStart * _bs, _descSize);
        var inodeTable = (long)new Ext4GroupDescriptor(gdt0).InodeTable;
        var inodeOffset = (inodeTable * _bs) + ((ResizeInodeNumber - 1) * (long)_sb.InodeSize);
        var inode = _volume.Read(inodeOffset, _sb.InodeSize);
        var dindBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(0x28 + (13 * 4)));
        if (dindBlock == 0)
        {
            return null;
        }

        var dind = _volume.Read((long)dindBlock * _bs, _bs);
        var oldBackups = BackupGroups(oldGroups);
        var newBackups = BackupGroups(newGroups);
        var indirect = new List<(long, byte[])>();

        for (var r = 0L; r < reserved; r++)
        {
            var gdtIndex = oldGdtBlocks + r;
            var block = gdtStart + gdtIndex;
            var slot = (int)(gdtIndex % apb) * 4;
            if (BinaryPrimitives.ReadUInt32LittleEndian(dind.AsSpan(slot)) != block)
            {
                return null;
            }

            if (r < consumed)
            {
                // This reserved block becomes a real descriptor block.
                BinaryPrimitives.WriteUInt32LittleEndian(dind.AsSpan(slot), 0);
                continue;
            }

            var list = _volume.Read(block * _bs, _bs);
            if (newBackups.Count > apb)
            {
                return null;
            }

            for (var i = 0; i < oldBackups.Count; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(i * 4)) != (uint)(block + (oldBackups[i] * _bpg)))
                {
                    return null;
                }
            }

            for (var i = oldBackups.Count; i < newBackups.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(i * 4), (uint)(block + (newBackups[i] * _bpg)));
            }

            indirect.Add((block, list));
        }

        // Every reserved block (and each of its backups) plus the DIND block is owned by the inode.
        var remaining = reserved - consumed;
        var ownedBlocks = (ulong)(1 + (remaining * (1 + newBackups.Count)));
        var sectors = ownedBlocks * (ulong)(_bs / 512);
        BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(0x1C), (uint)sectors);
        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x74), (ushort)(sectors >> 32));

        return new ResizeInodePlan(inode, inodeOffset, dindBlock, dind, indirect);
    }

    private void WriteResizeInode(ResizeInodePlan plan, Ext4Superblock newSb)
    {
        foreach (var (block, data) in plan.Indirect)
        {
            _volume.Write(block * _bs, data);
        }

        _volume.Write(plan.DindBlock * _bs, plan.Dind);
        if (newSb.HasMetadataCsum)
        {
            UpdateInodeChecksum(plan.Inode, ResizeInodeNumber);
        }

        _volume.Write(plan.InodeOffset, plan.Inode);
    }

    private void UpdateInodeChecksum(byte[] inode, uint number)
    {
        const int lo = 0x7C;
        const int hi = 0x82;
        var generation = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(0x64));
        var crc = Ext4Checksums.Crc32C(_sb.ChecksumSeed, number);
        crc = Ext4Checksums.Crc32C(crc, generation);
        crc = Ext4Checksums.Crc32C(crc, inode.AsSpan(0, lo));
        crc = Ext4Checksums.Crc32C(crc, [0, 0]);
        crc = Ext4Checksums.Crc32C(crc, inode.AsSpan(lo + 2, 128 - lo - 2));

        var hasHi = false;
        if (inode.Length > 128)
        {
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0x80));
            crc = Ext4Checksums.Crc32C(crc, inode.AsSpan(128, hi - 128));
            var offset = hi;
            if (128 + extra >= hi + 2)
            {
                crc = Ext4Checksums.Crc32C(crc, [0, 0]);
                offset += 2;
                hasHi = true;
            }

            crc = Ext4Checksums.Crc32C(crc, inode.AsSpan(offset));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(lo), (ushort)crc);
        if (hasHi)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(hi), (ushort)(crc >> 16));
        }
    }

    private void SetBlockBitmapChecksum(Ext4GroupDescriptor desc, byte[] bitmap)
    {
        if (_sb.HasMetadataCsum)
        {
            desc.SetBlockBitmapChecksum(Ext4Checksums.Crc32C(_sb.ChecksumSeed, bitmap.AsSpan(0, (int)(_bpg / 8))));
        }
    }

    private Ext4GroupDescriptor Descriptor(byte[] gdt, long group) => new(gdt.AsSpan((int)(group * _descSize), _descSize));

    private long GroupCount(ulong blocks) => (long)((blocks - _fdb + _bpg - 1) / _bpg);

    private long GroupStart(long group) => _fdb + (group * _bpg);

    private long GdtBlocks(long groups) => (groups + _descPerBlock - 1) / _descPerBlock;

    /// <summary>Groups carrying superblock/descriptor backups (sparse_super: 0, 1 and powers of 3, 5, 7).</summary>
    private bool HasBackup(long group)
    {
        if (group <= 1 || !_sb.HasSparseSuper)
        {
            return true;
        }

        return IsPowerOf(group, 3) || IsPowerOf(group, 5) || IsPowerOf(group, 7);
    }

    /// <summary>Backup groups (excluding group 0) below <paramref name="groups"/>, ascending.</summary>
    private List<long> BackupGroups(long groups)
    {
        var list = new List<long>();
        for (var g = 1L; g < groups; g++)
        {
            if (HasBackup(g))
            {
                list.Add(g);
            }
        }

        return list;
    }

    private static bool IsPowerOf(long value, long b)
    {
        while (value % b == 0)
        {
            value /= b;
        }

        return value == 1;
    }

    private static void SetBits(byte[] bitmap, long from, long to)
    {
        for (var bit = from; bit < to; bit++)
        {
            bitmap[bit >> 3] |= (byte)(1 << (int)(bit & 7));
        }
    }

    private sealed record LastGroupPlan(long BitmapBlock, byte[] Bitmap);

    private sealed record NewGroupPlan(long Group, long Start, bool Backup, long Meta, byte[] BlockBitmap, byte[] InodeBitmap);
}
