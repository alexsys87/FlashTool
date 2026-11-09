using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FlashTool.Core.Imaging;
using FlashTool.Core.IO;
using FlashTool.Core.Partitioning;

namespace FlashTool.Core.Tests;

public class VolumeLabelTests
{
    private static readonly string[] ToolFolders = ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];

    // ---- synthetic structures: run everywhere ----

    [Fact]
    public void Fat32_label_comes_from_the_boot_sector()
    {
        var boot = FatBoot(FileSystemType.Fat32, "BOOTFS");

        Assert.Equal(FileSystemType.Fat32, FileSystemProbe.Detect(boot));
        Assert.Equal("BOOTFS", Label(FileSystemType.Fat32, boot));
    }

    [Fact]
    public void Fat16_and_fat12_labels_sit_at_their_own_offset()
    {
        Assert.Equal("DATA16", Label(FileSystemType.Fat16, FatBoot(FileSystemType.Fat16, "DATA16")));
        Assert.Equal("FLOPPY", Label(FileSystemType.Fat12, FatBoot(FileSystemType.Fat12, "FLOPPY")));
    }

    [Theory]
    [InlineData("NO NAME")]
    [InlineData("no name")]
    [InlineData("")]
    public void A_fat_volume_without_a_label_has_none(string written)
    {
        Assert.Equal(string.Empty, Label(FileSystemType.Fat32, FatBoot(FileSystemType.Fat32, written)));
    }

    [Fact]
    public void Blanks_around_a_fat_label_are_dropped_but_inner_ones_stay()
    {
        Assert.Equal("My Stick", Label(FileSystemType.Fat32, FatBoot(FileSystemType.Fat32, "My Stick")));
    }

    [Fact]
    public void A_fat_label_in_an_oem_code_page_is_decoded_with_the_page_of_the_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            if (CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)?.CodePage != 866)
            {
                return;
            }

            var boot = FatBoot(FileSystemType.Fat32, string.Empty);
            byte[] cp866 = [0x94, 0x8B, 0x85, 0x98, 0x8A, 0x80]; // ФЛЕШКА
            cp866.CopyTo(boot.AsSpan(71));

            Assert.Equal("ФЛЕШКА", Label(FileSystemType.Fat32, boot));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("rootfs")]
    [InlineData("0123456789abcdef")]
    public void Ext_label_is_read_from_the_superblock(string label)
    {
        var data = ExtSuperblock(label);

        Assert.True(FileSystemProbe.Detect(data) is FileSystemType.Ext2 or FileSystemType.Ext3 or FileSystemType.Ext4);
        Assert.Equal(label, Label(FileSystemProbe.Detect(data), data));
    }

    [Fact]
    public void Ntfs_label_survives_the_fixups_even_when_it_crosses_a_sector_end()
    {
        const string label = "Backup drive with a long label";
        var (boot, mft, mftOffset) = NtfsVolume(label, fixupsMatch: true);

        var read = ReaderOf(mftOffset, mft);
        Assert.Equal(label, VolumeLabelReader.Read(FileSystemType.Ntfs, boot, 0, read));
    }

    [Fact]
    public void A_torn_ntfs_record_gives_no_label()
    {
        var (boot, mft, mftOffset) = NtfsVolume("Backup", fixupsMatch: false);

        Assert.Equal(string.Empty, VolumeLabelReader.Read(FileSystemType.Ntfs, boot, 0, ReaderOf(mftOffset, mft)));
    }

    [Fact]
    public void Ntfs_label_is_found_at_the_given_partition_offset()
    {
        var (boot, mft, mftOffset) = NtfsVolume("Archive", fixupsMatch: true);
        const long partition = 1024 * 1024;

        var read = ReaderOf(partition + mftOffset, mft);
        Assert.Equal("Archive", VolumeLabelReader.Read(FileSystemType.Ntfs, boot, partition, read));
        Assert.Equal(string.Empty, VolumeLabelReader.Read(FileSystemType.Ntfs, boot, 0, read));
    }

    [Fact]
    public void ExFat_label_is_the_volume_label_entry_of_the_root_directory()
    {
        var (boot, directory, directoryOffset) = ExFatVolume("Photos");

        Assert.Equal(FileSystemType.ExFat, FileSystemProbe.Detect(boot));
        Assert.Equal("Photos", VolumeLabelReader.Read(FileSystemType.ExFat, boot, 0, ReaderOf(directoryOffset, directory)));
    }

    [Theory]
    [InlineData("Фото")]
    [InlineData("日本語")]
    public void Ntfs_and_exfat_labels_are_utf16_and_keep_any_script(string label)
    {
        var (ntfsBoot, mft, mftOffset) = NtfsVolume(label, fixupsMatch: true);
        var (exfatBoot, directory, directoryOffset) = ExFatVolume(label);

        Assert.Equal(label, VolumeLabelReader.Read(FileSystemType.Ntfs, ntfsBoot, 0, ReaderOf(mftOffset, mft)));
        Assert.Equal(label, VolumeLabelReader.Read(FileSystemType.ExFat, exfatBoot, 0, ReaderOf(directoryOffset, directory)));
    }

    [Fact]
    public void A_deleted_exfat_label_entry_is_ignored()
    {
        var (boot, directory, directoryOffset) = ExFatVolume("Photos");
        directory[32] = 0x03; // not in use any more

        Assert.Equal(string.Empty, VolumeLabelReader.Read(FileSystemType.ExFat, boot, 0, ReaderOf(directoryOffset, directory)));
    }

    [Theory]
    [InlineData(FileSystemType.Ntfs)]
    [InlineData(FileSystemType.ExFat)]
    public void Labels_that_cannot_be_read_further_in_are_simply_missing(FileSystemType fileSystem)
    {
        var boot = fileSystem == FileSystemType.Ntfs ? NtfsVolume("x", fixupsMatch: true).Boot : ExFatVolume("x").Boot;

        Assert.Equal(string.Empty, VolumeLabelReader.Read(fileSystem, boot, 0, (_, _) => null));
    }

    [Theory]
    [InlineData(FileSystemType.Fat12)]
    [InlineData(FileSystemType.Fat16)]
    [InlineData(FileSystemType.Fat32)]
    [InlineData(FileSystemType.Ext4)]
    [InlineData(FileSystemType.Ntfs)]
    [InlineData(FileSystemType.ExFat)]
    [InlineData(FileSystemType.Btrfs)]
    [InlineData(FileSystemType.Unknown)]
    public void Damaged_or_random_data_never_throws(FileSystemType fileSystem)
    {
        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var data = new byte[random.Next(0, 5000)];
            random.NextBytes(data);
            if (round % 3 == 0 && data.Length > 0x80)
            {
                // Plausible small numbers where the parsers look for sizes and cluster numbers.
                data[0x0D] = (byte)random.Next(0, 3);
                data[0x6C] = (byte)random.Next(8, 14);
                data[0x6D] = (byte)random.Next(0, 6);
                data[0x40] = (byte)random.Next(0xF0, 0x100);
            }

            var label = VolumeLabelReader.Read(fileSystem, data, random.Next(0, 4) * 4096L, (_, count) => RandomBytes(random, Math.Min(count, 70_000)));

            Assert.NotNull(label);
            Assert.False(label.Any(char.IsControl));
        }
    }

    // ---- through the inspector ----

    [Fact]
    public void The_inspector_leaves_labels_alone_unless_asked()
    {
        using var dir = new TempDir();
        var disk = PartitionedDisk(dir, FileSystemType.Fat32, FatBoot(FileSystemType.Fat32, "BOOTFS"));

        using var device = HandleBlockDevice.OpenFile(disk);
        var head = device.ReadBytes(0, 1024 * 1024)!;
        var without = ImageInspector.Inspect(head, (o, c) => device.ReadBytes(o, c));
        var with = ImageInspector.Inspect(head, (o, c) => device.ReadBytes(o, c), readLabels: true);

        Assert.Equal(string.Empty, without.Partitions[0].Label);
        Assert.Equal("BOOTFS", with.Partitions[0].Label);
        Assert.Equal(FileSystemType.Fat32, with.Partitions[0].FileSystem);
    }

    [Fact]
    public void A_device_with_a_file_system_and_no_partition_table_reports_the_label_of_the_whole_volume()
    {
        using var dir = new TempDir();
        var path = dir.File("stick.img");
        var boot = FatBoot(FileSystemType.Fat32, "CAMERA");
        var image = new byte[2 * 1024 * 1024];
        boot.CopyTo(image, 0);
        File.WriteAllBytes(path, image);

        using var device = HandleBlockDevice.OpenFile(path);
        var info = ImageInspector.InspectDevice(device);

        Assert.Equal(ImageKind.Superfloppy, info.Kind);
        Assert.Equal(FileSystemType.Fat32, info.RootFileSystem);
        Assert.Equal("CAMERA", info.RootLabel);
    }

    [Fact]
    public void A_blank_device_is_reported_as_unrecognized()
    {
        using var dir = new TempDir();
        var path = dir.File("blank.img");
        File.WriteAllBytes(path, new byte[2 * 1024 * 1024]);

        using var device = HandleBlockDevice.OpenFile(path);
        var info = ImageInspector.InspectDevice(device);

        Assert.Equal(ImageKind.Unknown, info.Kind);
        Assert.Empty(info.Partitions);
    }

    [Fact]
    public void A_device_smaller_than_a_sector_is_unrecognized_rather_than_an_error()
    {
        using var dir = new TempDir();
        var path = dir.File("tiny.img");
        File.WriteAllBytes(path, new byte[512]);

        using var device = HandleBlockDevice.OpenFile(path);

        Assert.Equal(ImageKind.Unknown, ImageInspector.InspectDevice(device).Kind);
    }

    // ---- real file systems: run where the mkfs tools are installed (Linux), a no-op elsewhere ----

    [Theory]
    [InlineData("mkfs.fat", "-F 32 -n BOOTFS", FileSystemType.Fat32, "BOOTFS", 0x0C)]
    [InlineData("mkfs.fat", "-F 16 -n DATA16", FileSystemType.Fat16, "DATA16", 0x0E)]
    [InlineData("mkfs.exfat", "-L PHOTOS", FileSystemType.ExFat, "PHOTOS", 0x07)]
    [InlineData("mkntfs", "-F -Q -L Backup", FileSystemType.Ntfs, "Backup", 0x07)]
    [InlineData("mke2fs", "-q -F -t ext4 -L rootfs", FileSystemType.Ext4, "rootfs", 0x83)]
    [InlineData("mke2fs", "-q -F -t ext2 -L old-data", FileSystemType.Ext2, "old-data", 0x83)]
    [InlineData("mke2fs", "-q -F -t ext4 -b 1024 -L smallblocks", FileSystemType.Ext4, "smallblocks", 0x83)]
    [InlineData("mkfs.fat", "-F 32 -s 8 -n CLUSTERS", FileSystemType.Fat32, "CLUSTERS", 0x0C)]
    [InlineData("mkfs.exfat", "-c 131072 -L BIGCLUSTER", FileSystemType.ExFat, "BIGCLUSTER", 0x07)]
    [InlineData("mkfs.exfat", "-c 4096 -L small", FileSystemType.ExFat, "small", 0x07)]
    [InlineData("mkntfs", "-F -Q -s 4096 -c 4096 -L Native4k", FileSystemType.Ntfs, "Native4k", 0x07)]
    [InlineData("mkntfs", "-F -Q -c 65536 -L Huge", FileSystemType.Ntfs, "Huge", 0x07)]
    public void Labels_of_real_file_systems_are_read_inside_a_partition(string tool, string options, FileSystemType expected, string label, int mbrType)
    {
        var path = FindTool(tool);
        if (path == null)
        {
            return;
        }

        using var dir = new TempDir();
        var volume = dir.File("volume.img");
        MakeVolume(path, options, volume, tool.StartsWith("mke2fs", StringComparison.Ordinal) ? "32M" : null);

        var disk = PartitionedDisk(dir, volume, (byte)mbrType);
        using var device = HandleBlockDevice.OpenFile(disk);
        var info = ImageInspector.InspectDevice(device);

        var partition = Assert.Single(info.Partitions);
        Assert.Equal(expected, partition.FileSystem);
        Assert.Equal(label, partition.Label);
    }

    [Fact]
    public void A_real_ntfs_without_a_label_has_none()
    {
        var path = FindTool("mkntfs");
        if (path == null)
        {
            return;
        }

        using var dir = new TempDir();
        var volume = dir.File("volume.img");
        MakeVolume(path, "-F -Q", volume, null);

        using var device = HandleBlockDevice.OpenFile(PartitionedDisk(dir, volume, 0x07));
        var partition = Assert.Single(ImageInspector.InspectDevice(device).Partitions);

        Assert.Equal(FileSystemType.Ntfs, partition.FileSystem);
        Assert.Equal(string.Empty, partition.Label);
    }

    // ---- helpers: building structures ----

    private static string Label(FileSystemType fileSystem, byte[] data) =>
        VolumeLabelReader.Read(fileSystem, data, 0, (_, _) => null);

    private static byte[] FatBoot(FileSystemType type, string label)
    {
        var boot = new byte[512];
        var (labelOffset, nameOffset, name) = type switch
        {
            FileSystemType.Fat32 => (71, 82, "FAT32   "),
            FileSystemType.Fat16 => (43, 54, "FAT16   "),
            _ => (43, 54, "FAT12   "),
        };
        Encoding.ASCII.GetBytes(name).CopyTo(boot, nameOffset);
        Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(boot, labelOffset);
        boot[510] = 0x55;
        boot[511] = 0xAA;
        return boot;
    }

    private static byte[] ExtSuperblock(string label)
    {
        var data = new byte[2048];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1024 + 56), 0xEF53);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024 + 96), 0x40); // extents: ext4
        Encoding.UTF8.GetBytes(label).CopyTo(data, 1024 + 120);
        return data;
    }

    /// <summary>A boot sector and the MFT record 3 ($Volume) with the label as its $VOLUME_NAME attribute.</summary>
    private static (byte[] Boot, byte[] Mft, long MftOffset) NtfsVolume(string label, bool fixupsMatch)
    {
        const int mftCluster = 4;
        const int clusterSize = 4096;
        const int recordSize = 1024;

        var boot = new byte[512];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(boot, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(0x0B), 512);
        boot[0x0D] = 8;
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x30), mftCluster);
        boot[0x40] = unchecked((byte)-10); // 2^10 bytes per record

        var record = new byte[recordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 0x30); // update sequence array
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);    // sequence number plus two sectors
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x14), 0x38);

        // A filler attribute pushes the name to where its text crosses the end of the first sector.
        var position = 0x38;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(position), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(position + 4), 0x180);
        position += 0x180;

        var name = Encoding.Unicode.GetBytes(label);
        var length = (0x18 + name.Length + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(position), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(position + 4), (uint)length);
        record[position + 8] = 0; // resident
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(position + 0x10), (uint)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(position + 0x14), 0x18);
        name.CopyTo(record, position + 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(position + length), 0xFFFFFFFF);

        // What NTFS stores: the last two bytes of each 512 byte block are swapped for the sequence number.
        const ushort sequence = 0x0007;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x30), sequence);
        for (var block = 1; block <= 2; block++)
        {
            var end = (block * 512) - 2;
            record.AsSpan(end, 2).CopyTo(record.AsSpan(0x30 + (block * 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(end), fixupsMatch ? sequence : (ushort)(sequence + 1));
        }

        return (boot, record, (mftCluster * (long)clusterSize) + (3L * recordSize));
    }

    /// <summary>A boot sector and the first cluster of the root directory, which holds the label entry second.</summary>
    private static (byte[] Boot, byte[] Directory, long DirectoryOffset) ExFatVolume(string label)
    {
        const int heapSectors = 128;
        const int rootCluster = 4;
        const int clusterSize = 4096;

        var boot = new byte[512];
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(boot, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x58), heapSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x60), rootCluster);
        boot[0x6C] = 9;
        boot[0x6D] = 3;

        var directory = new byte[clusterSize];
        directory[0] = 0x81; // allocation bitmap, before the label as on a real volume
        directory[32] = 0x83;
        directory[33] = (byte)label.Length;
        Encoding.Unicode.GetBytes(label).CopyTo(directory, 34);

        return (boot, directory, ((long)heapSectors * 512) + ((rootCluster - 2) * (long)clusterSize));
    }

    /// <summary>A reader that knows one range of bytes and returns null for anything else.</summary>
    private static Func<long, int, byte[]?> ReaderOf(long offset, byte[] data) =>
        (o, count) => o == offset && count <= data.Length ? data[..count] : null;

    private static byte[] RandomBytes(Random random, int count)
    {
        var data = new byte[count];
        random.NextBytes(data);
        return data;
    }

    /// <summary>An MBR disk whose only partition starts at 1 MiB and holds the given boot sector.</summary>
    private static string PartitionedDisk(TempDir dir, FileSystemType type, byte[] boot)
    {
        var volume = dir.File("volume.bin");
        var data = new byte[1024 * 1024];
        boot.CopyTo(data, 0);
        File.WriteAllBytes(volume, data);
        return PartitionedDisk(dir, volume, type == FileSystemType.Fat32 ? (byte)0x0C : (byte)0x07);
    }

    private static string PartitionedDisk(TempDir dir, string volumePath, byte mbrType)
    {
        const long start = 1024 * 1024;
        var volume = File.ReadAllBytes(volumePath);
        var disk = new byte[start + volume.Length + (1024 * 1024)];
        volume.CopyTo(disk, start);

        var entry = disk.AsSpan(446, 16);
        entry[0] = 0x80;
        entry[4] = mbrType;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)(start / 512));
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)(volume.Length / 512));
        disk[510] = 0x55;
        disk[511] = 0xAA;

        var path = dir.File("disk.img");
        File.WriteAllBytes(path, disk);
        return path;
    }

    // ---- helpers: external tools ----

    private static string? FindTool(string name) =>
        ToolFolders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

    private static void MakeVolume(string tool, string options, string image, string? size)
    {
        using (var stream = File.Create(image))
        {
            stream.SetLength(32L * 1024 * 1024);
        }

        var arguments = size == null ? $"{options} \"{image}\"" : $"{options} \"{image}\" {size}";
        using var process = Process.Start(new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} {arguments} failed:\n{output}");
    }
}
