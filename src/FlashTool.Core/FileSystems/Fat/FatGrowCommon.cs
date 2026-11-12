using System.Buffers.Binary;

namespace FlashTool.Core.FileSystems.Fat;

/// <summary>Why a FAT32 or exFAT file system was left untouched.</summary>
public enum FatGrowSkipReason
{
    None,
    NotFat,
    AlreadyFillsPartition,

    /// <summary>FAT12/FAT16: growing would mean converting to FAT32.</summary>
    Fat12Or16,

    /// <summary>Marked as not cleanly removed (or with I/O errors): it needs chkdsk first.</summary>
    VolumeDirty,

    /// <summary>A layout this code does not rearrange (inactive FAT mirror, TexFAT, vendor entries...).</summary>
    UnsupportedLayout,

    InconsistentMetadata,

    /// <summary>No free clusters to move the data that is in the way of the larger FAT.</summary>
    NoFreeSpace,

    /// <summary>The volume already has as many clusters as its cluster size allows.</summary>
    ClusterLimit,
}

/// <param name="OldSectors">Volume size before, in file system sectors.</param>
/// <param name="NewSectors">Volume size after.</param>
public sealed record FatGrowResult(long OldSectors, long NewSectors, int BytesPerSector, FatGrowSkipReason SkipReason)
{
    public bool Grown => NewSectors > OldSectors && SkipReason == FatGrowSkipReason.None;

    /// <summary>The cluster size did not allow filling the whole partition; the volume grew as far as it could.</summary>
    public bool Limited { get; init; }

    /// <summary>Clusters whose data had to move out of the way of the larger FAT.</summary>
    public long ClustersMoved { get; init; }
}

/// <summary>Thrown inside the planning phase (nothing written yet) to refuse a volume.</summary>
internal sealed class FatGrowRefusedException(FatGrowSkipReason reason) : Exception(reason.ToString())
{
    public FatGrowSkipReason Reason { get; } = reason;
}

/// <summary>I/O shared by the FAT32 and exFAT growers.</summary>
internal static class FatGrowIo
{
    private const int MaxChunk = 4 * 1024 * 1024;

    /// <summary>Reads a range of any length in chunks.</summary>
    public static void ReadInto(PartitionAccess volume, long position, Span<byte> destination)
    {
        for (var done = 0; done < destination.Length;)
        {
            var count = Math.Min(MaxChunk, destination.Length - done);
            volume.Read(position + done, count).CopyTo(destination[done..]);
            done += count;
        }
    }

    /// <summary>Reads <paramref name="entries"/> 32-bit FAT entries starting at <paramref name="position"/>.</summary>
    public static uint[] ReadFat(PartitionAccess volume, long position, long entries, CancellationToken cancellationToken)
    {
        var fat = new uint[entries];
        const int perChunk = MaxChunk / 4;
        for (long done = 0; done < entries; done += perChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(perChunk, entries - done);
            var raw = volume.Read(position + (done * 4), count * 4);
            for (var i = 0; i < count; i++)
            {
                fat[done + i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
            }
        }

        return fat;
    }

    /// <summary>Writes the FAT entries and zero-fills the rest of the <paramref name="totalBytes"/> of the table.</summary>
    public static void WriteFat(PartitionAccess volume, long position, uint[] fat, long totalBytes)
    {
        const int perChunk = MaxChunk / 4;
        var buffer = new byte[Math.Min(perChunk, Math.Max(fat.Length, 1)) * 4];
        for (var done = 0; done < fat.Length; done += perChunk)
        {
            var count = Math.Min(perChunk, fat.Length - done);
            for (var i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * 4), fat[done + i]);
            }

            volume.Write(position + ((long)done * 4), buffer.AsSpan(0, count * 4));
        }

        var written = (long)fat.Length * 4;
        if (written < totalBytes)
        {
            volume.Zero(position + written, totalBytes - written, CancellationToken.None);
        }
    }

    /// <summary>
    /// Copies clusters to their new places, merging neighbours into larger transfers.
    /// <paramref name="moves"/> must be ordered by source cluster.
    /// </summary>
    public static void CopyClusters(
        PartitionAccess volume,
        IEnumerable<(long Source, long Target)> moves,
        Func<long, long> sourcePosition,
        Func<long, long> targetPosition,
        int clusterSize,
        CancellationToken cancellationToken)
    {
        var maxRun = Math.Max(1, MaxChunk / clusterSize);
        long runSource = 0, runTarget = 0;
        var runLength = 0;

        void Flush()
        {
            if (runLength > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new byte[runLength * clusterSize];
                ReadInto(volume, sourcePosition(runSource), data);
                volume.Write(targetPosition(runTarget), data);
                runLength = 0;
            }
        }

        foreach (var (source, target) in moves)
        {
            if (runLength > 0 && runLength < maxRun && source == runSource + runLength && target == runTarget + runLength)
            {
                runLength++;
                continue;
            }

            Flush();
            runSource = source;
            runTarget = target;
            runLength = 1;
        }

        Flush();
    }

    public static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;
}
