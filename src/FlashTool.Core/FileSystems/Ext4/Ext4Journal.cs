using System.Buffers.Binary;

namespace FlashTool.Core.FileSystems.Ext4;

/// <summary>What <see cref="Ext4Journal.Recover"/> did.</summary>
internal enum JournalRecovery
{
    /// <summary>The file system did not need recovery.</summary>
    NotNeeded,

    /// <summary>Committed transactions were written to their places and the journal was emptied.</summary>
    Replayed,

    /// <summary>A journal this code does not replay (external, fast commits, v1 checksums...): nothing was written.</summary>
    Unsupported,

    /// <summary>The journal contradicts itself or a checksum does not match: nothing was written.</summary>
    Corrupt,
}

/// <summary>
/// Offline recovery of the ext3/ext4 journal (jbd2), as the kernel does when mounting and e2fsck
/// before checking: committed transactions are copied to their home blocks, except blocks revoked
/// later, then the journal is marked empty and needs_recovery is cleared. A file system taken from a
/// running system (or a card pulled without shutting down) always needs this before it can be grown.
/// </summary>
/// <remarks>
/// Everything is read and checked first (descriptor, revoke and commit checksums of v2/v3 journals,
/// the checksum of every data block); nothing is written unless all of it is consistent. A commit
/// block with a wrong checksum ends the log, as in the kernel: that transaction never completed.
/// </remarks>
internal static class Ext4Journal
{
    private const uint Magic = 0xC03B3998;
    private const int Descriptor = 1;
    private const int Commit = 2;
    private const int SuperblockV1 = 3;
    private const int SuperblockV2 = 4;
    private const int Revoke = 5;

    private const uint CompatChecksum = 0x1;
    private const uint IncompatRevoke = 0x1;
    private const uint Incompat64Bit = 0x2;
    private const uint IncompatAsyncCommit = 0x4;
    private const uint IncompatCsumV2 = 0x8;
    private const uint IncompatCsumV3 = 0x10;
    private const uint IncompatFastCommit = 0x20;

    private const uint TagEscape = 0x1;
    private const uint TagSameUuid = 0x2;
    private const uint TagLast = 0x8;

    private const uint ExtentsFlag = 0x80000;
    private const int JournalSuperblockSize = 1024;

    public static JournalRecovery Recover(PartitionAccess volume, CancellationToken cancellationToken)
    {
        var sb = new Ext4Superblock(volume.Read(1024, Ext4Superblock.Size));
        if ((sb.FeatureIncompat & Ext4Superblock.IncompatRecover) == 0)
        {
            return JournalRecovery.NotNeeded;
        }

        try
        {
            return new Replay(volume, sb).Run(cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            // A block number pointing outside the partition, a tag running off its block...
            return JournalRecovery.Corrupt;
        }
    }

    private sealed class Replay
    {
        private readonly PartitionAccess _volume;
        private readonly Ext4Superblock _sb;
        private readonly int _bs;
        private long[] _map = [];
        private byte[] _jsb = [];
        private uint _incompat;
        private uint _first;
        private uint _maxLen;
        private uint _seed;

        public Replay(PartitionAccess volume, Ext4Superblock sb)
        {
            _volume = volume;
            _sb = sb;
            _bs = sb.BlockSize;
        }

        private bool Csum => (_incompat & (IncompatCsumV2 | IncompatCsumV3)) != 0;

        private bool Csum3 => (_incompat & IncompatCsumV3) != 0;

        private bool Wide => (_incompat & Incompat64Bit) != 0;

        private int TagSize => Csum3 ? 16 : (Wide ? 12 : 8) + ((_incompat & IncompatCsumV2) != 0 ? 2 : 0);

        public JournalRecovery Run(CancellationToken ct)
        {
            const uint compatHasJournal = 0x4;
            var journalInode = U32(_sb.Raw, 0xE0);
            var journalDevice = U32(_sb.Raw, 0xE4);
            if ((_sb.FeatureCompat & compatHasJournal) == 0 || journalInode == 0 || journalDevice != 0
                || (_sb.FeatureIncompat & Ext4Superblock.IncompatJournalDev) != 0)
            {
                return JournalRecovery.Unsupported;
            }

            var mapping = MapInode(journalInode);
            if (mapping == null)
            {
                return JournalRecovery.Corrupt;
            }

            _map = mapping;
            _jsb = ReadJournal(0);
            if (BE32(_jsb, 0) != Magic || BE32(_jsb, 4) is not (SuperblockV1 or SuperblockV2) || BE32(_jsb, 0x0C) != _bs)
            {
                return JournalRecovery.Corrupt;
            }

            _maxLen = BE32(_jsb, 0x10);
            _first = BE32(_jsb, 0x14);
            var sequence = BE32(_jsb, 0x18);
            var start = BE32(_jsb, 0x1C);
            if (_maxLen > _map.Length || _first == 0 || _first >= _maxLen)
            {
                return JournalRecovery.Corrupt;
            }

            if (BE32(_jsb, 4) == SuperblockV2)
            {
                _incompat = BE32(_jsb, 0x28);
                var known = IncompatRevoke | Incompat64Bit | IncompatCsumV2 | IncompatCsumV3;
                if ((_incompat & ~known) != 0 || (BE32(_jsb, 0x24) & CompatChecksum) != 0 || (_incompat & (IncompatAsyncCommit | IncompatFastCommit)) != 0)
                {
                    return JournalRecovery.Unsupported;
                }

                if (Csum)
                {
                    const byte crc32c = 4;
                    var stored = BE32(_jsb, 0xFC);
                    var copy = _jsb.AsSpan(0, JournalSuperblockSize).ToArray();
                    copy.AsSpan(0xFC, 4).Clear();
                    if (_jsb[0x50] != crc32c || Ext4Checksums.Crc32C(0xFFFFFFFF, copy) != stored)
                    {
                        return JournalRecovery.Corrupt;
                    }

                    _seed = Ext4Checksums.Crc32C(0xFFFFFFFF, _jsb.AsSpan(0x30, 16));
                }
            }

            var writes = new List<(long FsBlock, byte[] Data)>();
            var endSequence = sequence;
            if (start != 0)
            {
                var plan = Scan(start, sequence, ct);
                if (plan == null)
                {
                    return JournalRecovery.Corrupt;
                }

                (writes, endSequence) = plan.Value;
            }

            // Everything checked out: write the blocks home, then mark the journal empty, then the file system clean.
            foreach (var (block, data) in writes)
            {
                ct.ThrowIfCancellationRequested();
                _volume.Write(block * _bs, data);
            }

            BinaryPrimitives.WriteUInt32BigEndian(_jsb.AsSpan(0x18), endSequence + 1);
            BinaryPrimitives.WriteUInt32BigEndian(_jsb.AsSpan(0x1C), 0);
            if (Csum)
            {
                _jsb.AsSpan(0xFC, 4).Clear();
                BinaryPrimitives.WriteUInt32BigEndian(_jsb.AsSpan(0xFC), Ext4Checksums.Crc32C(0xFFFFFFFF, _jsb.AsSpan(0, JournalSuperblockSize)));
            }

            _volume.Write(_map[0] * _bs, _jsb);

            // The superblock itself may just have been replayed: change the version now on disk.
            var current = new Ext4Superblock(_volume.Read(1024, Ext4Superblock.Size));
            var raw = current.Raw.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x60), current.FeatureIncompat & ~Ext4Superblock.IncompatRecover);
            _volume.Write(1024, new Ext4Superblock(raw).Serialize());
            _volume.Flush();
            return JournalRecovery.Replayed;
        }

        /// <summary>
        /// Walks the log from its start: committed transactions, their revokes and data. Returns the
        /// blocks to write in log order and the first sequence that is not committed, or null when the
        /// journal is inconsistent.
        /// </summary>
        private (List<(long FsBlock, byte[] Data)> Writes, uint EndSequence)? Scan(uint start, uint firstSequence, CancellationToken ct)
        {
            var descriptors = new List<(uint Sequence, uint Position, List<(long Block, uint Flags, uint Checksum)> Tags)>();
            var revokeBlocks = new List<(uint Sequence, byte[] Data)>();
            var sequence = firstSequence;
            var position = start;
            var visited = 0L;

            while (visited++ <= _maxLen)
            {
                ct.ThrowIfCancellationRequested();
                var block = ReadJournal(position);
                if (BE32(block, 0) != Magic || BE32(block, 8) != sequence)
                {
                    break;
                }

                var type = BE32(block, 4);
                if (type == Descriptor)
                {
                    if (Csum && !TailMatches(block))
                    {
                        return null;
                    }

                    var tags = ParseTags(block);
                    if (tags == null)
                    {
                        return null;
                    }

                    descriptors.Add((sequence, position, tags));
                    position = Advance(position, 1 + tags.Count);
                }
                else if (type == Commit)
                {
                    if (Csum && !CommitMatches(block))
                    {
                        // Never completed: this is where the log ends.
                        break;
                    }

                    sequence++;
                    position = Advance(position, 1);
                }
                else if (type == Revoke)
                {
                    if (Csum && !TailMatches(block))
                    {
                        return null;
                    }

                    revokeBlocks.Add((sequence, block));
                    position = Advance(position, 1);
                }
                else
                {
                    break;
                }
            }

            var end = sequence;

            // Revoked blocks of committed transactions: block -> latest revoking sequence.
            var revoked = new Dictionary<long, uint>();
            foreach (var (revokeSequence, data) in revokeBlocks.Where(r => r.Sequence < end))
            {
                var used = (int)BE32(data, 12);
                var size = Wide ? 8 : 4;
                if (used < 16 || used > data.Length)
                {
                    return null;
                }

                for (var at = 16; at + size <= used; at += size)
                {
                    long blockNumber = Wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at)) : BE32(data, at);
                    if (!revoked.TryGetValue(blockNumber, out var existing) || revokeSequence > existing)
                    {
                        revoked[blockNumber] = revokeSequence;
                    }
                }
            }

            var writes = new List<(long, byte[])>();
            var fsBlocks = (long)_sb.BlocksCount;
            foreach (var (descriptorSequence, descriptorPosition, tags) in descriptors.Where(d => d.Sequence < end))
            {
                for (var i = 0; i < tags.Count; i++)
                {
                    var (blockNumber, flags, checksum) = tags[i];
                    var data = ReadJournal(Advance(descriptorPosition, 1 + i));
                    if (Csum && !DataMatches(data, descriptorSequence, checksum))
                    {
                        return null;
                    }

                    if (blockNumber >= fsBlocks)
                    {
                        return null;
                    }

                    if (revoked.TryGetValue(blockNumber, out var revokedAt) && revokedAt >= descriptorSequence)
                    {
                        continue;
                    }

                    if ((flags & TagEscape) != 0)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(data, Magic);
                    }

                    writes.Add((blockNumber, data));
                }
            }

            return (writes, end);
        }

        private List<(long Block, uint Flags, uint Checksum)>? ParseTags(byte[] block)
        {
            var tags = new List<(long, uint, uint)>();
            var limit = _bs - (Csum ? 4 : 0);
            var at = 12;
            while (at + TagSize <= limit)
            {
                long number = BE32(block, at);
                uint flags;
                uint checksum;
                if (Csum3)
                {
                    flags = BE32(block, at + 4);
                    number |= (long)BE32(block, at + 8) << 32;
                    checksum = BE32(block, at + 12);
                    if (!Wide)
                    {
                        number &= 0xFFFFFFFF;
                    }
                }
                else
                {
                    checksum = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at + 4));
                    flags = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at + 6));
                    if (Wide)
                    {
                        number |= (long)BE32(block, at + 8) << 32;
                    }
                }

                tags.Add((number, flags, checksum));
                at += TagSize;
                if ((flags & TagSameUuid) == 0)
                {
                    at += 16;
                }

                if ((flags & TagLast) != 0)
                {
                    return tags;
                }

                if (tags.Count > _maxLen)
                {
                    return null;
                }
            }

            return tags;
        }

        /// <summary>Descriptor and revoke blocks end with crc32c(seed, block with the tail zeroed).</summary>
        private bool TailMatches(byte[] block)
        {
            var stored = BE32(block, _bs - 4);
            var copy = (byte[])block.Clone();
            copy.AsSpan(_bs - 4, 4).Clear();
            return Ext4Checksums.Crc32C(_seed, copy) == stored;
        }

        /// <summary>The commit block's first checksum slot holds crc32c(seed, block with that slot zeroed).</summary>
        private bool CommitMatches(byte[] block)
        {
            var stored = BE32(block, 0x10);
            var copy = (byte[])block.Clone();
            copy.AsSpan(0x10, 4).Clear();
            return Ext4Checksums.Crc32C(_seed, copy) == stored;
        }

        /// <summary>Data blocks: crc32c(seed, sequence, data); v2 keeps only the low 16 bits in the tag.</summary>
        private bool DataMatches(byte[] data, uint sequence, uint stored)
        {
            Span<byte> sequenceBe = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(sequenceBe, sequence);
            var crc = Ext4Checksums.Crc32C(Ext4Checksums.Crc32C(_seed, sequenceBe), data);
            return Csum3 ? crc == stored : (crc & 0xFFFF) == stored;
        }

        private uint Advance(uint position, int count)
        {
            var next = (long)position + count;
            while (next >= _maxLen)
            {
                next -= _maxLen - _first;
            }

            return (uint)next;
        }

        private byte[] ReadJournal(long logical) => _volume.Read(_map[logical] * _bs, _bs);

        /// <summary>Physical block of every logical block of an inode (extents or the classic block map); null when unreadable.</summary>
        private long[]? MapInode(uint number)
        {
            var ipg = _sb.InodesPerGroup;
            var group = (number - 1) / ipg;
            var index = (number - 1) % ipg;
            var descPerBlock = _bs / _sb.DescriptorSize;
            if (_sb.HasMetaBg && group / descPerBlock >= _sb.FirstMetaBg)
            {
                return null;
            }

            var descriptorOffset = ((_sb.FirstDataBlock + 1L + (group / descPerBlock)) * _bs) + ((group % descPerBlock) * _sb.DescriptorSize);
            var descriptor = _volume.Read(descriptorOffset, _sb.DescriptorSize);
            var inodeTable = (long)new Ext4GroupDescriptor(descriptor).InodeTable;
            var inode = _volume.Read((inodeTable * _bs) + (index * (long)_sb.InodeSize), _sb.InodeSize);

            var size = U32(inode, 0x04) | ((long)U32(inode, 0x6C) << 32);
            var blocks = size / _bs;
            if (blocks <= 1 || blocks > 1L << 24)
            {
                return null;
            }

            var map = new long[blocks];
            Array.Fill(map, -1L);
            var root = inode.AsSpan(0x28, 60).ToArray();
            var ok = (U32(inode, 0x20) & ExtentsFlag) != 0 ? MapExtents(root, map, 0) : MapBlockMap(root, map);
            return ok && map.All(b => b > 0 && b < (long)_sb.BlocksCount) ? map : null;
        }

        private bool MapExtents(byte[] node, long[] map, int level)
        {
            if (level > 5 || BinaryPrimitives.ReadUInt16LittleEndian(node) != 0xF30A)
            {
                return false;
            }

            int entries = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2));
            int depth = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(6));
            for (var i = 0; i < entries; i++)
            {
                var e = node.AsSpan(12 + (i * 12), 12);
                if (depth == 0)
                {
                    long logical = BinaryPrimitives.ReadUInt32LittleEndian(e);
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
                    if (length > 32768)
                    {
                        // An uninitialized extent reads as zeros; a journal has none.
                        return false;
                    }

                    var physical = ((long)BinaryPrimitives.ReadUInt16LittleEndian(e[6..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                    for (var b = 0; b < length && logical + b < map.Length; b++)
                    {
                        map[logical + b] = physical + b;
                    }
                }
                else
                {
                    var child = ((long)BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                    if (!MapExtents(_volume.Read(child * _bs, _bs), map, level + 1))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>ext3: twelve direct blocks, then single, double and triple indirect ones.</summary>
        private bool MapBlockMap(byte[] root, long[] map)
        {
            var perBlock = _bs / 4;
            long logical = 0;
            for (var i = 0; i < 12 && logical < map.Length; i++)
            {
                map[logical++] = U32(root, i * 4);
            }

            for (var level = 1; level <= 3 && logical < map.Length; level++)
            {
                Indirect(U32(root, (11 + level) * 4), level, ref logical);
            }

            return true;

            void Indirect(long block, int depth, ref long next)
            {
                if (block == 0 || next >= map.Length)
                {
                    return;
                }

                var entries = _volume.Read(block * _bs, _bs);
                for (var j = 0; j < perBlock && next < map.Length; j++)
                {
                    var child = U32(entries, j * 4);
                    if (depth == 1)
                    {
                        map[next++] = child;
                    }
                    else
                    {
                        Indirect(child, depth - 1, ref next);
                    }
                }
            }
        }
    }

    private static uint BE32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
