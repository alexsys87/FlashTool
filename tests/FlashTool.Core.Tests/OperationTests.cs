using FlashTool.Core.Containers;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Partitioning;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Tests;

public class OperationTests
{
    public static TheoryData<string> Formats => new() { "backup.img", "backup.img.zip", "backup.img.gz", "backup.img.xz" };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Read_then_write_round_trip_through_container(string fileName)
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 30000, trailingSectors: 3000);
        File.WriteAllBytes(dir.File("source.bin"), image);
        var destination = dir.File(fileName);

        // 1. Device -> image file (compressed on the fly), verified.
        var readResult = await ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(dir.File("source.bin"), writable: false),
            destination,
            ContainerRegistry.Default,
            new ReadImageOptions { Verify = true, ChunkSize = 64 * 1024 },
            null,
            CancellationToken.None);

        Assert.Equal(image.Length, readResult.BytesRead);
        Assert.True(readResult.Verified);

        // 2. Image file -> bigger device (decompressed on the fly), verified, partition grown.
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(destination));
        Assert.Single(container.Entries);
        Assert.Equal("backup.img", container.Entries[0].Name);

        var deviceSize = (long)image.Length + (10 * 1024 * 1024);
        var writeResult = await WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(dir.File("target.bin"), deviceSize),
            new WriteImageOptions { Verify = true, ExpandLastPartition = true, ChunkSize = 64 * 1024 },
            null,
            CancellationToken.None);

        Assert.Equal(image.Length, writeResult.BytesWritten);
        Assert.True(writeResult.Fixups!.PartitionExtended);

        var written = File.ReadAllBytes(dir.File("target.bin"));
        Assert.Equal(image.AsSpan(512).ToArray(), written.AsSpan(512, image.Length - 512).ToArray());
        Assert.Equal(image.AsSpan(0, 446).ToArray(), written.AsSpan(0, 446).ToArray());
        var mbr = MbrPartitionTable.TryParse(written.AsSpan(0, 512))!;
        Assert.Equal((uint)((deviceSize / 512) - 4096), mbr.Entries[1].SectorCount);
    }

    [Fact]
    public async Task Only_allocated_partitions_stops_at_last_partition_end()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 8192, trailingSectors: 20000);
        File.WriteAllBytes(dir.File("source.bin"), image);

        var result = await ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(dir.File("source.bin"), writable: false),
            dir.File("out.img"),
            ContainerRegistry.Default,
            new ReadImageOptions { OnlyAllocatedPartitions = true, ChunkSize = 64 * 1024 },
            null,
            CancellationToken.None);

        Assert.Equal((4096L + 8192) * 512, result.BytesRead);
        Assert.Equal(result.BytesRead, new FileInfo(dir.File("out.img")).Length);
    }

    [Fact]
    public async Task Verification_detects_corrupted_device()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage();
        File.WriteAllBytes(dir.File("image.img"), image);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("image.img")));

        var ex = await Assert.ThrowsAsync<VerificationFailedException>(() => WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => new CorruptingDevice(HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length * 2L), corruptOffset: 300_000),
            new WriteImageOptions { ChunkSize = 64 * 1024 },
            null,
            CancellationToken.None));

        Assert.Equal(4 * 64 * 1024, ex.Offset);
    }

    [Fact]
    public async Task Non_image_content_is_rejected_before_writing()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("notes.txt"), string.Concat(Enumerable.Repeat("not an image\n", 100_000)));
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("notes.txt")));
        var opened = false;

        var ex = await Assert.ThrowsAsync<FlashToolException>(() => WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () =>
            {
                opened = true;
                return HandleBlockDevice.OpenFile(dir.File("target.bin"), 4 * 1024 * 1024);
            },
            new WriteImageOptions(),
            null,
            CancellationToken.None));

        Assert.Equal(FlashErrorCode.NotADiskImage, ex.Code);
        Assert.False(opened);
    }

    [Fact]
    public async Task Image_larger_than_device_is_rejected()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage();
        File.WriteAllBytes(dir.File("image.img"), image);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("image.img")));

        var ex = await Assert.ThrowsAsync<FlashToolException>(() => WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length / 2),
            new WriteImageOptions(),
            null,
            CancellationToken.None));

        Assert.Equal(FlashErrorCode.ImageTooLarge, ex.Code);
    }

    [Fact]
    public async Task Cancellation_stops_the_operation()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 60000);
        File.WriteAllBytes(dir.File("image.img"), image);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("image.img")));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == OperationPhase.Writing && p.BytesProcessed > 1024 * 1024)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => new SlowDevice(HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length)),
            new WriteImageOptions { ChunkSize = 64 * 1024 },
            progress,
            cts.Token));
    }

    [Fact]
    public void Zip_with_several_files_lists_all_entries()
    {
        using var dir = new TempDir();
        var zipPath = dir.File("bundle.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("readme.txt").Open())
            {
                s.Write("hello"u8);
            }

            using (var s = zip.CreateEntry("images/a.img").Open())
            {
                s.Write(TestImages.CreateMbrImage(seed: 10));
            }

            using (var s = zip.CreateEntry("images/b.img").Open())
            {
                s.Write(TestImages.CreateGptImage(seed: 11));
            }
        }

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(zipPath));
        Assert.Equal(ContainerFormat.Zip, container.Format);
        Assert.Equal(["readme.txt", "images/a.img", "images/b.img"], container.Entries.Select(e => e.Name));

        var kinds = container.Entries.Select(e =>
        {
            using var r = container.OpenEntry(e);
            var head = new byte[ImageInspector.HeadSize];
            var n = r.Data.ReadUpTo(head);
            return ImageInspector.Inspect(head.AsSpan(0, n)).Kind;
        }).ToList();
        Assert.Equal([ImageKind.Unknown, ImageKind.MbrDisk, ImageKind.GptDisk], kinds);
    }

    [Fact]
    public async Task Xz_index_gives_exact_uncompressed_size()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000);
        File.WriteAllBytes(dir.File("src.bin"), image);
        await ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(dir.File("src.bin"), writable: false),
            dir.File("img.xz"),
            ContainerRegistry.Default,
            new ReadImageOptions { Verify = false, Compression = new CompressionSettings(CompressionPreset.Fastest, 4) },
            null,
            CancellationToken.None);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("img.xz")));
        Assert.Equal(ContainerFormat.Xz, container.Format);
        Assert.Equal(image.Length, container.Entries[0].Length);
        Assert.Equal("img", Path.GetFileNameWithoutExtension(container.Entries[0].Name));
    }

    private sealed class SyncProgress(Action<OperationProgress> handler) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => handler(value);
    }

    /// <summary>Flips one byte on read to emulate a bad card.</summary>
    private sealed class CorruptingDevice(IBlockDevice inner, long corruptOffset) : IBlockDevice
    {
        public long Length => inner.Length;

        public int SectorSize => inner.SectorSize;

        public bool CanWrite => inner.CanWrite;

        public void Read(long offset, Span<byte> buffer)
        {
            inner.Read(offset, buffer);
            if (corruptOffset >= offset && corruptOffset < offset + buffer.Length)
            {
                buffer[(int)(corruptOffset - offset)] ^= 0xFF;
            }
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer) => inner.Write(offset, buffer);

        public void Flush() => inner.Flush();

        public void Dispose() => inner.Dispose();
    }

    private sealed class SlowDevice(IBlockDevice inner) : IBlockDevice
    {
        public long Length => inner.Length;

        public int SectorSize => inner.SectorSize;

        public bool CanWrite => inner.CanWrite;

        public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            Thread.Sleep(5);
            inner.Write(offset, buffer);
        }

        public void Flush() => inner.Flush();

        public void Dispose() => inner.Dispose();
    }
}
