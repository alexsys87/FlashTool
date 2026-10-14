using System.Buffers.Binary;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;

namespace FlashTool.Core.Partitioning;

public enum PartitionFixupSkipReason
{
    None,
    NoPartitionTable,
    IsoImage,
    LastPartitionIsLogical,
    AlreadyFillsDisk,
    UnsupportedSectorSize,
    NoFreeSpace,
}

/// <summary>What <see cref="PartitionTableEditor.Apply"/> changed on the device.</summary>
public sealed record PartitionFixupResult
{
    public bool GptBackupRelocated { get; init; }

    public bool PartitionExtended { get; init; }

    public PartitionInfo? Partition { get; init; }

    public long OldLength { get; init; }

    public long NewLength { get; init; }

    public bool FileSystemResized { get; init; }

    /// <summary>Set when growing the file system failed (the partition itself was extended).</summary>
    public string? FileSystemResizeError { get; init; }

    public PartitionFixupSkipReason SkipReason { get; init; }
}

/// <summary>
/// Post-write adjustments of the partition table when an image is smaller
/// than the target disk:
/// <list type="bullet">
/// <item>GPT: move the backup header/entries to the real end of the disk (always, the GPT is invalid otherwise);</item>
/// <item>optionally grow the last partition to the end of the disk (MBR and GPT).</item>
/// </list>
/// </summary>
public static class PartitionTableEditor
{
    public static PartitionFixupResult Apply(
        IBlockDevice device,
        ImageInfo image,
        bool extendLastPartition,
        IReadOnlyList<IFileSystemResizer>? resizers = null,
        CancellationToken cancellationToken = default)
    {
        // ISO hybrids carry checksummed/overlapping structures: never touch them.
        if (image.Kind is ImageKind.HybridIso or ImageKind.Iso9660)
        {
            return new PartitionFixupResult { SkipReason = PartitionFixupSkipReason.IsoImage };
        }

        if (image.TableType == PartitionTableType.Gpt)
        {
            return ApplyGpt(device, extendLastPartition, resizers, cancellationToken);
        }

        if (image.TableType == PartitionTableType.Mbr)
        {
            return extendLastPartition
                ? ApplyMbr(device, resizers, cancellationToken)
                : new PartitionFixupResult();
        }

        return new PartitionFixupResult { SkipReason = PartitionFixupSkipReason.NoPartitionTable };
    }

    private static PartitionFixupResult ApplyGpt(IBlockDevice device, bool extend, IReadOnlyList<IFileSystemResizer>? resizers, CancellationToken ct)
    {
        var ss = device.SectorSize;
        var gpt = GptPartitionTable.TryRead((o, c) => device.ReadBytes(o, c), ss)
            ?? throw new FlashToolException(FlashErrorCode.PartitionTableUpdateFailed, "Primary GPT header not found after writing.");

        var header = gpt.Header;
        var lastLba = (device.Length / ss) - 1;
        var entrySectors = gpt.EntriesSectorCount;
        var result = new PartitionFixupResult();

        var newLastUsable = lastLba - entrySectors - 1;
        var maxUsedLba = gpt.Entries.Count > 0 ? gpt.Entries.Max(e => e.LastLba) : header.FirstUsableLba;
        if (newLastUsable < maxUsedLba)
        {
            // The disk is smaller than the partitions: nothing sensible to do.
            return result with { SkipReason = PartitionFixupSkipReason.NoFreeSpace };
        }

        if (header.AlternateLba != lastLba || header.LastUsableLba != newLastUsable)
        {
            header.AlternateLba = lastLba;
            header.LastUsableLba = newLastUsable;
            result = result with { GptBackupRelocated = true };
        }

        PartitionInfo? grown = null;
        if (extend)
        {
            var last = gpt.Entries.MaxBy(e => e.LastLba);
            if (last == null)
            {
                result = result with { SkipReason = PartitionFixupSkipReason.NoPartitionTable };
            }
            else if (last.LastLba >= header.LastUsableLba)
            {
                result = result with { SkipReason = PartitionFixupSkipReason.AlreadyFillsDisk };
            }
            else
            {
                var oldLength = (last.LastLba - last.FirstLba + 1) * ss;
                gpt.SetEntryLastLba(last.Index, header.LastUsableLba);
                grown = gpt.GetPartitions().First(p => p.Number == last.Index + 1);
                result = result with { PartitionExtended = true, OldLength = oldLength, NewLength = grown.Length };
            }
        }

        if (!result.GptBackupRelocated && !result.PartitionExtended)
        {
            return result;
        }

        gpt.RecomputeEntriesCrc();

        // Primary: entries first, header last, so a crash leaves at worst a stale CRC.
        device.WriteBytes(header.PartitionEntryLba * ss, gpt.EntriesRaw);
        var primary = header.Clone();
        primary.MyLba = 1;
        primary.AlternateLba = lastLba;
        device.WriteBytes(ss, primary.ToSector(ss));

        var backup = header.Clone();
        backup.MyLba = lastLba;
        backup.AlternateLba = 1;
        backup.PartitionEntryLba = lastLba - entrySectors;
        device.WriteBytes(backup.PartitionEntryLba * ss, gpt.EntriesRaw);
        device.WriteBytes(lastLba * ss, backup.ToSector(ss));

        UpdateProtectiveMbr(device, lastLba);
        device.Flush();

        return FinishResize(device, result, grown, resizers, ct);
    }

    private static void UpdateProtectiveMbr(IBlockDevice device, long lastLba)
    {
        var mbr = device.ReadBytes(0, 512);
        if (mbr == null || !MbrPartitionTable.HasBootSignature(mbr))
        {
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            var e = mbr.AsSpan(MbrPartitionTable.EntriesOffset + (i * MbrPartitionTable.EntrySize), MbrPartitionTable.EntrySize);
            if (e[4] != MbrPartitionTable.GptProtectiveType)
            {
                continue;
            }

            var start = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var size = (uint)Math.Min(lastLba - start + 1, uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], size);
            device.WriteBytes(0, mbr);
            return;
        }
    }

    private static PartitionFixupResult ApplyMbr(IBlockDevice device, IReadOnlyList<IFileSystemResizer>? resizers, CancellationToken ct)
    {
        if (device.SectorSize != MbrPartitionTable.SectorSize)
        {
            return new PartitionFixupResult { SkipReason = PartitionFixupSkipReason.UnsupportedSectorSize };
        }

        var sector0 = device.ReadBytes(0, 512)!;
        var mbr = MbrPartitionTable.TryParse(sector0)
            ?? throw new FlashToolException(FlashErrorCode.PartitionTableUpdateFailed, "MBR not found after writing.");

        var last = mbr.Entries.Where(e => e.Type != MbrPartitionTable.GptProtectiveType).MaxBy(e => e.EndLba);
        if (last == null)
        {
            return new PartitionFixupResult { SkipReason = PartitionFixupSkipReason.NoPartitionTable };
        }

        if (MbrPartitionTable.IsExtendedType(last.Type))
        {
            // Growing a logical partition needs the EBR chain to be rewritten; not supported yet.
            return new PartitionFixupResult { SkipReason = PartitionFixupSkipReason.LastPartitionIsLogical };
        }

        var totalSectors = device.Length / MbrPartitionTable.SectorSize;
        var newCount = (uint)Math.Min(totalSectors - last.FirstLba, uint.MaxValue);
        if (newCount <= last.SectorCount)
        {
            return new PartitionFixupResult { SkipReason = PartitionFixupSkipReason.AlreadyFillsDisk };
        }

        var entry = sector0.AsSpan(MbrPartitionTable.EntriesOffset + (last.Slot * MbrPartitionTable.EntrySize), MbrPartitionTable.EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], newCount);

        // CHS end address: use the "beyond CHS range" marker, LBA fields are authoritative.
        entry[5] = 0xFE;
        entry[6] = 0xFF;
        entry[7] = 0xFF;

        device.WriteBytes(0, sector0);
        device.Flush();

        var grown = new PartitionInfo(
            last.Slot + 1,
            (long)last.FirstLba * MbrPartitionTable.SectorSize,
            (long)newCount * MbrPartitionTable.SectorSize,
            MbrPartitionTable.DescribeType(last.Type))
        {
            MbrType = last.Type,
        };

        var result = new PartitionFixupResult
        {
            PartitionExtended = true,
            OldLength = (long)last.SectorCount * MbrPartitionTable.SectorSize,
            NewLength = grown.Length,
        };

        return FinishResize(device, result, grown, resizers, ct);
    }

    private static PartitionFixupResult FinishResize(
        IBlockDevice device,
        PartitionFixupResult result,
        PartitionInfo? grown,
        IReadOnlyList<IFileSystemResizer>? resizers,
        CancellationToken ct)
    {
        if (grown == null)
        {
            return result;
        }

        var head = device.ReadBytes(grown.Offset, (int)Math.Min(FileSystemProbe.RequiredBytes, grown.Length));
        grown = grown with { FileSystem = head != null ? FileSystemProbe.Detect(head) : FileSystemType.Unknown };
        result = result with { Partition = grown };

        var resizer = resizers?.FirstOrDefault(r => r.RequiresOfflineDevice && r.CanResize(grown.FileSystem));
        if (resizer != null)
        {
            try
            {
                result = result with { FileSystemResized = resizer.Resize(device, grown, ct) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = result with { FileSystemResizeError = ex.Message };
            }
        }

        return result;
    }
}
