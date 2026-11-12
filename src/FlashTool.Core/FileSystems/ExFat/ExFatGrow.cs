using System.Buffers.Binary;
using System.Collections;
using System.Numerics;
using FlashTool.Core.FileSystems.Fat;

namespace FlashTool.Core.FileSystems.ExFat;

/// <summary>
/// Offline growth of an exFAT file system to fill its partition.
/// <para>
/// The FAT has to grow, and the cluster heap starts right behind it, so the heap start is pushed forward
/// by a whole number of clusters, exactly as <see cref="Fat32Grow"/> does: the data of those first clusters
/// is copied into free space and every cluster number is shifted down. On top of that the allocation bitmap
/// is rebuilt for the new cluster count (in a new contiguous run when it no longer fits where it was), the
/// up-case table is moved as a whole if it is in the way, contiguous files ("NoFatChain") that lose their
/// first clusters get a FAT chain instead, and the checksums of the changed entry sets and of the boot
/// region are recomputed.
/// </para>
/// <para>
/// Everything is validated and planned in memory first, and all copies go into free space before anything
/// live is touched. The step after that (directories, FAT, boot region) cannot be made atomic: an
/// interruption there leaves the volume inconsistent. Callers must warn about this.
/// </para>
/// </summary>
internal static class ExFatGrow
{
    private const uint EndOfChain = 0xFFFFFFFF;
    private const uint BadCluster = 0xFFFFFFF7;
    private const int BootRegionSectors = 12;
    private const int EntrySize = 32;
    private const long MaxDirectoryBytes = 256L * 1024 * 1024;

    /// <summary>A directory entry that owns clusters.</summary>
    private sealed class Item
    {
        public int Directory;

        /// <summary>Offset of the entry with the first cluster (stream extension, bitmap or up-case entry).</summary>
        public int EntryOffset;

        /// <summary>Offset of the file entry that carries the set checksum.</summary>
        public int SetOffset;

        public int SetLength;
        public long First;
        public long Count;

        /// <summary>The clusters are a contiguous run without a FAT chain.</summary>
        public bool Contiguous;
    }

    private sealed class DirectoryData(uint[] clusters, byte[] data)
    {
        public uint[] Clusters { get; } = clusters;

        public byte[] Data { get; } = data;
    }

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
        static FatGrowRefusedException Refuse(FatGrowSkipReason reason) => new(reason);

        // ---- boot region ----
        if (volume.Length < 2 * BootRegionSectors * 512)
        {
            throw Refuse(FatGrowSkipReason.NotFat);
        }

        var head = volume.Read(0, 512);
        int sectorShift = head[108];
        int clusterShift = head[109];
        if (!head.AsSpan(3, 8).SequenceEqual("EXFAT   "u8) || head[510] != 0x55 || head[511] != 0xAA
            || sectorShift is < 9 or > 12 || clusterShift > 25 - sectorShift)
        {
            throw Refuse(FatGrowSkipReason.NotFat);
        }

        var bps = 1 << sectorShift;
        long spc = 1L << clusterShift;
        var clusterSize = bps << clusterShift;
        if (volume.Length < 2L * BootRegionSectors * bps)
        {
            throw Refuse(FatGrowSkipReason.NotFat);
        }

        var region = new byte[BootRegionSectors * bps];
        FatGrowIo.ReadInto(volume, 0, region);
        if (BootChecksum(region, bps) != BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(11 * bps)))
        {
            throw Refuse(FatGrowSkipReason.InconsistentMetadata);
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(region.AsSpan(106));
        if (region[110] != 1 || (flags & 0x01) != 0)
        {
            // Two FATs and bitmaps: TexFAT.
            throw Refuse(FatGrowSkipReason.UnsupportedLayout);
        }

        // VolumeDirty or MediaFailure.
        if ((flags & 0x06) != 0)
        {
            throw Refuse(FatGrowSkipReason.VolumeDirty);
        }

        var oldTotalRaw = BinaryPrimitives.ReadUInt64LittleEndian(region.AsSpan(72));
        long fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(80));
        long fatLength = BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(84));
        long heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(88));
        long clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(92));
        long rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(96));
        var limit = volume.Length / bps;
        if (oldTotalRaw > (ulong)limit
            || fatOffset < 2 * BootRegionSectors || fatOffset + fatLength > heapOffset
            || clusterCount < 1 || heapOffset + (clusterCount * spc) > (long)oldTotalRaw
            || fatLength * (bps / 4) < clusterCount + 2
            || rootCluster < 2 || rootCluster >= clusterCount + 2)
        {
            throw Refuse(FatGrowSkipReason.InconsistentMetadata);
        }

        var oldTotal = (long)oldTotalRaw;
        if (clusterCount + 2 >= int.MaxValue)
        {
            throw Refuse(FatGrowSkipReason.UnsupportedLayout);
        }

        // ---- new geometry: the heap moves forward by k clusters, the FAT takes the room ----
        long k = 0, newHeap, newCount;
        while (true)
        {
            newHeap = heapOffset + (k * spc);
            if (newHeap > uint.MaxValue || newHeap + spc > limit)
            {
                throw Refuse(FatGrowSkipReason.AlreadyFillsPartition);
            }

            newCount = Math.Min((limit - newHeap) / spc, ExFatFormatter.MaxClusters);
            if ((newHeap - fatOffset) * (bps / 4) >= newCount + 2)
            {
                break;
            }

            k++;
        }

        if (newCount <= clusterCount)
        {
            throw Refuse(clusterCount >= ExFatFormatter.MaxClusters ? FatGrowSkipReason.ClusterLimit : FatGrowSkipReason.AlreadyFillsPartition);
        }

        var limited = (limit - newHeap) / spc > newCount;
        var newTotal = limited ? newHeap + (newCount * spc) : limit;
        var newFatLength = FatGrowIo.CeilDiv((newCount + 2) * 4, bps);
        long OldPosition(long cluster) => (heapOffset * bps) + ((cluster - 2) * clusterSize);
        long NewPosition(long cluster) => (newHeap * bps) + ((cluster - 2) * clusterSize);
        bool InRange(long cluster) => cluster >= 2 && cluster < clusterCount + 2;

        // ---- walk the whole tree: which clusters belong to what ----
        var fat = FatGrowIo.ReadFat(volume, fatOffset * bps, clusterCount + 2, ct);
        var visited = new BitArray((int)(clusterCount + 2));

        // The chains as they will be carried over; stays zero for contiguous files, which have none.
        var chains = new uint[clusterCount + 2];

        void Walk(long first, long count, bool contiguous, List<uint>? collect)
        {
            // count < 0: a chain of unknown length (the root directory).
            var c = first;
            for (long done = 0; ; done++)
            {
                if (!InRange(c) || visited[(int)c])
                {
                    throw Refuse(FatGrowSkipReason.InconsistentMetadata);
                }

                visited[(int)c] = true;
                collect?.Add((uint)c);
                if (contiguous)
                {
                    if (done + 1 == count)
                    {
                        return;
                    }

                    c++;
                    continue;
                }

                var next = fat[c];
                chains[c] = next;
                var last = done + 1 == count;
                if (next == EndOfChain || last)
                {
                    // Too short or too long for the recorded size.
                    if (next != EndOfChain || (count >= 0 && !last))
                    {
                        throw Refuse(FatGrowSkipReason.InconsistentMetadata);
                    }

                    return;
                }

                c = next;
            }
        }

        byte[] ReadClusters(List<uint> clusters)
        {
            var data = new byte[(long)clusters.Count * clusterSize];
            for (var i = 0; i < clusters.Count; i++)
            {
                FatGrowIo.ReadInto(volume, OldPosition(clusters[i]), data.AsSpan(i * clusterSize, clusterSize));
            }

            return data;
        }

        var directories = new List<DirectoryData>();
        var items = new List<Item>();
        long directoryBytes = 0;

        int AddDirectory(long first, long count, bool contiguous)
        {
            var clusters = new List<uint>();
            Walk(first, count, contiguous, clusters);
            directoryBytes += (long)clusters.Count * clusterSize;
            if (directoryBytes > MaxDirectoryBytes)
            {
                throw Refuse(FatGrowSkipReason.UnsupportedLayout);
            }

            directories.Add(new DirectoryData(clusters.ToArray(), ReadClusters(clusters)));
            return directories.Count - 1;
        }

        List<uint>? bitmapClusters = null, upcaseClusters = null;
        int bitmapEntry = -1, upcaseEntry = -1;

        AddDirectory(rootCluster, -1, contiguous: false);
        for (var d = 0; d < directories.Count; d++)
        {
            ct.ThrowIfCancellationRequested();
            var data = directories[d].Data;
            for (var i = 0; i + EntrySize <= data.Length;)
            {
                var type = data[i];
                if (type == 0x00)
                {
                    break;
                }

                if ((type & 0x80) == 0 || type is 0x83 or 0xA0)
                {
                    // Not in use, the volume label, or the volume GUID.
                    i += EntrySize;
                    continue;
                }

                if (type is 0x81 or 0x82)
                {
                    long first = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 20));
                    var length = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(i + 24));
                    var isBitmap = type == 0x81;
                    if (d != 0 || length == 0 || length > int.MaxValue
                        || (isBitmap ? bitmapClusters != null || (data[i + 1] & 1) != 0 || (long)length < FatGrowIo.CeilDiv(clusterCount, 8) : upcaseClusters != null))
                    {
                        throw Refuse(isBitmap && d == 0 && (data[i + 1] & 1) != 0 ? FatGrowSkipReason.UnsupportedLayout : FatGrowSkipReason.InconsistentMetadata);
                    }

                    var clusters = new List<uint>();
                    Walk(first, FatGrowIo.CeilDiv((long)length, clusterSize), contiguous: false, clusters);
                    if (isBitmap)
                    {
                        (bitmapClusters, bitmapEntry) = (clusters, i);
                    }
                    else
                    {
                        (upcaseClusters, upcaseEntry) = (clusters, i);
                    }

                    i += EntrySize;
                    continue;
                }

                if (type != 0x85)
                {
                    // Vendor or TexFAT entries may own clusters in ways unknown here.
                    throw Refuse(FatGrowSkipReason.UnsupportedLayout);
                }

                int secondaries = data[i + 1];
                var setLength = (secondaries + 1) * EntrySize;
                var stream = i + EntrySize;
                if (secondaries < 2 || i + setLength > data.Length || data[stream] != 0xC0)
                {
                    throw Refuse(FatGrowSkipReason.InconsistentMetadata);
                }

                for (var j = 2; j <= secondaries; j++)
                {
                    var secondary = data[i + (j * EntrySize)];
                    var ownsClusters = (data[i + (j * EntrySize) + 1] & 1) != 0;
                    if (secondary != 0xC1 && ((secondary & 0xC0) != 0xC0 || ownsClusters))
                    {
                        throw Refuse(FatGrowSkipReason.UnsupportedLayout);
                    }
                }

                if (SetChecksum(data.AsSpan(i, setLength)) != BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 2)))
                {
                    throw Refuse(FatGrowSkipReason.InconsistentMetadata);
                }

                var isDirectory = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 4)) & 0x10) != 0;
                var streamFlags = data[stream + 1];
                var size = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(stream + 24));
                if ((streamFlags & 1) != 0 && size > 0)
                {
                    if (size > long.MaxValue / 2 || (isDirectory && size % (ulong)clusterSize != 0))
                    {
                        throw Refuse(FatGrowSkipReason.InconsistentMetadata);
                    }

                    var item = new Item
                    {
                        Directory = d,
                        EntryOffset = stream,
                        SetOffset = i,
                        SetLength = setLength,
                        First = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(stream + 20)),
                        Count = FatGrowIo.CeilDiv((long)size, clusterSize),
                        Contiguous = (streamFlags & 2) != 0,
                    };
                    items.Add(item);
                    if (isDirectory)
                    {
                        AddDirectory(item.First, item.Count, item.Contiguous);
                    }
                    else
                    {
                        Walk(item.First, item.Count, item.Contiguous, null);
                    }
                }

                i += setLength;
            }
        }

        if (bitmapClusters == null || upcaseClusters == null)
        {
            throw Refuse(FatGrowSkipReason.InconsistentMetadata);
        }

        // Everything reachable must be marked allocated; the reverse (lost clusters) is harmless and kept.
        var oldBitmap = ReadClusters(bitmapClusters);
        bool Allocated(long cluster) => (oldBitmap[(cluster - 2) / 8] & (1 << (int)((cluster - 2) % 8))) != 0;
        for (long c = 2; c < clusterCount + 2; c++)
        {
            if (visited[(int)c] && !Allocated(c))
            {
                throw Refuse(FatGrowSkipReason.InconsistentMetadata);
            }
        }

        // ---- the new allocation bitmap ----
        var newBitmapBytes = FatGrowIo.CeilDiv(newCount, 8);
        var newBitmapClusters = FatGrowIo.CeilDiv(newBitmapBytes, clusterSize);
        var newBitmap = new byte[newBitmapBytes];
        static bool IsRun(List<uint> clusters) => clusters[^1] - clusters[0] == clusters.Count - 1;

        // Drivers read the bitmap and the up-case table as contiguous runs, so these two only move as a whole.
        var bitmapStays = newBitmapClusters == bitmapClusters.Count && bitmapClusters[0] >= k + 2 && IsRun(bitmapClusters);
        var upcaseStays = upcaseClusters[0] >= k + 2 && upcaseClusters.TrueForAll(c => c >= k + 2);
        var upcaseData = upcaseStays ? null : ReadClusters(upcaseClusters);
        void Release(List<uint> clusters)
        {
            foreach (var c in clusters)
            {
                visited[(int)c] = false;
                chains[c] = 0;
                oldBitmap[(c - 2) / 8] &= (byte)~(1 << (int)((c - 2) % 8));
            }
        }

        if (!bitmapStays)
        {
            Release(bitmapClusters);
        }

        if (!upcaseStays)
        {
            Release(upcaseClusters);
        }

        void Mark(long bit) => newBitmap[bit / 8] |= (byte)(1 << (int)(bit % 8));
        bool IsFree(long bit) => (newBitmap[bit / 8] & (1 << (int)(bit % 8))) == 0;
        for (var c = k + 2; c < clusterCount + 2; c++)
        {
            if (Allocated(c))
            {
                Mark(c - k - 2);
            }
        }

        // Free space is taken behind the old end first: nothing was ever stored there.
        var tailStart = Math.Max(0, clusterCount - k);
        long highest = Math.Max(1, clusterCount + 1 - k);

        long AllocateRun(long length)
        {
            foreach (var (from, to) in new[] { (tailStart, newCount), (0L, Math.Min(newCount, tailStart + length - 1)) })
            {
                long run = 0;
                for (var bit = from; bit < to; bit++)
                {
                    run = IsFree(bit) ? run + 1 : 0;
                    if (run == length)
                    {
                        var start = bit - length + 1;
                        for (var b = start; b <= bit; b++)
                        {
                            Mark(b);
                        }

                        highest = Math.Max(highest, bit + 2);
                        return start + 2;
                    }
                }
            }

            throw Refuse(FatGrowSkipReason.NoFreeSpace);
        }

        var cursor = tailStart;
        var wrapped = false;
        long AllocateOne()
        {
            while (true)
            {
                if (cursor >= (wrapped ? tailStart : newCount))
                {
                    if (wrapped)
                    {
                        throw Refuse(FatGrowSkipReason.NoFreeSpace);
                    }

                    wrapped = true;
                    cursor = 0;
                    continue;
                }

                var bit = cursor++;
                if (IsFree(bit))
                {
                    Mark(bit);
                    highest = Math.Max(highest, bit + 2);
                    return bit + 2;
                }
            }
        }

        var newBitmapFirst = bitmapStays ? 0 : AllocateRun(newBitmapClusters);
        var newUpcaseFirst = upcaseStays ? 0 : AllocateRun(upcaseClusters.Count);

        // A contiguous file that loses clusters to the FAT becomes an ordinary chained one.
        var evicted = (int)Math.Min(k, clusterCount);
        var converted = new HashSet<Item>();
        foreach (var item in items)
        {
            if (item.Contiguous && item.First < evicted + 2)
            {
                for (var c = item.First; c < item.First + item.Count; c++)
                {
                    chains[c] = c == item.First + item.Count - 1 ? EndOfChain : (uint)(c + 1);
                }

                converted.Add(item);
            }
        }

        var relocation = new uint[evicted];
        long moved = 0;
        for (var c = 2; c < evicted + 2; c++)
        {
            if (visited[c])
            {
                relocation[c - 2] = (uint)AllocateOne();
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
            return target != 0 ? target : throw Refuse(FatGrowSkipReason.InconsistentMetadata);
        }

        // ---- the FAT in the new numbering ----
        var newFat = new uint[highest + 1];
        newFat[0] = fat[0];
        newFat[1] = fat[1];
        for (long c = 2; c < clusterCount + 2; c++)
        {
            var next = chains[c];
            if (next != 0)
            {
                newFat[Map(c)] = next == EndOfChain ? EndOfChain : Map(next);
            }
            else if (fat[c] == BadCluster && c >= k + 2 && !visited[(int)c])
            {
                newFat[c - k] = BadCluster;
            }
        }

        void WriteRun(long first, long count)
        {
            for (var c = first; c < first + count; c++)
            {
                newFat[c] = c == first + count - 1 ? EndOfChain : (uint)(c + 1);
            }
        }

        if (!bitmapStays)
        {
            WriteRun(newBitmapFirst, newBitmapClusters);
        }

        if (!upcaseStays)
        {
            WriteRun(newUpcaseFirst, upcaseClusters.Count);
        }

        // ---- directory entries ----
        var root = directories[0].Data;
        var bitmapFirst = bitmapStays ? Map(bitmapClusters[0]) : (uint)newBitmapFirst;
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(bitmapEntry + 20), bitmapFirst);
        BinaryPrimitives.WriteUInt64LittleEndian(root.AsSpan(bitmapEntry + 24), (ulong)newBitmapBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(upcaseEntry + 20), upcaseStays ? Map(upcaseClusters[0]) : (uint)newUpcaseFirst);
        if (k > 0)
        {
            foreach (var item in items)
            {
                var data = directories[item.Directory].Data;
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(item.EntryOffset + 20), Map(item.First));
                if (converted.Contains(item))
                {
                    data[item.EntryOffset + 1] &= unchecked((byte)~2);
                }

                var set = data.AsSpan(item.SetOffset, item.SetLength);
                BinaryPrimitives.WriteUInt16LittleEndian(set[2..], SetChecksum(set));
            }
        }

        long allocated = 0;
        foreach (var b in newBitmap)
        {
            allocated += BitOperations.PopCount(b);
        }

        var newRoot = Map(rootCluster);

        void WriteBitmap(long cluster)
        {
            var position = NewPosition(cluster);
            volume.Write(position, newBitmap);
            var padding = (newBitmapClusters * clusterSize) - newBitmapBytes;
            if (padding > 0)
            {
                volume.Zero(position + newBitmapBytes, padding, CancellationToken.None);
            }
        }

        // ---- nothing was written so far. First the copies into free space: harmless if interrupted ----
        FatGrowIo.CopyClusters(
            volume,
            Enumerable.Range(0, evicted).Where(i => relocation[i] != 0).Select(i => ((long)i + 2, (long)relocation[i])),
            OldPosition,
            NewPosition,
            clusterSize,
            ct);
        if (upcaseData != null)
        {
            volume.Write(NewPosition(newUpcaseFirst), upcaseData);
        }

        if (!bitmapStays)
        {
            WriteBitmap(newBitmapFirst);
        }

        volume.Flush();
        ct.ThrowIfCancellationRequested();

        // ---- from here on the volume is inconsistent until the boot region is written: no cancellation ----
        for (var d = 0; d < directories.Count; d++)
        {
            if (d != 0 && k == 0)
            {
                break;
            }

            var directory = directories[d];
            for (var i = 0; i < directory.Clusters.Length; i++)
            {
                volume.Write(NewPosition(Map(directory.Clusters[i])), directory.Data.AsSpan(i * clusterSize, clusterSize));
            }
        }

        if (bitmapStays)
        {
            WriteBitmap(bitmapFirst);
        }

        FatGrowIo.WriteFat(volume, fatOffset * bps, newFat, newFatLength * bps);

        BinaryPrimitives.WriteUInt64LittleEndian(region.AsSpan(72), (ulong)newTotal);
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(84), (uint)newFatLength);
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(88), (uint)newHeap);
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(92), (uint)newCount);
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(96), newRoot);
        region[112] = (byte)(allocated * 100 / newCount);
        var checksum = BootChecksum(region, bps);
        for (var i = 11 * bps; i < 12 * bps; i += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(i), checksum);
        }

        // The backup first; the main boot region last makes the new layout take effect.
        volume.Write((long)BootRegionSectors * bps, region);
        volume.Write(0, region);
        volume.Flush();

        return new FatGrowResult(oldTotal, newTotal, bps, FatGrowSkipReason.None) { Limited = limited, ClustersMoved = moved };
    }

    /// <summary>Checksum of boot sectors 0-10, without VolumeFlags and PercentInUse.</summary>
    private static uint BootChecksum(ReadOnlySpan<byte> region, int bytesPerSector)
    {
        uint checksum = 0;
        for (var i = 0; i < 11 * bytesPerSector; i++)
        {
            if (i is 106 or 107 or 112)
            {
                continue;
            }

            checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + region[i];
        }

        return checksum;
    }

    /// <summary>Checksum of a file entry set, without the checksum field itself.</summary>
    private static ushort SetChecksum(ReadOnlySpan<byte> set)
    {
        ushort checksum = 0;
        for (var i = 0; i < set.Length; i++)
        {
            if (i is 2 or 3)
            {
                continue;
            }

            checksum = (ushort)(((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]);
        }

        return checksum;
    }
}
