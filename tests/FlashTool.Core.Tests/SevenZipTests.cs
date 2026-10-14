using FlashTool.Core.Containers;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Tests;

/// <summary>7z goes through the native 7z.dll, so these tests only run on Windows.</summary>
public class SevenZipTests
{
    private static bool Available()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var arch = Environment.Is64BitProcess ? "x64" : "x86";
        return SevenZipContainerHandler.Initialize(Path.Combine(AppContext.BaseDirectory, arch, "7z.dll"));
    }

    [Fact]
    public async Task SevenZip_round_trip()
    {
        if (!Available())
        {
            return;
        }

        using var dir = new TempDir();
        var image = TestImages.CreateGptImage(totalSectors: 40000);
        File.WriteAllBytes(dir.File("src.bin"), image);

        var read = await ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(dir.File("src.bin"), writable: false),
            dir.File("backup.img.7z"),
            ContainerRegistry.Default,
            new ReadImageOptions { Verify = true, Compression = new CompressionSettings(CompressionPreset.Fast) },
            null,
            CancellationToken.None);
        Assert.Equal(ContainerFormat.SevenZip, read.Format);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("backup.img.7z")));
        Assert.Equal("backup.img", container.Entries.Single().Name);

        await WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length),
            new WriteImageOptions(),
            null,
            CancellationToken.None);
        Assert.Equal(image, File.ReadAllBytes(dir.File("target.bin")));
    }
}
