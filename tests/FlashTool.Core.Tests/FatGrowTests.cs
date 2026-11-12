using System.Buffers.Binary;
using System.Security.Cryptography;
using FlashTool.Core.FileSystems;
using FlashTool.Core.FileSystems.ExFat;
using FlashTool.Core.FileSystems.Fat;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;
using Xunit.Abstractions;

namespace FlashTool.Core.Tests;

/// <summary>
/// Grows FAT32 and exFAT file systems and checks that every file survives.
/// The FAT32 volumes are made and checked with dosfstools and mtools, so those tests run where they are
/// installed (Linux) and are a no-op elsewhere. The exFAT volumes are filled by a small writer of the tests'
/// own and so run everywhere; fsck.exfat and mkfs.exfat are used in addition where they exist.
/// </summary>
public class FatGrowTests(ITestOutputHelper output)
{
    private const long MiB = 1024 * 1024;

    private static readonly string? MkfsFat = E2fsprogs.Find("mkfs.fat");
    private static readonly string? Mcopy = E2fsprogs.Find("mcopy");
    private static readonly string? FsckFat = E2fsprogs.Find("fsck.fat");
    private static readonly string? FsckExFat = E2fsprogs.Find("fsck.exfat");
    private static readonly string? MkfsExFat = E2fsprogs.Find("mkfs.exfat");

    private static bool HasFatTools => MkfsFat != null && Mcopy != null && FsckFat != null;

    public static TheoryData<string, string, int, int> Fat32Cases => new()
    {
        // name, mkfs.fat options, initial MiB, final MiB
        { "512-byte clusters, FAT grows a lot", "-F 32 -s 1", 64, 1500 },
        { "4k clusters", "-F 32 -s 8", 300, 3000 },
        { "default cluster size", "-F 32", 128, 1024 },
        { "a single FAT", "-F 32 -f 1 -s 2", 100, 900 },
        { "4k sectors", "-F 32 -S 4096 -s 1", 300, 2000 },
        { "tiny growth", "-F 32 -s 1", 64, 66 },
        { "more reserved sectors, unaligned", "-F 32 -a -R 77 -s 4", 200, 800 },
        { "32k clusters", "-F 32 -s 64", 2200, 6000 },
    };

    [Theory]
    [MemberData(nameof(Fat32Cases))]
    public void Grown_fat32_passes_fsck_and_keeps_files(string name, string options, int initialMiB, int finalMiB)
    {
        if (!HasFatTools)
        {
            return;
        }

        using var dir = new TempDir();
        var image = dir.File("fat.img");
        var files = MakeFat32(dir, image, options, initialMiB);
        var before = Fat32Sectors(image);
        Resize(image, finalMiB);

        FatGrowResult result;
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            result = Fat32Grow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
        }

        output.WriteLine($"{name}: {result}");
        var fsck = E2fsprogs.Run(FsckFat!, $"-n \"{image}\"", allowFailure: true);
        output.WriteLine(fsck.Output);
        Assert.True(fsck.ExitCode == 0, $"fsck.fat reported problems:\n{fsck.Output}");

        Assert.True(result.Grown, result.SkipReason.ToString());
        Assert.False(result.Limited);
        Assert.Equal(before.Sectors, result.OldSectors);
        Assert.Equal(Fat32Sectors(image).Sectors, result.NewSectors);
        Assert.Equal(finalMiB * MiB / before.BytesPerSector, result.NewSectors);
        AssertFat32Files(dir, image, files);

        // A second run has nothing left to do.
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            var again = Fat32Grow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
            Assert.Equal(FatGrowSkipReason.AlreadyFillsPartition, again.SkipReason);
        }
    }

    [Fact]
    public void Fat32_marked_dirty_is_left_alone()
    {
        if (!HasFatTools)
        {
            return;
        }

        using var dir = new TempDir();
        var image = dir.File("fat.img");
        MakeFat32(dir, image, "-F 32 -s 1", 64);
        using (var stream = File.OpenWrite(image))
        {
            // The "mounted" bit Linux sets in the boot sector.
            stream.Position = 65;
            stream.WriteByte(0x01);
            stream.SetLength(200 * MiB);
        }

        var hash = Hash(image);
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            var outcome = new FatResizer().ResizeWithOutcome(device, Partition(device, FileSystemType.Fat32), CancellationToken.None);
            Assert.False(outcome.Resized);
            Assert.Equal(FileSystemResizeSkipReason.FatVolumeDirty, outcome.SkipReason);
        }

        Assert.Equal(hash, Hash(image));
    }

    [Fact]
    public void Fat32_with_differing_fat_copies_is_left_alone()
    {
        if (!HasFatTools)
        {
            return;
        }

        using var dir = new TempDir();
        var image = dir.File("fat.img");
        MakeFat32(dir, image, "-F 32 -s 1", 64);
        var boot = File.ReadAllBytes(image)[..512];
        var secondFat = (BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14)) + BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36))) * 512L;
        using (var stream = File.OpenWrite(image))
        {
            stream.Position = secondFat + (100 * 4);
            stream.Write([0x55, 0x00, 0x00, 0x00]);
            stream.SetLength(200 * MiB);
        }

        var hash = Hash(image);
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            var result = Fat32Grow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
            Assert.Equal(FatGrowSkipReason.InconsistentMetadata, result.SkipReason);
        }

        Assert.Equal(hash, Hash(image));
    }

    [Fact]
    public void Fat16_is_reported_as_not_growable()
    {
        if (!HasFatTools)
        {
            return;
        }

        using var dir = new TempDir();
        var image = dir.File("fat.img");
        Resize(image, 32);
        E2fsprogs.Run(MkfsFat!, $"-F 16 \"{image}\"");
        Resize(image, 100);
        var hash = Hash(image);

        using (var device = HandleBlockDevice.OpenFile(image))
        {
            // Both by the detected type and by the boot sector itself.
            Assert.Equal(FileSystemResizeSkipReason.Fat12Or16, new FatResizer().ResizeWithOutcome(device, Partition(device, FileSystemType.Fat16), CancellationToken.None).SkipReason);
            Assert.Equal(FatGrowSkipReason.Fat12Or16, Fat32Grow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None).SkipReason);
        }

        Assert.Equal(hash, Hash(image));
    }

    [Fact]
    public async Task Writing_an_image_grows_the_last_fat32_partition()
    {
        if (!HasFatTools)
        {
            return;
        }

        using var dir = new TempDir();
        var volume = dir.File("fat.img");
        var files = MakeFat32(dir, volume, "-F 32 -s 2", 100);
        var imagePath = DiskImage(dir, volume, 0x0C);

        var target = dir.File("card.bin");
        var result = await WriteAsync(imagePath, target, 1024 * MiB);

        Assert.True(result.Fixups!.PartitionExtended);
        Assert.True(result.Fixups.FileSystemResized, result.Fixups.FileSystemResizeError ?? result.Fixups.FileSystemSkipReason.ToString());
        Assert.False(result.Fixups.FileSystemGrowthLimited);
        Assert.Equal(FileSystemType.Fat32, result.Fixups.Partition!.FileSystem);
        Assert.Equal(1023 * MiB, result.Fixups.FileSystemSize);

        var partition = ExtractPartition(dir, target);
        var fsck = E2fsprogs.Run(FsckFat!, $"-n \"{partition}\"", allowFailure: true);
        Assert.True(fsck.ExitCode == 0, fsck.Output);
        AssertFat32Files(dir, partition, files);
    }

    public static TheoryData<string, int, int, int, int> ExFatCases => new()
    {
        // name, cluster size, sector size, initial MiB, final MiB
        { "4k clusters", 4096, 512, 32, 1500 },
        { "32k clusters", 32768, 512, 300, 3000 },
        { "1k clusters", 1024, 512, 16, 700 },
        { "tiny growth, bitmap stays", 4096, 512, 32, 33 },
        { "bitmap gets a second cluster", 4096, 512, 100, 200 },
        { "4k sectors", 4096, 4096, 64, 900 },
        { "128k clusters", 131072, 512, 600, 9000 },
    };

    [Theory]
    [MemberData(nameof(ExFatCases))]
    public void Grown_exfat_keeps_files(string name, int clusterSize, int sectorSize, int initialMiB, int finalMiB)
    {
        using var dir = new TempDir();
        var image = dir.File("exfat.img");
        using (var device = HandleBlockDevice.OpenFile(image, initialMiB * MiB, sectorSize))
        {
            var sectors = initialMiB * MiB / sectorSize;
            var perCluster = clusterSize / sectorSize;
            var fatOffset = 2048L * 512 / sectorSize / 2;
            var fatLength = ((((sectors - fatOffset) / perCluster) + 2) * 4 / sectorSize) + 1;
            var heap = StreamExtensions.AlignUp(fatOffset + fatLength, 2048 * 512 / sectorSize);
            var layout = new ExFatLayout(sectors, sectorSize, perCluster, fatOffset, fatLength, heap, (sectors - heap) / perCluster);
            ExFatFormatter.Format(device, 0, layout, "GROWME", 0x1234ABCD);
        }

        GrowExFatAndCheck(dir, image, name, finalMiB);
    }

    [Theory]
    [InlineData("", 64, 2000)]
    [InlineData("-c 4096", 40, 1200)]
    [InlineData("-c 65536 -b 4096", 200, 5000)]
    public void Grown_exfat_made_by_mkfs_keeps_files(string options, int initialMiB, int finalMiB)
    {
        if (MkfsExFat == null)
        {
            return;
        }

        using var dir = new TempDir();
        var image = dir.File("exfat.img");
        Resize(image, initialMiB);
        E2fsprogs.Run(MkfsExFat, $"{options} \"{image}\"");
        GrowExFatAndCheck(dir, image, $"mkfs.exfat {options}", finalMiB);
    }

    [Fact]
    public void Exfat_marked_dirty_is_left_alone()
    {
        using var dir = new TempDir();
        var image = dir.File("exfat.img");
        using (var device = HandleBlockDevice.OpenFile(image, 32 * MiB))
        {
            ExFatFormatter.Format(device, 0, ExFatFormatter.TryPlan(32 * MiB, 512, (int)MiB)!, "DIRTY", 1);
        }

        using (var volume = new ExFatTestVolume(image))
        {
            FillExFat(volume);
            volume.SetVolumeFlags(0x0002);
        }

        Resize(image, 500);
        var hash = Hash(image);
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            var outcome = new FatResizer().ResizeWithOutcome(device, Partition(device, FileSystemType.ExFat), CancellationToken.None);
            Assert.False(outcome.Resized);
            Assert.Equal(FileSystemResizeSkipReason.FatVolumeDirty, outcome.SkipReason);
        }

        Assert.Equal(hash, Hash(image));
    }

    [Fact]
    public void Exfat_with_a_broken_entry_checksum_is_left_alone()
    {
        using var dir = new TempDir();
        var image = dir.File("exfat.img");
        using (var device = HandleBlockDevice.OpenFile(image, 32 * MiB))
        {
            ExFatFormatter.Format(device, 0, ExFatFormatter.TryPlan(32 * MiB, 512, (int)MiB)!, string.Empty, 1);
        }

        long rootOffsetInHeap;
        using (var volume = new ExFatTestVolume(image))
        {
            FillExFat(volume);
            rootOffsetInHeap = (volume.RootCluster - 2) * (long)volume.ClusterSize;
        }

        // Read only once the volume is closed: Windows does not share the open file.
        var rootPosition = (BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(image).AsSpan(88)) * 512L) + rootOffsetInHeap;

        // Entries 0 and 1 are the bitmap and the up-case table; the first file set follows.
        using (var stream = File.OpenWrite(image))
        {
            stream.Position = rootPosition + (2 * 32) + 2;
            stream.Write([0xEE, 0xEE]);
            stream.SetLength(500 * MiB);
        }

        var hash = Hash(image);
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            Assert.Equal(FatGrowSkipReason.InconsistentMetadata, ExFatGrow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None).SkipReason);
        }

        Assert.Equal(hash, Hash(image));
    }

    [Fact]
    public async Task Writing_an_image_grows_the_last_exfat_partition()
    {
        using var dir = new TempDir();
        var volumePath = dir.File("exfat.img");
        using (var device = HandleBlockDevice.OpenFile(volumePath, 64 * MiB))
        {
            ExFatFormatter.Format(device, 0, ExFatFormatter.TryPlan(64 * MiB, 512, (int)MiB)!, "CARD", 7);
        }

        Dictionary<string, byte[]> files;
        using (var volume = new ExFatTestVolume(volumePath))
        {
            files = FillExFat(volume);
        }

        var imagePath = DiskImage(dir, volumePath, 0x07);
        var target = dir.File("card.bin");
        var result = await WriteAsync(imagePath, target, 2048 * MiB);

        Assert.True(result.Fixups!.PartitionExtended);
        Assert.True(result.Fixups.FileSystemResized, result.Fixups.FileSystemResizeError ?? result.Fixups.FileSystemSkipReason.ToString());
        Assert.Equal(FileSystemType.ExFat, result.Fixups.Partition!.FileSystem);
        Assert.Equal(2047 * MiB, result.Fixups.FileSystemSize);

        using (var volume = new ExFatTestVolume(target, MiB))
        {
            Assert.Equal(2047 * MiB / 512, volume.TotalSectors);
            AssertSameFiles(files, volume.ReadTree());
        }

        if (FsckExFat != null)
        {
            var fsck = E2fsprogs.Run(FsckExFat, $"-n \"{ExtractPartition(dir, target)}\"", allowFailure: true);
            Assert.True(fsck.ExitCode == 0, fsck.Output);
        }
    }

    // ---- helpers ----

    private void GrowExFatAndCheck(TempDir dir, string image, string name, int finalMiB)
    {
        Dictionary<string, byte[]> files;
        long before;
        using (var volume = new ExFatTestVolume(image))
        {
            files = FillExFat(volume);
            before = volume.TotalSectors;
            AssertSameFiles(files, volume.ReadTree());
        }

        CheckExFat(image, "before");
        Resize(image, finalMiB);

        FatGrowResult result;
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            result = ExFatGrow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
        }

        output.WriteLine($"{name}: {result}");
        Assert.True(result.Grown, result.SkipReason.ToString());
        Assert.False(result.Limited);
        Assert.Equal(before, result.OldSectors);
        using (var volume = new ExFatTestVolume(image))
        {
            Assert.Equal(result.NewSectors, volume.TotalSectors);
            Assert.Equal(finalMiB * MiB / volume.BytesPerSector, volume.TotalSectors);
            Assert.Equal((volume.ClusterCount + 7) / 8, volume.BitmapBytes);
            AssertSameFiles(files, volume.ReadTree());
        }

        CheckExFat(image, "after");

        // A second run has nothing left to do.
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            var again = ExFatGrow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
            Assert.Equal(FatGrowSkipReason.AlreadyFillsPartition, again.SkipReason);
        }

        _ = dir;
    }

    private void CheckExFat(string image, string stage)
    {
        if (FsckExFat == null)
        {
            return;
        }

        var fsck = E2fsprogs.Run(FsckExFat, $"-n \"{image}\"", allowFailure: true);
        output.WriteLine($"fsck.exfat {stage}: {fsck.Output}");
        Assert.True(fsck.ExitCode == 0, $"fsck.exfat reported problems {stage} growing:\n{fsck.Output}");
    }

    /// <summary>Contiguous and fragmented files, in the root and in directories of both kinds.</summary>
    private static Dictionary<string, byte[]> FillExFat(ExFatTestVolume volume)
    {
        var random = new Random(7);
        var files = new Dictionary<string, byte[]>();
        void Add(uint directory, string prefix, string name, int size, bool fragmented)
        {
            var data = new byte[size];
            random.NextBytes(data);
            volume.AddFile(directory, name, data, fragmented);
            files[prefix + name] = data;
        }

        var cluster = volume.ClusterSize;
        Add(volume.RootCluster, string.Empty, "first.bin", (cluster * 5) + 17, fragmented: false);
        Add(volume.RootCluster, string.Empty, "fragmented with a long name.dat", (cluster * 9) + 1, fragmented: true);
        Add(volume.RootCluster, string.Empty, "empty.txt", 0, fragmented: false);
        var plain = volume.AddDirectory(volume.RootCluster, "contiguous dir", contiguous: true);
        Add(plain, "contiguous dir/", "a.bin", cluster * 3, fragmented: true);
        Add(plain, "contiguous dir/", "b with quite a long file name, more than thirty characters.bin", cluster + 100, fragmented: false);
        var chained = volume.AddDirectory(plain, "chained", contiguous: false);
        Add(chained, "contiguous dir/chained/", "deep.bin", (cluster * 20) + 5, fragmented: false);
        Add(chained, "contiguous dir/chained/", "one byte", 1, fragmented: true);
        Add(volume.RootCluster, string.Empty, "last.bin", cluster * 40, fragmented: true);
        Add(volume.RootCluster, string.Empty, "big contiguous.bin", cluster * 64, fragmented: false);
        return files;
    }

    private static void AssertSameFiles(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, data) in expected)
        {
            Assert.True(data.AsSpan().SequenceEqual(actual[path]), $"'{path}' changed");
        }
    }

    /// <summary>Formats a FAT32 volume and fills it; some files are deleted in between so that later ones are fragmented.</summary>
    private static Dictionary<string, byte[]> MakeFat32(TempDir dir, string image, string options, int sizeMiB)
    {
        Resize(image, sizeMiB);
        E2fsprogs.Run(MkfsFat!, $"{options} \"{image}\"");

        var random = new Random(11);
        var files = new Dictionary<string, byte[]>();
        var source = Path.Combine(dir.Path, "source-" + Path.GetFileNameWithoutExtension(image));
        void Copy(string relative, int size)
        {
            var data = new byte[size];
            random.NextBytes(data);
            var local = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllBytes(local, data);
            files[relative] = data;
        }

        for (var i = 0; i < 12; i++)
        {
            Copy($"hole{i}.bin", 30_000 + (i * 7919));
            Copy($"keep{i}.bin", 50_000 + (i * 104_729));
        }

        Copy("empty.txt", 0);
        Mtools($"-i \"{image}\" {string.Join(' ', files.Keys.Select(f => $"\"{Path.Combine(source, f)}\""))} ::/");
        foreach (var hole in files.Keys.Where(f => f.StartsWith("hole", StringComparison.Ordinal)).ToList())
        {
            E2fsprogs.Run(E2fsprogs.Find("mdel")!, $"-i \"{image}\" \"::/{hole}\"");
            files.Remove(hole);
            File.Delete(Path.Combine(source, hole));
        }

        var rootFiles = files.Count;
        for (var i = 0; i < 18; i++)
        {
            Copy(Path.Combine("data", $"dir{i % 3}", $"sub directory {i % 2}", $"A file with a long name number {i}.bin"), random.Next(1, 600_000));
        }

        Copy(Path.Combine("data", "top.bin"), 1_234_567);
        Mtools($"-s -i \"{image}\" \"{Path.Combine(source, "data")}\" ::/");
        Assert.True(files.Count > rootFiles);
        return files;
    }

    private static void AssertFat32Files(TempDir dir, string image, Dictionary<string, byte[]> files)
    {
        var restored = Path.Combine(dir.Path, "restored-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restored);
        Mtools($"-s -n -i \"{image}\" ::/data \"{restored}\"");
        foreach (var (relative, data) in files)
        {
            var local = Path.Combine(restored, relative);
            if (!relative.StartsWith("data", StringComparison.Ordinal))
            {
                Mtools($"-n -i \"{image}\" \"::/{relative}\" \"{local}\"");
            }

            Assert.True(File.Exists(local), $"'{relative}' is missing");
            Assert.True(data.AsSpan().SequenceEqual(File.ReadAllBytes(local)), $"'{relative}' changed");
        }
    }

    private static void Mtools(string arguments) => E2fsprogs.Run(Mcopy!, arguments);

    private static (long Sectors, int BytesPerSector) Fat32Sectors(string image)
    {
        var boot = new byte[512];
        using var stream = File.OpenRead(image);
        stream.ReadExactly(boot);
        return (BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32)), BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)));
    }

    private static void Resize(string path, long sizeMiB)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write);
        stream.SetLength(sizeMiB * MiB);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static PartitionInfo Partition(HandleBlockDevice device, FileSystemType fileSystem)
        => new(1, 0, device.Length, "test") { FileSystem = fileSystem };

    /// <summary>An MBR disk image whose only partition starts at 1 MiB and holds the volume.</summary>
    private static string DiskImage(TempDir dir, string volume, byte type)
    {
        var path = dir.File("disk.img");
        using var image = File.Create(path);
        var mbr = new byte[512];
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        TestImages.WriteMbrEntry(mbr, 0, type, 2048, (uint)(new FileInfo(volume).Length / 512));
        image.Write(mbr);
        image.Position = MiB;
        using var source = File.OpenRead(volume);
        source.CopyTo(image);
        return path;
    }

    private static async Task<FlashTool.Core.Operations.WriteImageResult> WriteAsync(string imagePath, string target, long deviceSize)
    {
        using var container = FlashTool.Core.Containers.ContainerRegistry.Default.Open(new FlashTool.Core.Sources.FileStreamSource(imagePath));
        return await FlashTool.Core.Operations.WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(target, deviceSize),
            new FlashTool.Core.Operations.WriteImageOptions { ExpandLastPartition = true, FileSystemResizers = [new FlashTool.Core.FileSystems.Ext4.Ext4Resizer(), new FatResizer()] },
            null,
            CancellationToken.None);
    }

    private static string ExtractPartition(TempDir dir, string disk)
    {
        var path = dir.File("partition-" + Guid.NewGuid().ToString("N") + ".img");
        using var source = File.OpenRead(disk);
        using var partition = File.Create(path);
        source.Position = MiB;
        source.CopyTo(partition);
        return path;
    }
}
