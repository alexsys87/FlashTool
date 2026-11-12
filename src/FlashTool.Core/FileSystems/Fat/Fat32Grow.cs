using System.Buffers.Binary;
using System.Collections;

namespace FlashTool.Core.FileSystems.Fat;

/// <summary>
/// Offline growth of a FAT32 file system to fill its partition.
/// <para>
/// More clusters need larger FATs, and the FATs sit right in front of the cluster heap. The heap start is
/// therefore pushed forward by a whole number of clusters: the data of those first clusters is copied into
/// free space, and every cluster number (FAT chains, directory entries, the root directory pointer) is
/// shifted down by that number. The cluster size and the position of all other data stay as they are.
/// </para>
/// <para>
/// Everything is validated and planned in memory first, and the displaced clusters are copied into free
/// space before anything live is touched. The step after that (directories, FATs, boot sector) cannot be made
/// atomic: an interruption there leaves the volume inconsistent. Callers must warn about this.
/// </para>
/// </summary>
internal static class Fat32Grow
{
    private const uint EntryMask = 0x0FFFFFFF;
    private const uint BadCluster = 0x0FFFFFF7;
    private const uint EndOfChain = 0x0FFFFFF8;
    private const uint CleanShutdownBit = 0x08000000;
    private const uint NoHardErrorBit = 0x04000000;
    private const int DirectoryEntrySize = 32;
    private const long MaxDirectoryBytes = 64L * 1024 * 1024;

    public static FatGrowResult Grow(PartitionAccess volume, CancellationToken cancellationToken)
    {
        try
        {
            return Run(volume, cancellationToken);
        }
        catch (FatGrowRefusedException refused)
        {
            return new FatGrowResult(0, 0, 0, refused.Reason);
        }
    }

    private static FatGrowResult Run(PartitionAccess volume, CancellationToken ct)
    {
        // ---- boot sector ----
        if (volume.Length < 512)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.NotFat);
        }

        var head = volume.Read(0, 512);
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(11));
        int spc = head[13];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14));
        int fatCount = head[16];
        if (head[510] != 0x55 || head[511] != 0xAA
            || bps is not (512 or 1024 or 2048 or 4096)
            || spc == 0 || (spc & (spc - 1)) != 0
            || reserved == 0 || fatCount == 0)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.NotFat);
        }

        // A root directory area or a 16-bit FAT size means FAT12/16, whatever the label in the boot sector says.
        if (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(17)) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(22)) != 0)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.Fat12Or16);
        }

        long fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(36));
        var extFlags = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(40));
        var version = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(42));
        long rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(44));
        int fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(48));
        int backupBootSector = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(50));
        if (fatSectors == 0)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.NotFat);
        }

        // Bit 7 of the flags: only one FAT is kept up to date.
        if (fatCount > 2 || version != 0 || (extFlags & 0x80) != 0)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.UnsupportedLayout);
        }

        long oldTotal = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(32));
        if (oldTotal == 0)
        {
            oldTotal = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(19));
        }

        var dataStart = reserved + (fatCount * fatSectors);
        if (oldTotal <= dataStart || oldTotal * bps > volume.Length)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.InconsistentMetadata);
        }

        var oldClusters = (oldTotal - dataStart) / spc;
        if (oldClusters < 1 || oldClusters > Fat32Formatter.MaxClusters
            || fatSectors * (bps / 4) < oldClusters + 2
            || rootCluster < 2 || rootCluster >= oldClusters + 2)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.InconsistentMetadata);
        }

        // Linux marks a mounted volume in the boot sector, Windows in the second FAT entry (checked below).
        if ((head[65] & 0x03) != 0)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.VolumeDirty);
        }

        // ---- new geometry: the heap moves forward by k clusters, the FATs share the sectors gained ----
        var limit = Math.Min(volume.Length / bps, uint.MaxValue);
        var step = fatCount == 2 && spc % 2 != 0 ? 2 : 1;
        long k = 0, newFatSectors, newDataStart, newClusters;
        while (true)
        {
            newFatSectors = fatSectors + (k * spc / fatCount);
            newDataStart = dataStart + (k * spc);
            if (newDataStart + spc > limit)
            {
                throw new FatGrowRefusedException(FatGrowSkipReason.AlreadyFillsPartition);
            }

            newClusters = Math.Min((limit - newDataStart) / spc, Fat32Formatter.MaxClusters);
            if (newFatSectors * (bps / 4) >= newClusters + 2)
            {
                break;
            }

            k += step;
        }

        if (newClusters <= oldClusters)
        {
            throw new FatGrowRefusedException(oldClusters >= Fat32Formatter.MaxClusters ? FatGrowSkipReason.ClusterLimit : FatGrowSkipReason.AlreadyFillsPartition);
        }

        var limited = (limit - newDataStart) / spc > newClusters;
        var newTotal = limited ? newDataStart + (newClusters * spc) : limit;
        var clusterSize = bps * spc;
        long OldPosition(long cluster) => (dataStart + ((cluster - 2) * spc)) * bps;
        long NewPosition(long cluster) => (newDataStart + ((cluster - 2) * spc)) * bps;

        // ---- FAT ----
        var fat = FatGrowIo.ReadFat(volume, (long)reserved * bps, oldClusters + 2, ct);
        for (var copy = 1; copy < fatCount; copy++)
        {
            var other = FatGrowIo.ReadFat(volume, (reserved + (copy * fatSectors)) * bps, oldClusters + 2, ct);
            if (!fat.AsSpan().SequenceEqual(other))
            {
                throw new FatGrowRefusedException(FatGrowSkipReason.InconsistentMetadata);
            }
        }

        if ((fat[1] & CleanShutdownBit) == 0 || (fat[1] & NoHardErrorBit) == 0)
        {
            throw new FatGrowRefusedException(FatGrowSkipReason.VolumeDirty);
        }

        // ---- where the displaced clusters go: behind the old end first, then into free old clusters ----
        var evicted = (int)Math.Min(k, oldClusters);
        var relocation = new uint[evicted];
        var tailStart = Math.Max(2, oldClusters + 2 - k);
        var nextTail = tailStart;
        long nextOld = 2;
        long moved = 0;

        uint Allocate()
        {
            if (nextTail < newClusters + 2)
            {
                return (uint)nextTail++;
            }

            while (nextOld < tailStart)
            {
                var candidate = nextOld++;
                if ((fat[candidate + k] & EntryMask) == 0)
                {
                    return (uint)candidate;
                }
            }

            throw new FatGrowRefusedException(FatGrowSkipReason.NoFreeSpace);
        }

        for (var c = 2; c < evicted + 2; c++)
        {
            var value = fat[c] & EntryMask;
            if (value != 0 && value != BadCluster)
            {
                relocation[c - 2] = Allocate();
                moved++;
            }
        }

        uint Map(long cluster)
        {
            if (cluster >= k + 2)
            {
                return (uint)(cluster - k);
            }

            var target = relocation[cluster - 2];
            return target != 0 ? target : throw new FatGrowRefusedException(FatGrowSkipReason.InconsistentMetadata);
        }

        bool InUse(long cluster)
        {
            if (cluster < 2 || cluster >= oldClusters + 2)
            {
                return false;
            }

            var value = fat[cluster] & EntryMask;
            return value != 0 && value != BadCluster;
        }

        // ---- the FAT in the new numbering ----
        var newFat = new uint[Math.Max(tailStart, nextTail)];
        newFat[0] = fat[0];
        newFat[1] = fat[1];
        long used = 0;
        for (long c = 2; c < oldClusters + 2; c++)
        {
            var raw = fat[c];
            var value = raw & EntryMask;
            if (value == 0)
            {
                continue;
            }

            if (value == BadCluster)
            {
                // A bad cluster under the new FAT is no longer a cluster at all.
                if (c >= k + 2)
                {
                    newFat[c - k] = raw;
                    used++;
                }

                continue;
            }

            uint mapped;
            if (value >= EndOfChain)
            {
                mapped = value;
            }
            else if (InUse(value))
            {
                mapped = Map(value);
            }
            else
            {
                throw new FatGrowRefusedException(FatGrowSkipReason.InconsistentMetadata);
            }

            newFat[Map(c)] = (raw & ~EntryMask) | mapped;
            used++;
        }

        // ---- directories: every start cluster in them is renumbered (only when the numbering changes) ----
        var directories = new List<(uint[] Clusters, byte[] Data)>();
        if (k > 0)
        {
            var visited = new BitArray((int)(oldClusters + 2));
            var queue = new Queue<long>();
            queue.Enqueue(rootCluster);
            long directoryBytes = 0;
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var chain = new List<uint>();
                for (var c = queue.Dequeue(); ;)
                {
                    if (!InUse(c) || visited[(int)c])
                    {
                        throw new FatGrowRefusedException(FatGrowSkipReason.InconsistentMetadata);
                    }

                    visited[(int)c] = true;
                    chain.Add((uint)c);
                    directoryBytes += clusterSize;
                    if (directoryBytes > MaxDirectoryBytes)
                    {
                        throw new FatGrowRefusedException(FatGrowSkipReason.UnsupportedLayout);
                    }

                    var next = fat[c] & EntryMask;
                    if (next >= EndOfChain)
                    {
                        break;
                    }

                    c = next;
                }

                var data = new byte[chain.Count * clusterSize];
                for (var i = 0; i < chain.Count; i++)
                {
                    FatGrowIo.ReadInto(volume, OldPosition(chain[i]), data.AsSpan(i * clusterSize, clusterSize));
                }

                for (var offset = 0; offset < data.Length; offset += DirectoryEntrySize)
                {
                    var entry = data.AsSpan(offset, DirectoryEntrySize);
                    if (entry[0] == 0x00)
                    {
                        break;
                    }

                    var attributes = entry[11];
                    if (entry[0] == 0xE5 || (attributes & 0x3F) == 0x0F || (attributes & 0x08) != 0)
                    {
                        // Deleted, a long name part, or the volume label.
                        continue;
                    }

                    long first = ((long)BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
                    if (first == 0)
                    {
                        // An empty file, or ".." of a directory in the root.
                        continue;
                    }

                    if (!InUse(first))
                    {
                        throw new FatGrowRefusedException(FatGrowSkipReason.InconsistentMetadata);
                    }

                    var isDot = entry[0] == (byte)'.' && (entry[1] == (byte)' ' || (entry[1] == (byte)'.' && entry[2] == (byte)' '));
                    if ((attributes & 0x10) != 0 && !isDot)
                    {
                        queue.Enqueue(first);
                    }

                    var mapped = Map(first);
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[20..], (ushort)(mapped >> 16));
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], (ushort)mapped);
                }

                directories.Add((chain.ToArray(), data));
            }
        }

        var newRoot = Map(rootCluster);

        // ---- nothing was written so far. First the copies into free space: harmless if interrupted ----
        FatGrowIo.CopyClusters(
            volume,
            Enumerable.Range(0, evicted).Where(i => relocation[i] != 0).Select(i => ((long)i + 2, (long)relocation[i])),
            OldPosition,
            NewPosition,
            clusterSize,
            ct);
        volume.Flush();
        ct.ThrowIfCancellationRequested();

        // ---- from here on the volume is inconsistent until the boot sector is written: no cancellation ----
        foreach (var (clusters, data) in directories)
        {
            for (var i = 0; i < clusters.Length; i++)
            {
                volume.Write(NewPosition(Map(clusters[i])), data.AsSpan(i * clusterSize, clusterSize));
            }
        }

        for (var copy = 0; copy < fatCount; copy++)
        {
            FatGrowIo.WriteFat(volume, (reserved + (copy * newFatSectors)) * bps, newFat, newFatSectors * bps);
        }

        var freeClusters = (uint)(newClusters - used);
        void PatchFsInfo(long sector)
        {
            if (sector <= 0 || sector >= reserved)
            {
                return;
            }

            var info = volume.Read(sector * bps, bps);
            if (BinaryPrimitives.ReadUInt32LittleEndian(info) == 0x41615252 && BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(484)) == 0x61417272)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(488), freeClusters);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(492), 0xFFFFFFFF); // no hint for the next free cluster
                volume.Write(sector * bps, info);
            }
        }

        void PatchBoot(long sector, bool required)
        {
            var boot = volume.Read(sector * bps, bps);
            if (!required && (boot[510] != 0x55 || boot[511] != 0xAA || BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)) != bps))
            {
                return;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(19), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), (uint)newTotal);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(36), (uint)newFatSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(44), newRoot);
            volume.Write(sector * bps, boot);
        }

        PatchFsInfo(fsInfoSector);
        if (backupBootSector > 0 && backupBootSector < reserved)
        {
            PatchFsInfo(fsInfoSector == 0 ? 0 : backupBootSector + fsInfoSector);
            PatchBoot(backupBootSector, required: false);
        }

        // The boot sector last: it is what makes the new FATs and numbering take effect.
        PatchBoot(0, required: true);
        volume.Flush();

        return new FatGrowResult(oldTotal, newTotal, bps, FatGrowSkipReason.None) { Limited = limited, ClustersMoved = moved };
    }
}
