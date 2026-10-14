using System.Diagnostics;
using FlashTool.Core.FileSystems;
using FlashTool.Core.FileSystems.Ext4;
using FlashTool.Core.IO;
using Xunit.Abstractions;

namespace FlashTool.Core.Tests;

/// <summary>
/// Grows real file systems made by mke2fs and validates them with e2fsck.
/// Runs where e2fsprogs is installed (Linux); a no-op elsewhere.
/// </summary>
public class Ext4GrowTests(ITestOutputHelper output)
{
    private static readonly string[] ToolFolders = ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];
    private static readonly string? Mke2fs = FindTool("mke2fs");

    public static TheoryData<string, string, int, int> Cases => new()
    {
        // name, mke2fs options, initial MiB, final MiB
        { "ext4 default (64bit, metadata_csum, flex_bg)", "-t ext4", 300, 2048 },
        { "ext4 like Raspberry Pi OS (no 64bit)", "-t ext4 -O ^64bit", 300, 3000 },
        { "ext4 gdt_csum instead of metadata_csum", "-t ext4 -O ^metadata_csum,uninit_bg", 200, 1500 },
        { "ext3", "-t ext3", 200, 1024 },
        { "ext2 without csum (inode tables zeroed)", "-t ext2", 100, 400 },
        { "1k blocks, descriptor table grows", "-t ext4 -b 1024", 64, 1200 },
        { "1k blocks, no 64bit, descriptor table grows", "-t ext4 -b 1024 -O ^64bit", 60, 700 },
        { "partial last group grows only", "-t ext4", 100, 120 },
        { "no resize_inode: limited by descriptor slack", "-t ext4 -b 1024 -O ^resize_inode", 64, 600 },
        { "csum_seed feature", "-t ext4 -O metadata_csum_seed", 200, 900 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Grown_file_system_passes_fsck_and_keeps_files(string name, string options, int initialMiB, int finalMiB)
    {
        if (Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var content = Path.Combine(dir.Path, "content");
        var files = CreateContent(content);
        var image = dir.File("fs.img");

        Run(Mke2fs, $"-q -F -d \"{content}\" {options} \"{image}\" {initialMiB}M");
        var before = BlockCount(image);
        using (var stream = File.OpenWrite(image))
        {
            stream.SetLength((long)finalMiB * 1024 * 1024);
        }

        Ext4GrowResult result;
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            result = Ext4Grow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
        }

        output.WriteLine($"{name}: {result}");
        var fsck = Run(FindTool("e2fsck")!, $"-fn \"{image}\"", allowFailure: true);
        output.WriteLine(fsck.Output);
        Assert.True(fsck.ExitCode == 0, $"e2fsck reported problems:\n{fsck.Output}");

        Assert.Equal(before, result.OldBlocks);
        Assert.Equal(BlockCount(image), result.NewBlocks);
        Assert.True(result.Grown, result.SkipReason.ToString());
        Assert.True((long)result.NewBlocks * result.BlockSize > (finalMiB - 80) * 1024L * 1024 || name.StartsWith("no resize_inode", StringComparison.Ordinal));

        // Files are intact.
        var restored = Path.Combine(dir.Path, "restored");
        Directory.CreateDirectory(restored);
        Run(FindTool("debugfs")!, $"-R \"rdump / {restored}\" \"{image}\"");
        foreach (var (relative, data) in files)
        {
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(restored, relative)));
        }

        // A second run has nothing left to do.
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            var again = Ext4Grow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
            Assert.False(again.Grown);
        }
    }

    [Fact]
    public async Task Writing_an_image_grows_the_last_ext4_partition()
    {
        if (Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var content = Path.Combine(dir.Path, "content");
        var files = CreateContent(content);

        // Disk image: MBR, FAT32 "boot" (1 MiB .. 9 MiB), ext4 "root" (9 MiB .. 209 MiB) like Raspberry Pi OS.
        const long mib = 1024 * 1024;
        var rootFs = dir.File("root.ext4");
        Run(Mke2fs, $"-q -F -d \"{content}\" -t ext4 -O ^64bit \"{rootFs}\" 200M");
        var imagePath = dir.File("disk.img");
        using (var image = File.Create(imagePath))
        {
            var mbr = new byte[512];
            mbr[510] = 0x55;
            mbr[511] = 0xAA;
            TestImages.WriteMbrEntry(mbr, 0, 0x0C, 2048, 16384);
            TestImages.WriteMbrEntry(mbr, 1, 0x83, 18432, 409600);
            image.Write(mbr);
            image.Position = 9 * mib;
            using (var fs = File.OpenRead(rootFs))
            {
                fs.CopyTo(image);
            }
        }

        using var container = FlashTool.Core.Containers.ContainerRegistry.Default.Open(new FlashTool.Core.Sources.FileStreamSource(imagePath));
        var target = dir.File("sdcard.bin");
        var result = await FlashTool.Core.Operations.WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(target, 1024 * mib),
            new FlashTool.Core.Operations.WriteImageOptions { ExpandLastPartition = true, FileSystemResizers = [new Ext4Resizer()] },
            null,
            CancellationToken.None);

        Assert.True(result.Fixups!.PartitionExtended);
        Assert.True(result.Fixups.FileSystemResized, result.Fixups.FileSystemResizeError);
        Assert.Equal(FlashTool.Core.Partitioning.FileSystemType.Ext4, result.Fixups.Partition!.FileSystem);

        // Extract the grown partition and check it.
        var partition = dir.File("part.ext4");
        using (var disk = File.OpenRead(target))
        using (var part = File.Create(partition))
        {
            disk.Position = 9 * mib;
            disk.CopyTo(part);
        }

        var fsck = Run(FindTool("e2fsck")!, $"-fn \"{partition}\"", allowFailure: true);
        Assert.True(fsck.ExitCode == 0, fsck.Output);
        Assert.True(BlockCount(partition) * 4096 >= 1000 * mib);

        var restored = Path.Combine(dir.Path, "restored");
        Directory.CreateDirectory(restored);
        Run(FindTool("debugfs")!, $"-R \"rdump / {restored}\" \"{partition}\"");
        foreach (var (relative, data) in files)
        {
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(restored, relative)));
        }
    }

    [Fact]
    public void Unclean_file_system_is_left_alone()
    {
        if (Mke2fs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var image = dir.File("fs.img");
        Run(Mke2fs, $"-q -F -t ext4 \"{image}\" 100M");
        Run(FindTool("debugfs")!, $"-w -R \"ssv state 0\" \"{image}\"");
        using (var stream = File.OpenWrite(image))
        {
            stream.SetLength(300L * 1024 * 1024);
        }

        var hashBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image)));
        using (var device = HandleBlockDevice.OpenFile(image))
        {
            var result = Ext4Grow.Grow(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
            Assert.Equal(Ext4GrowSkipReason.NotCleanlyUnmounted, result.SkipReason);
        }

        Assert.Equal(hashBefore, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image))));
    }

    private static Dictionary<string, byte[]> CreateContent(string root)
    {
        var random = new Random(42);
        var files = new Dictionary<string, byte[]>();
        for (var i = 0; i < 25; i++)
        {
            var relative = Path.Combine($"dir{i % 4}", $"sub{i % 3}", $"file{i}.bin");
            var data = new byte[random.Next(0, 3 * 1024 * 1024)];
            random.NextBytes(data);
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            files[relative] = data;
        }

        return files;
    }

    private static ulong BlockCount(string image)
    {
        var dump = Run(FindTool("dumpe2fs")!, $"-h \"{image}\"").Output;
        var line = dump.Split('\n').First(l => l.StartsWith("Block count:", StringComparison.Ordinal));
        return ulong.Parse(line["Block count:".Length..].Trim());
    }

    private static string? FindTool(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return ToolFolders
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);
    }

    private static (int ExitCode, string Output) Run(string tool, string arguments, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var text = stdout.Result + stderr;
        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} {arguments} failed ({process.ExitCode}):\n{text}");
        }

        return (process.ExitCode, text);
    }
}
