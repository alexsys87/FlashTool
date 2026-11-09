using System.Buffers.Binary;
using FlashTool.Core.Containers;
using FlashTool.Core.FileSystems;
using FlashTool.Core.FileSystems.Ext4;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;
using Xunit.Abstractions;
using static FlashTool.Core.Tests.Ext4TestDisk;

namespace FlashTool.Core.Tests;

/// <summary>
/// Creating an image with free ext2/3/4 blocks stored as zeros. File systems are made by
/// mke2fs on top of non-zero data, so skipped blocks show up as differences; dumpe2fs is the
/// independent judge of which blocks are free. Runs where e2fsprogs is installed (Linux).
/// </summary>
public class SkipFreeBlocksTests(ITestOutputHelper output)
{
    public static TheoryData<string, string, int> Cases => new()
    {
        // name, mke2fs options, size in MiB
        { "ext4 default (64bit, metadata_csum, flex_bg, BLOCK_UNINIT groups)", "-t ext4", 512 },
        { "ext4 without 64bit (32-byte descriptors)", "-t ext4 -O ^64bit", 384 },
        { "ext4 with gdt_csum instead of metadata_csum", "-t ext4 -O ^metadata_csum,uninit_bg", 384 },
        { "ext4 without flex_bg (metadata inside each group)", "-t ext4 -O ^flex_bg", 384 },
        { "ext4 with meta_bg", "-t ext4 -O meta_bg,^resize_inode", 384 },
        { "ext4 with sparse_super2", "-t ext4 -O sparse_super2", 384 },
        { "ext4 with csum_seed", "-t ext4 -O metadata_csum_seed", 256 },
        { "ext4 with 1k blocks", "-t ext4 -b 1024", 128 },
        { "ext3", "-t ext3", 256 },
        { "ext2 (no checksums, every bitmap on disk)", "-t ext2", 256 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Free_blocks_become_zeros_and_the_file_system_stays_intact(string name, string options, int sizeMiB)
    {
        if (Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var files = MakeFileSystem(dir, options, sizeMiB);
        var disk = MakeDisk(dir);

        var result = await CreateImageAsync(disk, dir.File("out.img"), skipFreeBlocks: true);

        var entry = Assert.Single(result.FreeSpace);
        output.WriteLine($"{name}: {entry}");
        Assert.Equal(FreeSpaceStatus.Skipped, entry.Status);
        Assert.Equal(1, entry.PartitionNumber);

        // Almost all free space is skipped, BLOCK_UNINIT groups included; only runs under the minimum are read.
        var (blockSize, freeRanges) = FreeBlocks(dir.File("fs.img"));
        var freeBytes = freeRanges.Sum(r => r.Count) * blockSize;
        output.WriteLine($"free {freeBytes:N0}, skipped {entry.SkippedBytes:N0}");
        Assert.True(entry.SkippedBytes > freeBytes * 0.9, $"Skipped {entry.SkippedBytes:N0} of {freeBytes:N0} free bytes.");
        Assert.Equal(entry.SkippedBytes, result.SkippedBytes);

        // Every difference is a zeroed free block of the file system; everything else is a faithful copy.
        var source = File.ReadAllBytes(disk);
        var image = File.ReadAllBytes(dir.File("out.img"));
        Assert.Equal(source.Length, image.Length);
        long differing = 0;
        for (var offset = 0L; offset < source.Length; offset += Sector)
        {
            var original = source.AsSpan((int)offset, Sector);
            var copy = image.AsSpan((int)offset, Sector);
            if (original.SequenceEqual(copy))
            {
                continue;
            }

            differing += Sector;
            Assert.True(copy.IndexOfAnyExcept((byte)0) < 0, $"Sector at {offset} differs but is not zero.");
            Assert.True(offset >= PartitionOffset && offset < PartitionOffset + (sizeMiB * 1024L * 1024), $"Sector at {offset} is outside the file system.");
            var block = (offset - PartitionOffset) / blockSize;
            Assert.True(freeRanges.Any(r => block >= r.First && block < r.First + r.Count), $"Block {block} was zeroed but is in use.");
        }

        Assert.True(differing > 0, "Nothing was skipped: the source free space was expected to hold data.");
        Assert.True(differing <= entry.SkippedBytes);

        // The copied file system passes fsck and holds the same files.
        var copiedFs = dir.File("copied-fs.img");
        await using (var fs = File.Create(copiedFs))
        {
            fs.Write(image, (int)PartitionOffset, sizeMiB * 1024 * 1024);
        }

        var fsck = E2fsprogs.Run(E2fsprogs.Find("e2fsck")!, $"-fn \"{copiedFs}\"", allowFailure: true);
        Assert.True(fsck.ExitCode == 0, $"e2fsck reported problems:\n{fsck.Output}");

        var restored = Path.Combine(dir.Path, "restored");
        Directory.CreateDirectory(restored);
        E2fsprogs.Run(E2fsprogs.Find("debugfs")!, $"-R \"rdump / {restored}\" \"{copiedFs}\"");
        foreach (var (relative, data) in files)
        {
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(restored, relative)));
        }
    }

    [Fact]
    public async Task Compressed_image_is_smaller_and_verifies()
    {
        if (Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        MakeFileSystem(dir, "-t ext4", 256);
        var disk = MakeDisk(dir);

        var full = await CreateImageAsync(disk, dir.File("full.img.gz"), skipFreeBlocks: false);
        var skipped = await CreateImageAsync(disk, dir.File("skipped.img.gz"), skipFreeBlocks: true);

        Assert.Empty(full.FreeSpace);
        Assert.True(skipped.Verified);
        output.WriteLine($"full {full.FileSize:N0}, skipped {skipped.FileSize:N0}");
        Assert.True(skipped.FileSize * 3 < full.FileSize, $"{skipped.FileSize:N0} vs {full.FileSize:N0}");

        // Written back, it is the same file system.
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("skipped.img.gz")));
        using var reader = container.OpenEntry(container.Entries[0]);
        await using (var target = File.Create(dir.File("restored.img")))
        {
            await reader.Data.CopyToAsync(target);
        }

        using var restored = HandleBlockDevice.OpenFile(dir.File("restored.img"), writable: false);
        var fs = new PartitionAccess(restored, PartitionOffset, 256L * 1024 * 1024);
        Assert.Equal(FreeSpaceStatus.Skipped, Ext4FreeSpace.Scan(fs, FreeBlockMap.MinimumRun, CancellationToken.None).Status);
    }

    [Theory]
    [InlineData("not cleanly unmounted", "ssv state 0", FreeSpaceStatus.NotCleanlyUnmounted)]
    [InlineData("errors recorded", "ssv state 3", FreeSpaceStatus.NotCleanlyUnmounted)]
    [InlineData("journal needs recovery", "feature needs_recovery", FreeSpaceStatus.JournalNeedsRecovery)]
    [InlineData("bigalloc", null, FreeSpaceStatus.UnsupportedFeature)]
    public async Task Unsafe_file_systems_are_copied_in_full(string name, string? debugfsCommand, FreeSpaceStatus expected)
    {
        if (Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        MakeFileSystem(dir, debugfsCommand == null ? "-t ext4 -O bigalloc -C 16384" : "-t ext4", 256);
        if (debugfsCommand != null)
        {
            E2fsprogs.Run(E2fsprogs.Find("debugfs")!, $"-w -R \"{debugfsCommand}\" \"{dir.File("fs.img")}\"");
        }

        var disk = MakeDisk(dir);
        var result = await CreateImageAsync(disk, dir.File("out.img"), skipFreeBlocks: true);

        output.WriteLine($"{name}: {result.FreeSpace.Single()}");
        Assert.Equal(expected, result.FreeSpace.Single().Status);
        Assert.Equal(0, result.SkippedBytes);
        Assert.Equal(File.ReadAllBytes(disk), File.ReadAllBytes(dir.File("out.img")));
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("block bitmap")]
    [InlineData("superblock")]
    public async Task Metadata_with_a_wrong_checksum_is_not_trusted(string what)
    {
        if (Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        MakeFileSystem(dir, "-t ext4", 256);
        var fsImage = dir.File("fs.img");
        using (var device = HandleBlockDevice.OpenFile(fsImage))
        {
            var superblock = device.ReadBytes(1024, 1024)!;
            var blockSize = 1024 << BinaryPrimitives.ReadInt32LittleEndian(superblock.AsSpan(0x18));
            var descriptor = device.ReadBytes(blockSize, 64)!;
            var offset = what switch
            {
                // Free blocks count of group 0: plausible, but no longer matching the checksum.
                "descriptor" => blockSize + 0x0C,

                // A used block marked free in the bitmap of group 0.
                "block bitmap" => (BinaryPrimitives.ReadUInt32LittleEndian(descriptor) * (long)blockSize) + 1,
                _ => 1024 + 0x10,
            };

            var b = device.ReadBytes(offset, 1)!;
            b[0] ^= 0x01;
            device.WriteBytes(offset, b);
        }

        var disk = MakeDisk(dir);
        var result = await CreateImageAsync(disk, dir.File("out.img"), skipFreeBlocks: true);

        Assert.Equal(FreeSpaceStatus.InconsistentMetadata, result.FreeSpace.Single().Status);
        Assert.Equal(File.ReadAllBytes(disk), File.ReadAllBytes(dir.File("out.img")));
    }

    [Fact]
    public async Task Only_the_part_that_is_read_is_skipped()
    {
        if (Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        MakeFileSystem(dir, "-t ext4", 256);
        var disk = MakeDisk(dir);

        // The partition table claims less than the device holds: the tail after the partition is not read.
        var result = await CreateImageAsync(disk, dir.File("out.img"), skipFreeBlocks: true, onlyAllocated: true);

        Assert.Equal(PartitionOffset + (256L * 1024 * 1024), result.BytesRead);
        Assert.Equal(FreeSpaceStatus.Skipped, result.FreeSpace.Single().Status);
        Assert.True(result.SkippedBytes < result.BytesRead);
    }

    /// <summary>Portable: only the free ranges are left unread and come back as zeros.</summary>
    [Fact]
    public void Reading_through_the_map_skips_exactly_the_free_ranges()
    {
        var data = new byte[64 * 1024];
        new Random(5).NextBytes(data);
        var device = new RecordingDevice(data);
        var map = new FreeBlockMap([(4096, 8192), (20480, 40960), (61440, 65536)]);

        var buffer = new byte[16384];
        var result = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += buffer.Length)
        {
            map.Read(device, offset, buffer);
            buffer.CopyTo(result, offset);
        }

        Assert.Equal(4096 + 20480 + 4096, map.FreeBytes);
        for (var i = 0; i < data.Length; i++)
        {
            var free = i is >= 4096 and < 8192 or >= 20480 and < 40960 or >= 61440;
            Assert.Equal(free ? (byte)0 : data[i], result[i]);
        }

        Assert.Equal(data.Length - map.FreeBytes, device.Reads.Sum(r => r.Length));
        Assert.DoesNotContain(device.Reads, r => r.Offset < 8192 && r.Offset + r.Length > 4096);
        Assert.DoesNotContain(device.Reads, r => r.Offset < 40960 && r.Offset + r.Length > 20480);
        Assert.DoesNotContain(device.Reads, r => r.Offset + r.Length > 61440);
    }

    private static Task<ReadImageResult> CreateImageAsync(string disk, string destination, bool skipFreeBlocks, bool onlyAllocated = false)
        => ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(disk, writable: false),
            destination,
            ContainerRegistry.Default,
            new ReadImageOptions
            {
                SkipFreeBlocks = skipFreeBlocks,
                OnlyAllocatedPartitions = onlyAllocated,
                Verify = true,
                ChunkSize = 64 * 1024,
                Compression = new CompressionSettings(CompressionPreset.Fastest),
            },
            null,
            CancellationToken.None);

    /// <summary>Free block ranges as dumpe2fs (libext2fs) sees them, uninitialized groups included.</summary>
    private static (int BlockSize, List<(long First, long Count)> Ranges) FreeBlocks(string fsImage)
    {
        var dump = E2fsprogs.Run(E2fsprogs.Find("dumpe2fs")!, $"\"{fsImage}\"").Output.Split('\n');
        var blockSize = int.Parse(dump.First(l => l.StartsWith("Block size:", StringComparison.Ordinal))["Block size:".Length..].Trim());
        var ranges = new List<(long, long)>();
        foreach (var line in dump.Select(l => l.Trim()).Where(l => l.StartsWith("Free blocks: ", StringComparison.Ordinal)))
        {
            foreach (var part in line["Free blocks: ".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bounds = part.Split('-');
                var first = long.Parse(bounds[0]);
                var last = bounds.Length > 1 ? long.Parse(bounds[1]) : first;
                ranges.Add((first, last - first + 1));
            }
        }

        return (blockSize, ranges);
    }

    private sealed class RecordingDevice(byte[] data) : IBlockDevice
    {
        public List<(long Offset, long Length)> Reads { get; } = [];

        public long Length => data.Length;

        public int SectorSize => Sector;

        public bool CanWrite => false;

        public void Read(long offset, Span<byte> buffer)
        {
            Reads.Add((offset, buffer.Length));
            data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
