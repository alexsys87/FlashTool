using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Tests;

public class PartitionTableTests
{
    [Fact]
    public void Mbr_image_is_recognized_with_partitions_and_file_systems()
    {
        var image = TestImages.CreateMbrImage(trailingSectors: 100);

        var info = ImageInspector.Inspect(image);

        Assert.Equal(ImageKind.MbrDisk, info.Kind);
        Assert.Equal(PartitionTableType.Mbr, info.TableType);
        Assert.Equal(2, info.Partitions.Count);
        Assert.Equal(FileSystemType.Fat32, info.Partitions[0].FileSystem);
        Assert.Equal(FileSystemType.Ext4, info.Partitions[1].FileSystem);
        Assert.Equal((4096L + 4096) * 512, info.UsedLength);
    }

    [Fact]
    public void Gpt_image_is_recognized()
    {
        var image = TestImages.CreateGptImage();

        var info = ImageInspector.Inspect(image);

        Assert.Equal(ImageKind.GptDisk, info.Kind);
        Assert.Equal(2, info.Partitions.Count);
        Assert.Equal("EFI System", info.Partitions[0].TypeDescription);
        Assert.Equal("root", info.Partitions[1].Name);
        Assert.Equal(FileSystemType.Ext4, info.Partitions[1].FileSystem);
    }

    [Fact]
    public void Random_data_and_text_are_not_disk_images()
    {
        var random = new byte[1024 * 1024];
        new Random(5).NextBytes(random);
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Hello, this is a README file.\n", 2000)));

        Assert.False(ImageInspector.Inspect(random).IsDiskImage);
        Assert.False(ImageInspector.Inspect(text).IsDiskImage);
        Assert.False(ImageInspector.Inspect(new byte[1024 * 1024]).IsDiskImage);
    }

    [Fact]
    public void Fat_superfloppy_is_not_mistaken_for_mbr()
    {
        var image = TestImages.CreateMbrImage();
        var vbr = image.AsSpan(2048 * 512, 512).ToArray();
        var floppy = new byte[2048 * 512];
        vbr.CopyTo(floppy, 0);

        var info = ImageInspector.Inspect(floppy);

        Assert.Equal(ImageKind.Superfloppy, info.Kind);
        Assert.Equal(FileSystemType.Fat32, info.RootFileSystem);
        Assert.Equal(2048L * 512, info.UsedLength);
    }

    [Fact]
    public void Gpt_backup_is_relocated_and_last_partition_extended()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateGptImage(totalSectors: 16384);
        var deviceSectors = 40000L;

        using (var device = HandleBlockDevice.OpenFile(dir.File("disk.bin"), deviceSectors * 512))
        {
            device.Write(0, image);
            var result = PartitionTableEditor.Apply(device, ImageInspector.Inspect(image), extendLastPartition: true);

            Assert.True(result.GptBackupRelocated);
            Assert.True(result.PartitionExtended);
            Assert.Equal(FileSystemType.Ext4, result.Partition!.FileSystem);
        }

        var disk = File.ReadAllBytes(dir.File("disk.bin"));
        var lastLba = deviceSectors - 1;
        var primary = GptHeader.TryParse(disk.AsSpan(512, 512))!;
        var backup = GptHeader.TryParse(disk.AsSpan((int)(lastLba * 512), 512))!;

        Assert.NotNull(primary);
        Assert.NotNull(backup);
        Assert.Equal(lastLba, primary.AlternateLba);
        Assert.Equal(lastLba - 33, primary.LastUsableLba);
        Assert.Equal(lastLba, backup.MyLba);
        Assert.Equal(lastLba - 32, backup.PartitionEntryLba);

        var table = GptPartitionTable.TryRead((o, c) => disk.AsSpan((int)o, c).ToArray())!;
        Assert.True(table.EntriesCrcValid);
        Assert.Equal(lastLba - 33, table.Entries.Single(e => e.Name == "root").LastLba);

        var backupEntries = disk.AsSpan((int)((lastLba - 32) * 512), table.EntriesRaw.Length).ToArray();
        Assert.Equal(table.EntriesRaw, backupEntries);

        var protective = MbrPartitionTable.TryParse(disk.AsSpan(0, 512))!;
        Assert.Equal((uint)lastLba, protective.Entries[0].SectorCount);
    }

    [Fact]
    public void Gpt_backup_is_relocated_without_extension_when_disabled()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateGptImage(totalSectors: 16384);

        using var device = HandleBlockDevice.OpenFile(dir.File("disk.bin"), 30000L * 512);
        device.Write(0, image);
        var result = PartitionTableEditor.Apply(device, ImageInspector.Inspect(image), extendLastPartition: false);

        Assert.True(result.GptBackupRelocated);
        Assert.False(result.PartitionExtended);
        var table = GptPartitionTable.TryRead((o, c) => device.ReadBytes(o, c))!;
        Assert.Equal(16383 - 34, table.Entries.Single(e => e.Name == "root").LastLba);
    }

    [Fact]
    public void Mbr_last_partition_is_extended()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage();
        var deviceSectors = 20000L;

        using var device = HandleBlockDevice.OpenFile(dir.File("disk.bin"), deviceSectors * 512);
        device.Write(0, image);
        var result = PartitionTableEditor.Apply(device, ImageInspector.Inspect(image), extendLastPartition: true);

        Assert.True(result.PartitionExtended);
        Assert.Equal(4096L * 512, result.OldLength);
        Assert.Equal((deviceSectors - 4096) * 512, result.NewLength);
        var mbr = MbrPartitionTable.TryParse(device.ReadBytes(0, 512))!;
        Assert.Equal((uint)(deviceSectors - 4096), mbr.Entries[1].SectorCount);
        Assert.Equal(2048u, mbr.Entries[0].SectorCount);
    }
}
