using System.Buffers.Binary;
using FlashTool.Core.Containers;
using FlashTool.Core.FileSystems;
using FlashTool.Core.FileSystems.Ext4;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Partitioning;
using FlashTool.Core.Sources;
using Xunit.Abstractions;

namespace FlashTool.Core.Tests;

/// <summary>
/// Offline journal replay against e2fsprogs: file systems with transactions left in the journal (written by
/// debugfs, as a system that was not shut down leaves them) are replayed by us and by debugfs' journal_run,
/// which is e2fsck's recovery code, and must come out the same. Runs where e2fsprogs is installed (Linux).
/// </summary>
public class Ext4JournalTests(ITestOutputHelper output)
{
    private const int MiB = 1024 * 1024;

    private static readonly string? Mke2fs = E2fsprogs.Find("mke2fs");
    private static readonly string? Debugfs = E2fsprogs.Find("debugfs");

    public static TheoryData<string, string, string> Cases => new()
    {
        // name, mke2fs options, debugfs script (one command per line)
        { "one transaction", "-t ext4", "jo\njw -b 20000,20001 {data}\njc" },
        { "checksums v3, revoke", "-t ext4", "jo -c\njw -b 20000,20001,20002 {data}\njc\njo -c\njw -r 20001\njc" },
        { "several transactions, later one wins", "-t ext4", "jo -c\njw -b 20000,20001 {data}\njc\njo -c\njw -b 20001,20002 {data2}\njc" },
        { "revoked, then written again", "-t ext4", "jo -c\njw -b 20000 {data}\njc\njo -c\njw -r 20000\njc\njo -c\njw -b 20000 {data2}\njc" },
        { "checksums v2", "-t ext4", "jo -c -v 2\njw -b 20000,20001 {data}\njc" },
        { "superblock and descriptors journaled", "-t ext4", "jo -c\njw -b 0,1 {meta}\njc" },
        { "ext3 (block-mapped journal)", "-t ext3", "jo\njw -b 20000,20001 {data}\njc" },
        { "no 64bit", "-t ext4 -O ^64bit", "jo -c\njw -b 20000,20001 {data}\njc" },
        { "1k blocks", "-t ext4 -b 1024", "jo -c\njw -b 40000,40001 {data}\njc" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Replay_matches_e2fsprogs(string name, string options, string script)
    {
        if (Mke2fs == null || Debugfs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var fs = MakeJournaledFileSystem(dir, options, script);
        File.Copy(fs, dir.File("ours.img"));
        File.Copy(fs, dir.File("theirs.img"));

        JournalRecovery outcome;
        using (var device = HandleBlockDevice.OpenFile(dir.File("ours.img")))
        {
            outcome = Ext4Journal.Recover(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
        }

        E2fsprogs.Run(Debugfs, $"-w -R jr \"{dir.File("theirs.img")}\"");
        Assert.Equal(JournalRecovery.Replayed, outcome);

        var ours = File.ReadAllBytes(dir.File("ours.img"));
        var theirs = File.ReadAllBytes(dir.File("theirs.img"));

        // Bookkeeping that e2fsprogs updates and we leave alone: last write time, last check time,
        // lifetime kilobytes written, and the superblock checksum that covers them.
        foreach (var image in new[] { ours, theirs })
        {
            foreach (var (offset, length) in new[] { (0x30, 4), (0x40, 4), (0x178, 8), (0x3FC, 4) })
            {
                image.AsSpan(1024 + offset, length).Clear();
            }
        }

        var differing = Enumerable.Range(0, ours.Length / 1024).Where(k => !ours.AsSpan(k * 1024, 1024).SequenceEqual(theirs.AsSpan(k * 1024, 1024))).ToList();
        output.WriteLine($"{name}: differing KiB at {string.Join(", ", differing.Take(20))}");
        Assert.Empty(differing);
        Assert.DoesNotContain("needs_recovery", E2fsprogs.Run(E2fsprogs.Find("dumpe2fs")!, $"-h \"{dir.File("ours.img")}\"").Output);

        var fsck = E2fsprogs.Run(E2fsprogs.Find("e2fsck")!, $"-fn \"{dir.File("ours.img")}\"", allowFailure: true);
        Assert.True(fsck.ExitCode == 0, fsck.Output);
    }

    [Theory]
    [InlineData("data block")]
    [InlineData("commit-less descriptor")]
    public void Damaged_journal_is_not_replayed_and_nothing_is_written(string what)
    {
        if (Mke2fs == null || Debugfs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var fs = MakeJournaledFileSystem(dir, "-t ext4", "jo -c\njw -b 20000,20001 {data}\njc");
        var journalStart = JournalBlock(dir, fs);
        var bytes = File.ReadAllBytes(fs);

        // The log: descriptor at journal block 1, the two data blocks, then the commit.
        if (what == "data block")
        {
            bytes[(journalStart + 2) * 4096 + 100] ^= 0x40;
        }
        else
        {
            // A descriptor whose checksum does not match: the kernel refuses to mount; we refuse to replay.
            bytes[(journalStart + 1) * 4096 + 20] ^= 0x01;
        }

        File.WriteAllBytes(fs, bytes);
        JournalRecovery outcome;
        using (var device = HandleBlockDevice.OpenFile(fs))
        {
            outcome = Ext4Journal.Recover(new PartitionAccess(device, 0, device.Length), CancellationToken.None);
        }

        Assert.Equal(JournalRecovery.Corrupt, outcome);
        Assert.Equal(bytes, File.ReadAllBytes(fs));
    }

    [Fact]
    public void A_commit_with_a_wrong_checksum_ends_the_log()
    {
        if (Mke2fs == null || Debugfs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var fs = MakeJournaledFileSystem(dir, "-t ext4", "jo -c\njw -b 20000 {data}\njc\njo -c\njw -b 20001 {data2}\njc");
        var journalStart = JournalBlock(dir, fs);
        var before = File.ReadAllBytes(fs);

        // Log: desc, data, commit (transaction 1), desc, data, commit (transaction 2). Break the second commit.
        var bytes = (byte[])before.Clone();
        bytes[(journalStart + 6) * 4096 + 0x40] ^= 0x01;
        File.WriteAllBytes(fs, bytes);
        File.WriteAllBytes(dir.File("theirs.img"), bytes);

        using (var device = HandleBlockDevice.OpenFile(fs))
        {
            Assert.Equal(JournalRecovery.Replayed, Ext4Journal.Recover(new PartitionAccess(device, 0, device.Length), CancellationToken.None));
        }

        var data = File.ReadAllBytes(dir.File("data"));
        var data2 = File.ReadAllBytes(dir.File("data2"));
        var after = File.ReadAllBytes(fs);
        Assert.Equal(data.AsSpan(0, 4096).ToArray(), after.AsSpan(20000 * 4096, 4096).ToArray());
        Assert.False(after.AsSpan(20001 * 4096, 4096).SequenceEqual(data2.AsSpan(0, 4096)), "The uncommitted transaction was replayed.");
    }

    [Fact]
    public async Task Image_from_a_running_system_is_grown_after_replaying_its_journal()
    {
        if (Mke2fs == null || Debugfs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var fs = MakeJournaledFileSystem(dir, "-t ext4", "jo -c\njw -b 20000,20001 {data}\njc");
        var disk = Disk(dir, File.ReadAllBytes(fs));

        var result = await WriteAsync(dir, disk, 1024L * MiB);

        var fixups = result.Fixups!;
        Assert.True(fixups.PartitionExtended);
        Assert.True(fixups.FileSystemResized, fixups.FileSystemSkipReason.ToString());
        Assert.True(fixups.JournalReplayed);

        var grown = Partition(dir.File("device.bin"), fixups.Partition!);
        var dump = E2fsprogs.Run(E2fsprogs.Find("dumpe2fs")!, $"-h \"{grown}\"").Output;
        Assert.DoesNotContain("needs_recovery", dump);
        Assert.True(BlockCount(dump) * 4096L > 900L * MiB);
        var fsck = E2fsprogs.Run(E2fsprogs.Find("e2fsck")!, $"-fn \"{grown}\"", allowFailure: true);
        Assert.True(fsck.ExitCode == 0, fsck.Output);

        // What the journal held is in place.
        var data = File.ReadAllBytes(dir.File("data"));
        Assert.Equal(data.AsSpan(0, 8192).ToArray(), File.ReadAllBytes(grown).AsSpan(20000 * 4096, 8192).ToArray());
    }

    [Theory]
    [InlineData("ssv state 3", FileSystemResizeSkipReason.HasErrors)]
    [InlineData("ssv state 0", FileSystemResizeSkipReason.NotCleanlyUnmounted)]
    [InlineData("feature meta_bg", FileSystemResizeSkipReason.UnsupportedFeature)]
    public async Task The_reason_is_reported_and_nothing_is_written_to_the_file_system(string debugfsCommand, FileSystemResizeSkipReason expected)
    {
        if (Mke2fs == null || Debugfs == null)
        {
            return;
        }

        using var dir = new TempDir();
        var fs = MakeJournaledFileSystem(dir, "-t ext4", "jo -c\njw -b 20000 {data}\njc");
        E2fsprogs.Run(Debugfs, $"-w -R \"{debugfsCommand}\" \"{fs}\"");
        var original = File.ReadAllBytes(fs);
        var disk = Disk(dir, original);

        var result = await WriteAsync(dir, disk, 1024L * MiB);

        var fixups = result.Fixups!;
        Assert.True(fixups.PartitionExtended);
        Assert.False(fixups.FileSystemResized);
        Assert.Equal(expected, fixups.FileSystemSkipReason);
        Assert.False(fixups.JournalReplayed);
        var written = File.ReadAllBytes(Partition(dir.File("device.bin"), fixups.Partition!));
        Assert.Equal(original, written.AsSpan(0, original.Length).ToArray());
    }

    /// <summary>The first journal block of the file system, from the journal inode's only extent.</summary>
    private static long JournalBlock(TempDir dir, string fs)
    {
        var stat = E2fsprogs.Run(Debugfs!, $"-R \"bmap <8> 0\" \"{fs}\"").Output;
        return long.Parse(stat.Split('\n').Last(l => l.Trim().Length > 0 && char.IsDigit(l.Trim()[0])).Trim());
    }

    private static string Disk(TempDir dir, byte[] fs)
    {
        var disk = new byte[MiB + fs.Length];
        disk[510] = 0x55;
        disk[511] = 0xAA;
        TestImages.WriteMbrEntry(disk, 0, 0x83, MiB / 512, (uint)(fs.Length / 512));
        fs.CopyTo(disk, MiB);
        var path = dir.File("disk.img");
        File.WriteAllBytes(path, disk);
        return path;
    }

    private static async Task<WriteImageResult> WriteAsync(TempDir dir, string disk, long deviceSize)
    {
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(disk));
        return await WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(dir.File("device.bin"), deviceSize),
            new WriteImageOptions { ExpandLastPartition = true, FileSystemResizers = [new Ext4Resizer()] },
            null,
            CancellationToken.None);
    }

    private static string Partition(string device, PartitionInfo partition)
    {
        var path = device + ".p";
        using var source = File.OpenRead(device);
        using var target = File.Create(path);
        source.Position = partition.Offset;
        var buffer = new byte[MiB];
        for (long done = 0; done < partition.Length; done += buffer.Length)
        {
            var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, partition.Length - done));
            target.Write(buffer, 0, n);
        }

        return path;
    }

    private static long BlockCount(string dump)
        => long.Parse(dump.Split('\n').First(l => l.StartsWith("Block count:", StringComparison.Ordinal))["Block count:".Length..].Trim());

    private static string MakeJournaledFileSystem(TempDir dir, string options, string script)
    {
        var fs = dir.File("fs.img");
        E2fsprogs.Run(Mke2fs!, $"-q -F {options} \"{fs}\" 256M");
        var random = new Random(9);
        foreach (var (file, size) in new[] { ("data", 3 * 4096), ("data2", 3 * 4096) })
        {
            var bytes = new byte[size];
            random.NextBytes(bytes);

            // A block that starts with the journal magic has to be escaped in the log.
            BinaryPrimitives.WriteUInt32BigEndian(bytes, 0xC03B3998);
            File.WriteAllBytes(dir.File(file), bytes);
        }

        // Journal the current superblock and descriptor block, slightly changed, as the kernel does all the time.
        using (var stream = File.OpenRead(fs))
        {
            var head = new byte[8192];
            stream.ReadExactly(head);
            File.WriteAllBytes(dir.File("meta"), head);
        }

        var commands = script.Replace("{data}", dir.File("data")).Replace("{data2}", dir.File("data2")).Replace("{meta}", dir.File("meta"));
        File.WriteAllText(dir.File("script"), commands + "\n");
        E2fsprogs.Run(Debugfs!, $"-w -f \"{dir.File("script")}\" \"{fs}\"");
        Assert.Contains("needs_recovery", E2fsprogs.Run(E2fsprogs.Find("dumpe2fs")!, $"-h \"{fs}\"").Output);
        return fs;
    }
}
