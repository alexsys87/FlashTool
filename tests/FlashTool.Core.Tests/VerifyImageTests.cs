using System.Security.Cryptography;
using FlashTool.Core.Containers;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Tests;

public class VerifyImageTests
{
    public static TheoryData<string> Formats => new() { "image.img", "image.img.xz", "image.img.zip", "image.img.gz" };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Identical_device_is_reported_identical(string fileName)
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000, trailingSectors: 7);
        var container = await CreateContainer(dir, image, fileName);
        File.WriteAllBytes(dir.File("device.bin"), [.. image, .. new byte[1024 * 1024]]);

        var result = await Verify(container, dir.File("device.bin"));

        Assert.True(result.Identical);
        Assert.Equal(image.Length, result.BytesCompared);
        Assert.Equal(0, result.DifferenceRangeCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(), result.ImageSha256);
        Assert.Equal(result.ImageSha256, result.DeviceSha256);
        container.Dispose();
    }

    [Fact]
    public async Task Every_difference_is_found_and_located()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000);
        var container = await CreateContainer(dir, image, "image.img.xz");
        var device = (byte[])image.Clone();

        // 1) one byte in the partition table, 2) a run crossing a 64 KiB chunk boundary
        // inside partition 2, 3) a single byte at the very end.
        device[446 + 16 + 12] ^= 0xFF;
        var crossing = (4096 * 512) + (64 * 1024 * 3) - 100;
        for (var i = 0; i < 300; i++)
        {
            device[crossing + i] ^= 0x5A;
        }

        device[^1] ^= 1;
        File.WriteAllBytes(dir.File("device.bin"), device);

        var result = await Verify(container, dir.File("device.bin"), chunk: 64 * 1024);

        Assert.False(result.Identical);
        Assert.Equal(1 + 300 + 1, result.DifferentBytes);
        Assert.Equal(3, result.DifferenceRangeCount);
        Assert.Equal(new DifferenceRange(446 + 16 + 12, 1, DifferenceArea.PartitionTable, null), result.Ranges[0]);
        Assert.Equal(new DifferenceRange(crossing, 300, DifferenceArea.Partition, 2), result.Ranges[1]);
        Assert.Equal(new DifferenceRange(image.Length - 1, 1, DifferenceArea.Partition, 2), result.Ranges[2]);
        Assert.NotEqual(result.ImageSha256, result.DeviceSha256);
        container.Dispose();
    }

    [Fact]
    public async Task Fully_different_device_is_counted_without_storing_every_range()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage();
        var container = await CreateContainer(dir, image, "image.img");
        var device = new byte[image.Length];
        for (var i = 0; i < device.Length; i += 2048)
        {
            device[i] = (byte)~image[i];
        }

        File.WriteAllBytes(dir.File("device.bin"), device);

        var result = await VerifyImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(dir.File("device.bin"), writable: false),
            new VerifyImageOptions { MaxReportedRanges = 10, ChunkSize = 64 * 1024 },
            null,
            CancellationToken.None);

        Assert.Equal(10, result.Ranges.Count);
        Assert.True(result.DifferenceRangeCount > 10);
        container.Dispose();
    }

    [Fact]
    public async Task Image_larger_than_device_is_rejected()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage();
        var container = await CreateContainer(dir, image, "image.img");
        File.WriteAllBytes(dir.File("device.bin"), new byte[image.Length / 2]);

        var ex = await Assert.ThrowsAsync<FlashToolException>(() => Verify(container, dir.File("device.bin")));
        Assert.Equal(FlashErrorCode.ImageTooLarge, ex.Code);
        container.Dispose();
    }

    private static Task<VerifyImageResult> Verify(IImageContainer container, string device, int chunk = 4 * 1024 * 1024)
        => VerifyImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(device, writable: false),
            new VerifyImageOptions { ChunkSize = chunk },
            null,
            CancellationToken.None);

    /// <summary>Writes <paramref name="image"/> in the format implied by the file name and opens it.</summary>
    private static async Task<IImageContainer> CreateContainer(TempDir dir, byte[] image, string fileName)
    {
        File.WriteAllBytes(dir.File("raw.bin"), image);
        await ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(dir.File("raw.bin"), writable: false),
            dir.File(fileName),
            ContainerRegistry.Default,
            new ReadImageOptions { Verify = false },
            null,
            CancellationToken.None);
        return ContainerRegistry.Default.Open(new FileStreamSource(dir.File(fileName)));
    }
}
