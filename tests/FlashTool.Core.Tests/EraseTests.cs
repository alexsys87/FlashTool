using System.Buffers.Binary;
using FlashTool.Core.FileSystems.ExFat;
using FlashTool.Core.FileSystems.Fat;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Tests;

public class EraseTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    [Fact]
    public async Task Quick_erase_replaces_old_layout_with_fat32()
    {
        using var dir = new TempDir();
        var path = dir.File("disk.bin");
        WriteOldImage(path, 64 * MiB);

        var result = await EraseDeviceOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(path),
            new EraseOptions { Mode = EraseMode.Quick, FileSystem = EraseFileSystem.Fat32, Label = "my card" },
            null,
            CancellationToken.None);

        var disk = File.ReadAllBytes(path);
        var mbr = MbrPartitionTable.TryParse(disk.AsSpan(0, 512))!;
        var entry = Assert.Single(mbr.Entries);
        Assert.Equal(0x0C, entry.Type);
        Assert.Equal(2048u, entry.FirstLba);
        Assert.Equal((uint)((disk.Length / 512) - 2048), entry.SectorCount);

        var partition = disk.AsSpan(2048 * 512);
        Assert.Equal(FileSystemType.Fat32, FileSystemProbe.Detect(partition));
        Assert.Equal("MY CARD    "u8.ToArray(), partition.Slice(71, 11).ToArray());
        Assert.Equal(partition[..512].ToArray(), partition.Slice(6 * 512, 512).ToArray());

        var fat = result.Layout!.Fat32!;
        Assert.Equal(fat.TotalSectors, BinaryPrimitives.ReadUInt32LittleEndian(partition[32..]));
        Assert.True(fat.ClusterCount >= Fat32Formatter.MinClusters);
        Assert.Equal(0, fat.DataStartSector * 512 % MiB);

        // The old ext4 superblock (old partition 2 at LBA 4096) is gone.
        Assert.True(disk.AsSpan((4096 * 512) + 1024, 1024).IndexOfAnyExcept((byte)0) < 0);
        Assert.False(result.Verified);
    }

    [Fact]
    public async Task Quick_erase_creates_exfat_on_gpt()
    {
        using var dir = new TempDir();
        var path = dir.File("disk.bin");
        WriteOldImage(path, 48 * MiB);

        var result = await EraseDeviceOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(path),
            new EraseOptions { FileSystem = EraseFileSystem.ExFat, PartitionScheme = ErasePartitionScheme.Gpt, Label = "Диск" },
            null,
            CancellationToken.None);

        var disk = File.ReadAllBytes(path);
        var gpt = GptPartitionTable.TryRead((o, c) => o + c <= disk.Length ? disk.AsSpan((int)o, c).ToArray() : null)!;
        Assert.True(gpt.EntriesCrcValid);
        var entry = Assert.Single(gpt.Entries);
        Assert.Equal(PartitionTableWriter.BasicDataPartitionType, entry.TypeGuid);
        Assert.Equal(2048, entry.FirstLba);

        var backup = GptHeader.TryParse(disk.AsSpan(disk.Length - 512, 512))!;
        Assert.Equal(1, backup.AlternateLba);
        Assert.Equal(gpt.Header.EntriesCrc32, backup.EntriesCrc32);

        var partition = disk.AsSpan(2048 * 512, (int)result.Layout!.PartitionLength);
        Assert.Equal(FileSystemType.ExFat, FileSystemProbe.Detect(partition));

        // Main and backup boot regions are identical and carry a valid checksum.
        Assert.Equal(partition[..(12 * 512)].ToArray(), partition.Slice(12 * 512, 12 * 512).ToArray());
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(partition[(11 * 512)..]);
        Assert.Equal(checksum, BootChecksum(partition[..(11 * 512)]));

        var layout = result.Layout.ExFat!;
        Assert.Equal(layout.ClusterCount, BinaryPrimitives.ReadUInt32LittleEndian(partition[92..]));
        Assert.Equal(0, layout.ClusterHeapOffset * 512 % MiB);
    }

    [Fact]
    public async Task Full_erase_zeroes_the_whole_device()
    {
        using var dir = new TempDir();
        var path = dir.File("disk.bin");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0xAB, (int)(5 * MiB) + 4096).ToArray());

        var result = await EraseDeviceOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(path),
            new EraseOptions { Mode = EraseMode.Full, FileSystem = EraseFileSystem.None, ChunkSize = 1024 * 1024 },
            null,
            CancellationToken.None);

        Assert.Equal(new FileInfo(path).Length, result.BytesOverwritten);
        Assert.Null(result.Layout);
        Assert.True(File.ReadAllBytes(path).AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Theory]
    [InlineData(ErasePattern.Random)]
    [InlineData(ErasePattern.Zeros)]
    [InlineData(ErasePattern.Ones)]
    public async Task Secure_erase_writes_and_verifies_the_pattern(ErasePattern pattern)
    {
        using var dir = new TempDir();
        var path = dir.File("disk.bin");
        WriteOldImage(path, 3 * MiB);

        var result = await EraseDeviceOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(path),
            new EraseOptions { Mode = EraseMode.Secure, Pattern = pattern, FileSystem = EraseFileSystem.None, ChunkSize = 256 * 1024, PatternSeed = 42 },
            null,
            CancellationToken.None);

        Assert.True(result.Verified);
        var disk = File.ReadAllBytes(path);
        var expected = new byte[disk.Length];
        EraseDeviceOperation.FillPattern(expected, 0, pattern, 42);
        Assert.Equal(expected, disk);
    }

    [Fact]
    public void Random_pattern_depends_on_position_only()
    {
        var whole = new byte[64 * 1024];
        EraseDeviceOperation.FillPattern(whole, 0, ErasePattern.Random, 7);
        var part = new byte[4096];
        EraseDeviceOperation.FillPattern(part, 8192, ErasePattern.Random, 7);

        Assert.Equal(whole.AsSpan(8192, 4096).ToArray(), part);
        Assert.NotEqual(whole.AsSpan(0, 4096).ToArray(), part);
    }

    [Fact]
    public async Task Secure_erase_detects_media_that_wraps_around()
    {
        using var dir = new TempDir();
        var path = dir.File("disk.bin");

        // Reports 8 MiB but stores only 4: the second half overwrites the first.
        var error = await Assert.ThrowsAsync<VerificationFailedException>(() => EraseDeviceOperation.RunAsync(
            () => new WrappingDevice(HandleBlockDevice.OpenFile(path, 4 * MiB)),
            new EraseOptions { Mode = EraseMode.Secure, FileSystem = EraseFileSystem.None, ChunkSize = 256 * 1024 },
            null,
            CancellationToken.None));

        Assert.Equal(0, error.Offset);
    }

    [Theory]
    [InlineData(16L * GiB, ErasePartitionScheme.Mbr, FileSystemType.Fat32)]
    [InlineData(64L * GiB, ErasePartitionScheme.Mbr, FileSystemType.ExFat)]
    [InlineData(8L * MiB, ErasePartitionScheme.Mbr, FileSystemType.ExFat)]
    [InlineData(3L * 1024 * GiB, ErasePartitionScheme.Gpt, FileSystemType.ExFat)]
    public void Auto_picks_the_file_system_by_size(long size, ErasePartitionScheme scheme, FileSystemType expected)
    {
        var layout = EraseDeviceOperation.Plan(size, 512, new EraseOptions { PartitionScheme = scheme })!;
        Assert.Equal(expected, layout.FileSystem);
        Assert.Equal(size >= GiB ? 4 * MiB : MiB, layout.PartitionOffset);
    }

    [Fact]
    public void Plan_rejects_unsupported_layouts()
    {
        var mbr = Assert.Throws<FlashToolException>(() => EraseDeviceOperation.Plan(3L * 1024 * GiB, 512, new EraseOptions()));
        Assert.Equal(FlashErrorCode.DeviceTooLargeForMbr, mbr.Code);

        var fat = Assert.Throws<FlashToolException>(() => EraseDeviceOperation.Plan(16 * MiB, 512, new EraseOptions { FileSystem = EraseFileSystem.Fat32 }));
        Assert.Equal(FlashErrorCode.FileSystemSizeNotSupported, fat.Code);

        Assert.Null(EraseDeviceOperation.Plan(16 * MiB, 512, new EraseOptions { FileSystem = EraseFileSystem.None }));
    }

    [Theory]
    [InlineData("my card", "MY CARD")]
    [InlineData("  a:b*c  ", "A_B_C")]
    [InlineData("very long label", "VERY LONG L")]
    [InlineData("Флешка", "______")]
    public void Fat32_label_is_sanitized(string input, string expected)
        => Assert.Equal(expected, Fat32Formatter.SanitizeLabel(input));

    [Fact]
    public void ExFat_label_keeps_unicode()
    {
        Assert.Equal("Флешка", ExFatFormatter.SanitizeLabel("Флешка"));
        Assert.Equal("a_b", ExFatFormatter.SanitizeLabel("a:b"));
        Assert.Equal(11, ExFatFormatter.SanitizeLabel("123456789012345").Length);
    }

    /// <summary>A sparse device file whose start holds the MBR test image (FAT32 + ext4 partitions).</summary>
    private static void WriteOldImage(string path, long length)
    {
        var image = TestImages.CreateMbrImage();
        using var file = File.Create(path);
        file.SetLength(length);
        file.Write(image, 0, (int)Math.Min(image.Length, length));
    }

    private static uint BootChecksum(ReadOnlySpan<byte> sectors)
    {
        uint checksum = 0;
        for (var i = 0; i < sectors.Length; i++)
        {
            if (i is 106 or 107 or 112)
            {
                continue;
            }

            checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + sectors[i];
        }

        return checksum;
    }

    /// <summary>Counterfeit media: twice the real capacity, addresses wrap around.</summary>
    private sealed class WrappingDevice(IBlockDevice inner) : IBlockDevice
    {
        public long Length => inner.Length * 2;

        public int SectorSize => inner.SectorSize;

        public bool CanWrite => inner.CanWrite;

        public void Read(long offset, Span<byte> buffer) => inner.Read(offset % inner.Length, buffer);

        public void Write(long offset, ReadOnlySpan<byte> buffer) => inner.Write(offset % inner.Length, buffer);

        public void Flush() => inner.Flush();

        public void Dispose() => inner.Dispose();
    }
}
