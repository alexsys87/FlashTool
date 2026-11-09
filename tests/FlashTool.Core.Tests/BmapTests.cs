using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FlashTool.Core.Bmap;
using FlashTool.Core.Containers;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;
using Xunit.Abstractions;

namespace FlashTool.Core.Tests;

/// <summary>.bmap files: reading, writing with holes, verifying by ranges and creating them, also against bmaptool.</summary>
public class BmapTests(ITestOutputHelper output)
{
    private const long MiB = 1024 * 1024;
    private const byte OldData = 0xEE;

    private static readonly string? Bmaptool = E2fsprogs.Find("bmaptool");

    // ---- Reading ----

    [Fact]
    public void Built_map_reads_back_with_its_ranges_and_checksums()
    {
        var image = SyntheticImage();
        var bmap = BuildBmap(image);

        Assert.Equal("2.0", bmap.Version);
        Assert.Equal(image.Length, bmap.ImageSize);
        Assert.Equal(BmapChecksumType.Sha256, bmap.ChecksumType);
        Assert.True(bmap.MappedBytes < image.Length / 4);
        foreach (var (range, (start, end)) in bmap.Ranges.Zip(bmap.MappedByteRanges()))
        {
            Assert.Equal(SHA256.HashData(image.AsSpan((int)start, (int)(end - start))), range.Checksum);
            Assert.True(image.AsSpan((int)start, (int)(end - start)).IndexOfAnyExcept((byte)0) >= 0);
        }
    }

    [Fact]
    public void Edited_map_fails_its_own_checksum()
    {
        var text = Encoding.ASCII.GetString(BuilderBytes(SyntheticImage()));
        var index = text.IndexOf("chksum=\"", StringComparison.Ordinal) + 8;
        var edited = text[..index] + (text[index] == 'a' ? 'b' : 'a') + text[(index + 1)..];

        Assert.False(BmapFile.TryParse(Encoding.ASCII.GetBytes(edited), out _, out var problem));
        Assert.Equal(BmapProblem.FileChecksumMismatch, problem);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><bmap version=\"3.0\"><ImageSize>4096</ImageSize><BlockSize>4096</BlockSize><BlocksCount>1</BlocksCount><MappedBlocksCount>0</MappedBlocksCount><BlockMap/></bmap>", BmapProblem.UnsupportedVersion)]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE bmap [<!ENTITY x \"y\">]><bmap version=\"1.2\"/>", BmapProblem.Malformed)]
    [InlineData("<bmap version=\"1.2\"><ImageSize>8192</ImageSize><BlockSize>4096</BlockSize><BlocksCount>3</BlocksCount><MappedBlocksCount>0</MappedBlocksCount><BlockMap/></bmap>", BmapProblem.Inconsistent)]
    [InlineData("<bmap version=\"1.2\"><ImageSize>16384</ImageSize><BlockSize>4096</BlockSize><BlocksCount>4</BlocksCount><MappedBlocksCount>3</MappedBlocksCount><BlockMap><Range>0-1</Range><Range>1-1</Range></BlockMap></bmap>", BmapProblem.Inconsistent)]
    [InlineData("<bmap version=\"1.2\"><ImageSize>16384</ImageSize><BlockSize>4096</BlockSize><BlocksCount>4</BlocksCount><MappedBlocksCount>5</MappedBlocksCount><BlockMap><Range>0-1</Range></BlockMap></bmap>", BmapProblem.Inconsistent)]
    [InlineData("<bmap version=\"1.2\"><ImageSize>16384</ImageSize><BlockSize>4096</BlockSize><BlocksCount>4</BlocksCount><MappedBlocksCount>1</MappedBlocksCount><BlockMap><Range>4</Range></BlockMap></bmap>", BmapProblem.Inconsistent)]
    [InlineData("<bmap version=\"2.0\"><ImageSize>4096</ImageSize><BlockSize>4096</BlockSize><BlocksCount>1</BlocksCount><MappedBlocksCount>0</MappedBlocksCount><ChecksumType>md5</ChecksumType><BmapFileChecksum>0</BmapFileChecksum><BlockMap/></bmap>", BmapProblem.UnsupportedChecksum)]
    [InlineData("<bmap version=\"1.2\"><ImageSize>x</ImageSize></bmap>", BmapProblem.Malformed)]
    public void Unusable_maps_are_refused(string xml, BmapProblem expected)
    {
        Assert.False(BmapFile.TryParse(Encoding.UTF8.GetBytes(xml), out _, out var problem));
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void Old_formats_are_read()
    {
        var image = SyntheticImage();
        var ranges = BuildBmap(image).MappedByteRanges().ToList();

        // 1.2: no checksums at all.
        var v12 = Legacy(image, ranges, "1.2", withSha1: false);
        Assert.True(BmapFile.TryParse(v12, out var old, out _));
        Assert.Equal(BmapChecksumType.None, old.ChecksumType);
        Assert.All(old.Ranges, r => Assert.Null(r.Checksum));

        // 1.3: SHA-1 for the ranges and the file.
        var v13 = Legacy(image, ranges, "1.3", withSha1: true);
        Assert.True(BmapFile.TryParse(v13, out var sha1, out var problem), problem.ToString());
        Assert.Equal(BmapChecksumType.Sha1, sha1.ChecksumType);
        Assert.Equal(ranges.Count, sha1.Ranges.Count);
    }

    // ---- Writing ----

    [Fact]
    public async Task Writes_mapped_data_and_guards_but_leaves_holes_inside_partitions()
    {
        using var dir = new TempDir();
        var image = SyntheticImage();
        var bmap = BuildBmap(image);
        var device = OldDevice(dir, image.Length + (8 * MiB));

        var result = await WriteAsync(dir, image, bmap, device);

        Assert.True(result.Verified);
        Assert.Equal(image.Length, result.BytesWritten);
        output.WriteLine($"image {image.Length:N0}, mapped {bmap.MappedBytes:N0}, written {result.DeviceBytesWritten:N0}");
        Assert.True(result.DeviceBytesWritten < image.Length / 2);

        var disk = File.ReadAllBytes(device);
        foreach (var (start, end) in bmap.MappedByteRanges())
        {
            Assert.Equal(image.AsSpan((int)start, (int)(end - start)).ToArray(), disk.AsSpan((int)start, (int)(end - start)).ToArray());
        }

        // Outside partitions everything comes from the image: boot loader area, gap, tail.
        AssertAll(disk, 64 * 1024, 2 * MiB, 0);
        AssertAll(disk, 10 * MiB, 12 * MiB, 0);
        AssertAll(disk, 22 * MiB, 24 * MiB, 0);

        // The empty second partition: its first and last mebibyte are cleared, the rest is left alone.
        AssertAll(disk, 12 * MiB, 13 * MiB, 0);
        AssertAll(disk, 21 * MiB, 22 * MiB, 0);
        AssertAll(disk, 13 * MiB, 21 * MiB, OldData);

        // A hole in the middle of the first partition is not written either.
        AssertAll(disk, (5 * MiB) + (64 * 1024), 9 * MiB, OldData);

        // Past the image nothing is touched.
        AssertAll(disk, 24 * MiB, disk.Length, OldData);
    }

    [Fact]
    public async Task Size_comes_from_the_map_for_a_stream_without_one()
    {
        using var dir = new TempDir();
        var image = SyntheticImage();
        var bmap = BuildBmap(image);
        var device = OldDevice(dir, image.Length);

        var gz = dir.File("image.img.gz");
        await using (var file = File.Create(gz))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            gzip.Write(image);
        }

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(gz));
        Assert.Null(container.Entries[0].Length);
        var result = await WriteImageOperation.RunAsync(container, container.Entries[0], () => HandleBlockDevice.OpenFile(device), Options(bmap), null, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(image.Length, result.BytesWritten);
    }

    [Theory]
    [InlineData("changed data in a mapped range", 4 * MiB + 100)]
    [InlineData("data in a hole", 15 * MiB)]
    public async Task Map_of_another_image_stops_before_the_partition_table(string what, long offset)
    {
        using var dir = new TempDir();
        var image = SyntheticImage();
        var bmap = BuildBmap(image);
        image[offset] ^= 0x5A;
        var device = OldDevice(dir, image.Length);

        var error = await Assert.ThrowsAsync<FlashToolException>(() => WriteAsync(dir, image, bmap, device));
        output.WriteLine($"{what}: {error.Message}");
        Assert.Equal(FlashErrorCode.BmapDataMismatch, error.Code);

        // The first chunk was blanked and never written for real.
        AssertAll(File.ReadAllBytes(device), 0, 256 * 1024, 0);
    }

    [Fact]
    public async Task Map_for_another_size_is_refused_before_the_device_is_touched()
    {
        using var dir = new TempDir();
        var image = SyntheticImage();
        var bmap = BuildBmap(image[..^4096]);
        var device = OldDevice(dir, image.Length);

        var error = await Assert.ThrowsAsync<FlashToolException>(() => WriteAsync(dir, image, bmap, device));
        Assert.Equal(FlashErrorCode.BmapSizeMismatch, error.Code);
        AssertAll(File.ReadAllBytes(device), 0, MiB, OldData);
    }

    [Fact]
    public async Task Sha1_map_of_format_1_3_is_used_for_writing()
    {
        using var dir = new TempDir();
        var image = SyntheticImage();
        Assert.True(BmapFile.TryParse(Legacy(image, BuildBmap(image).MappedByteRanges().ToList(), "1.3", withSha1: true), out var bmap, out _));
        var device = OldDevice(dir, image.Length);

        var result = await WriteAsync(dir, image, bmap, device);
        Assert.True(result.Verified);

        image[4 * MiB] ^= 1;
        var error = await Assert.ThrowsAsync<FlashToolException>(() => WriteAsync(dir, image, bmap, device));
        Assert.Equal(FlashErrorCode.BmapDataMismatch, error.Code);
    }

    // ---- Verifying ----

    [Fact]
    public async Task Verify_with_the_map_compares_only_mapped_ranges()
    {
        using var dir = new TempDir();
        var image = SyntheticImage();
        var bmap = BuildBmap(image);
        var device = OldDevice(dir, image.Length);
        await WriteAsync(dir, image, bmap, device);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("image.img")));
        var full = await VerifyImageOperation.RunAsync(container, container.Entries[0], () => HandleBlockDevice.OpenFile(device, writable: false), new VerifyImageOptions(), null, CancellationToken.None);
        Assert.False(full.Identical);

        var mapped = await VerifyImageOperation.RunAsync(container, container.Entries[0], () => HandleBlockDevice.OpenFile(device, writable: false), new VerifyImageOptions { Bmap = bmap }, null, CancellationToken.None);
        Assert.True(mapped.Identical);
        Assert.True(mapped.UsedBmap);
        Assert.Equal(bmap.MappedBytes, mapped.BytesCompared);

        // A change inside a mapped range is still found.
        using (var stream = File.OpenWrite(device))
        {
            stream.Position = 4 * MiB;
            stream.WriteByte(0x42);
        }

        var changed = await VerifyImageOperation.RunAsync(container, container.Entries[0], () => HandleBlockDevice.OpenFile(device, writable: false), new VerifyImageOptions { Bmap = bmap }, null, CancellationToken.None);
        Assert.Equal(1, changed.DifferentBytes);
        Assert.Equal(4 * MiB, changed.Ranges.Single().Offset);
    }

    // ---- Finding and creating ----

    [Theory]
    [InlineData("core-image.wic.zst", "core-image.wic.bmap")]
    [InlineData("core-image.wic.zst", "core-image.wic.zst.bmap")]
    [InlineData("core-image.wic", "core-image.wic.bmap")]
    public void Map_is_found_next_to_the_image(string imageName, string bmapName)
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File(imageName), [1]);
        File.WriteAllBytes(dir.File(bmapName), BuilderBytes(SyntheticImage()));

        var found = BmapDiscovery.FindForFile(dir.File(imageName));
        Assert.Equal(bmapName, found.Origin);
        Assert.NotNull(found.Bmap);
    }

    [Fact]
    public void Damaged_map_is_reported_not_used()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("a.wic.gz"), [1]);
        var bytes = BuilderBytes(SyntheticImage());
        bytes[^20] ^= 1;
        File.WriteAllBytes(dir.File("a.wic.bmap"), bytes);

        var found = BmapDiscovery.FindForFile(dir.File("a.wic.gz"));
        Assert.True(found.Found);
        Assert.Null(found.Bmap);
        Assert.NotEqual(BmapProblem.None, found.Problem);
        Assert.False(BmapDiscovery.FindForFile(dir.File("missing.img")).Found);
        Assert.Equal(Path.Combine("x", "backup.img.bmap"), BmapDiscovery.PathFor(Path.Combine("x", "backup.img.xz")));
    }

    [Fact]
    public async Task Created_image_gets_a_map_that_writes_it_back()
    {
        using var dir = new TempDir();
        var disk = TestImages.CreateMbrImage(secondPartitionSectors: 20000, trailingSectors: 2000);
        File.WriteAllBytes(dir.File("source.bin"), disk);
        File.WriteAllText(dir.File("backup.img.bmap"), "stale");

        var created = await ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(dir.File("source.bin"), writable: false),
            dir.File("backup.img.xz"),
            ContainerRegistry.Default,
            new ReadImageOptions { CreateBmapFile = true, ChunkSize = 64 * 1024 },
            null,
            CancellationToken.None);

        Assert.Equal(dir.File("backup.img.bmap"), created.BmapFilePath);
        Assert.True(BmapFile.TryParse(File.ReadAllBytes(created.BmapFilePath!), out var bmap, out var problem), problem.ToString());
        Assert.Equal(disk.Length, bmap.ImageSize);

        // Nothing was skipped, so everything is mapped; the checksums still describe the data.
        Assert.Equal(disk.Length, bmap.MappedBytes);
        var found = BmapDiscovery.FindForFile(dir.File("backup.img.xz"));
        Assert.Equal("backup.img.bmap", found.Origin);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("backup.img.xz")));
        var device = OldDevice(dir, disk.Length);
        var written = await WriteImageOperation.RunAsync(container, container.Entries[0], () => HandleBlockDevice.OpenFile(device), Options(found.Bmap!), null, CancellationToken.None);
        Assert.True(written.Verified);
        Assert.Equal(disk, File.ReadAllBytes(device));
    }

    // ---- bmaptool, both ways (Linux, where bmap-tools and e2fsprogs are installed) ----

    [Fact]
    public async Task Map_made_by_bmaptool_writes_a_working_file_system()
    {
        if (Bmaptool == null || Ext4TestDisk.Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var files = Ext4TestDisk.MakeFileSystem(dir, "-t ext4", 256, fillFreeSpace: false);
        var disk = Ext4TestDisk.MakeDisk(dir, sparse: true);
        File.Move(disk, dir.File("core.wic"));
        E2fsprogs.Run(Bmaptool, $"create -o \"{dir.File("core.wic.bmap")}\" \"{dir.File("core.wic")}\"");
        var image = File.ReadAllBytes(dir.File("core.wic"));
        await using (var file = File.Create(dir.File("core.wic.gz")))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            gzip.Write(image);
        }

        var found = BmapDiscovery.FindForFile(dir.File("core.wic.gz"));
        Assert.Equal("core.wic.bmap", found.Origin);
        var bmap = found.Bmap!;
        output.WriteLine($"bmaptool mapped {bmap.MappedBytes:N0} of {bmap.ImageSize:N0}");
        Assert.True(bmap.MappedBytes < bmap.ImageSize / 4);

        // Ours: onto a device full of old data.
        var ours = OldDevice(dir, image.Length + (4 * MiB));
        using (var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("core.wic.gz"))))
        {
            var result = await WriteImageOperation.RunAsync(container, container.Entries[0], () => HandleBlockDevice.OpenFile(ours), Options(bmap), null, CancellationToken.None);
            Assert.True(result.Verified);
            output.WriteLine($"written to the device: {result.DeviceBytesWritten:N0}");
        }

        // bmaptool's own copy of the same image.
        var theirs = dir.File("bmaptool.bin");
        E2fsprogs.Run(Bmaptool, $"copy --bmap \"{dir.File("core.wic.bmap")}\" \"{dir.File("core.wic")}\" \"{theirs}\"");

        var oursBytes = File.ReadAllBytes(ours);
        var theirsBytes = File.ReadAllBytes(theirs);
        foreach (var (start, end) in bmap.MappedByteRanges())
        {
            var expected = image.AsSpan((int)start, (int)(end - start));
            Assert.True(expected.SequenceEqual(oursBytes.AsSpan((int)start, (int)(end - start))));
            Assert.True(expected.SequenceEqual(theirsBytes.AsSpan((int)start, (int)(end - start))));
        }

        AssertFileSystemIntact(dir, oursBytes, 256, files);

        // Verifying by the map accepts what bmaptool wrote.
        using var source = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("core.wic")));
        var verify = await VerifyImageOperation.RunAsync(source, source.Entries[0], () => HandleBlockDevice.OpenFile(theirs, writable: false), new VerifyImageOptions { Bmap = bmap }, null, CancellationToken.None);
        Assert.True(verify.Identical);
    }

    [Fact]
    public async Task Map_made_by_us_is_accepted_by_bmaptool()
    {
        if (Bmaptool == null || Ext4TestDisk.Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var files = Ext4TestDisk.MakeFileSystem(dir, "-t ext4", 256);
        var disk = Ext4TestDisk.MakeDisk(dir);

        var created = await ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(disk, writable: false),
            dir.File("backup.img.gz"),
            ContainerRegistry.Default,
            new ReadImageOptions { SkipFreeBlocks = true, CreateBmapFile = true, Compression = new CompressionSettings(CompressionPreset.Fastest) },
            null,
            CancellationToken.None);

        Assert.True(BmapFile.TryParse(File.ReadAllBytes(created.BmapFilePath!), out var bmap, out _));
        output.WriteLine($"mapped {bmap.MappedBytes:N0} of {bmap.ImageSize:N0}, skipped {created.SkippedBytes:N0}");
        Assert.True(bmap.MappedBytes < bmap.ImageSize / 4);

        // bmaptool checks the file checksum and every range checksum while copying.
        var copy = dir.File("copy.bin");
        var run = E2fsprogs.Run(Bmaptool, $"copy --bmap \"{created.BmapFilePath}\" \"{dir.File("backup.img.gz")}\" \"{copy}\"");
        output.WriteLine(run.Output);
        AssertFileSystemIntact(dir, File.ReadAllBytes(copy), 256, files);
    }

    // ---- Helpers ----

    /// <summary>
    /// 24 MiB MBR disk: boot code and data in the first 64 KiB, zeros up to partition 1 at 2 MiB (8 MiB, data at
    /// its start, at 4 MiB and at its end), a zero gap, an empty partition 2 at 12 MiB (10 MiB) and a zero tail.
    /// </summary>
    private static byte[] SyntheticImage()
    {
        var image = new byte[24 * MiB];
        var random = new Random(3);
        random.NextBytes(image.AsSpan(0, 64 * 1024));
        image.AsSpan(446, 64).Clear();
        WritePartition(image, 0, 2 * MiB, 8 * MiB);
        WritePartition(image, 1, 12 * MiB, 10 * MiB);
        image[510] = 0x55;
        image[511] = 0xAA;
        random.NextBytes(image.AsSpan((int)(2 * MiB), 128 * 1024));
        random.NextBytes(image.AsSpan((int)(4 * MiB), (int)MiB + (64 * 1024)));
        random.NextBytes(image.AsSpan((int)(10 * MiB) - (64 * 1024), 64 * 1024));
        return image;
    }

    private static void WritePartition(byte[] image, int index, long offset, long length)
    {
        var entry = image.AsSpan(446 + (16 * index), 16);
        entry[4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)(offset / 512));
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)(length / 512));
    }

    /// <summary>The holes are the zero blocks, as bmaptool sees a sparse image.</summary>
    private static byte[] BuilderBytes(byte[] image)
    {
        var holes = new List<(long, long)>();
        for (long offset = 0; offset < image.Length; offset += BmapBuilder.BlockSize)
        {
            var length = (int)Math.Min(BmapBuilder.BlockSize, image.Length - offset);
            if (image.AsSpan((int)offset, length).IndexOfAnyExcept((byte)0) < 0)
            {
                holes.Add((offset, offset + length));
            }
        }

        using var builder = new BmapBuilder(image.Length, holes);
        builder.Append(image);
        return builder.ToBytes();
    }

    private static BmapFile BuildBmap(byte[] image) => BmapFile.Parse(BuilderBytes(image));

    /// <summary>A map in format 1.2 (no checksums) or 1.3 (SHA-1), as older bmaptool wrote them.</summary>
    private static byte[] Legacy(byte[] image, List<(long Start, long End)> ranges, string version, bool withSha1)
    {
        var blocks = (image.Length + 4095) / 4096;
        var mapped = ranges.Sum(r => ((r.End + 4095) / 4096) - (r.Start / 4096));
        var zeros = new string('0', 40);
        var text = new StringBuilder();
        text.Append($"<?xml version=\"1.0\" ?>\n<bmap version=\"{version}\">\n<ImageSize> {image.Length} </ImageSize>\n<BlockSize> 4096 </BlockSize>\n");
        text.Append($"<BlocksCount> {blocks} </BlocksCount>\n<MappedBlocksCount> {mapped} </MappedBlocksCount>\n");
        if (withSha1)
        {
            text.Append($"<BmapFileSHA1> {zeros} </BmapFileSHA1>\n");
        }

        text.Append("<BlockMap>\n");
        foreach (var (start, end) in ranges)
        {
            var sha1 = withSha1 ? $" sha1=\"{Convert.ToHexString(SHA1.HashData(image.AsSpan((int)start, (int)(end - start)))).ToLowerInvariant()}\"" : string.Empty;
            text.Append($"<Range{sha1}> {start / 4096}-{((end + 4095) / 4096) - 1} </Range>\n");
        }

        text.Append("</BlockMap>\n</bmap>\n");
        var bytes = Encoding.ASCII.GetBytes(text.ToString());
        if (withSha1)
        {
            var at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(zeros));
            Encoding.ASCII.GetBytes(Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()).CopyTo(bytes, at);
        }

        return bytes;
    }

    private static string OldDevice(TempDir dir, long length)
    {
        var path = dir.File("device.bin");
        var data = new byte[length];
        data.AsSpan().Fill(OldData);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static WriteImageOptions Options(BmapFile bmap) => new() { Bmap = bmap, Verify = true, ChunkSize = 256 * 1024 };

    private static async Task<WriteImageResult> WriteAsync(TempDir dir, byte[] image, BmapFile bmap, string device)
    {
        File.WriteAllBytes(dir.File("image.img"), image);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("image.img")));
        return await WriteImageOperation.RunAsync(container, container.Entries[0], () => HandleBlockDevice.OpenFile(device), Options(bmap), null, CancellationToken.None);
    }

    private static void AssertAll(byte[] data, long from, long to, byte value)
    {
        var index = data.AsSpan((int)from, (int)(to - from)).IndexOfAnyExcept(value);
        Assert.True(index < 0, $"Byte at {from + index} is not 0x{value:X2}.");
    }

    private static void AssertFileSystemIntact(TempDir dir, byte[] disk, int sizeMiB, Dictionary<string, byte[]> files)
    {
        var fsImage = dir.File("check-fs.img");
        File.WriteAllBytes(fsImage, disk.AsSpan((int)Ext4TestDisk.PartitionOffset, sizeMiB * (int)MiB).ToArray());
        var fsck = E2fsprogs.Run(E2fsprogs.Find("e2fsck")!, $"-fn \"{fsImage}\"", allowFailure: true);
        Assert.True(fsck.ExitCode == 0, $"e2fsck reported problems:\n{fsck.Output}");

        var restored = Path.Combine(dir.Path, "restored-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restored);
        E2fsprogs.Run(E2fsprogs.Find("debugfs")!, $"-R \"rdump / {restored}\" \"{fsImage}\"");
        foreach (var (relative, data) in files)
        {
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(restored, relative)));
        }
    }
}
