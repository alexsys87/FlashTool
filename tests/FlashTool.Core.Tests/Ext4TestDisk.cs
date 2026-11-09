using System.Buffers.Binary;

namespace FlashTool.Core.Tests;

/// <summary>
/// Disks with a real ext4 file system made by mke2fs, for tests that run where e2fsprogs is installed:
/// an MBR, random "boot loader" bytes, one Linux partition at 1 MiB and a random tail.
/// </summary>
internal static class Ext4TestDisk
{
    public const int Sector = 512;
    public const long PartitionOffset = 1024 * 1024;
    public const int TailBytes = 1024 * 1024;

    public static readonly string? Mke2fs = E2fsprogs.Find("mke2fs");

    /// <summary>
    /// Makes fs.img with a few random files. With <paramref name="fillFreeSpace"/> it is made on top of
    /// non-zero data, so free blocks are not zero; otherwise free space is left as holes of a sparse file.
    /// </summary>
    public static Dictionary<string, byte[]> MakeFileSystem(TempDir dir, string options, int sizeMiB, bool fillFreeSpace = true)
    {
        var content = Path.Combine(dir.Path, "content");
        var files = new Dictionary<string, byte[]>();
        var random = new Random(7);
        for (var i = 0; i < 12; i++)
        {
            var relative = Path.Combine($"dir{i % 3}", $"file{i}.bin");
            var data = new byte[random.Next(0, 4 * 1024 * 1024)];
            random.NextBytes(data);
            Directory.CreateDirectory(Path.Combine(content, $"dir{i % 3}"));
            File.WriteAllBytes(Path.Combine(content, relative), data);
            files[relative] = data;
        }

        if (fillFreeSpace)
        {
            var pattern = new byte[1024 * 1024];
            random.NextBytes(pattern);
            using var fs = File.Create(dir.File("fs.img"));
            for (var i = 0; i < sizeMiB; i++)
            {
                fs.Write(pattern);
            }
        }

        E2fsprogs.Run(Mke2fs!, $"-q -F -E nodiscard -d \"{content}\" {options} \"{dir.File("fs.img")}\" {sizeMiB}M");
        return files;
    }

    /// <summary>
    /// Puts fs.img into disk.bin. With <paramref name="sparse"/> the file gets holes wherever a 4 KiB block is
    /// zero, the way wic leaves its images, so that bmaptool maps only the data.
    /// </summary>
    public static string MakeDisk(TempDir dir, bool sparse = false)
    {
        var fsBytes = File.ReadAllBytes(dir.File("fs.img"));
        var head = new byte[PartitionOffset];
        new Random(11).NextBytes(head);
        head.AsSpan(446, 64).Clear();
        head[446 + 4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(446 + 8), (uint)(PartitionOffset / Sector));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(446 + 12), (uint)(fsBytes.Length / Sector));
        head[510] = 0x55;
        head[511] = 0xAA;

        var tail = new byte[TailBytes];
        new Random(13).NextBytes(tail);

        var path = dir.File("disk.bin");
        using var disk = File.Create(path);
        disk.Write(head);
        if (sparse)
        {
            const int block = 4096;
            for (var offset = 0; offset < fsBytes.Length; offset += block)
            {
                var data = fsBytes.AsSpan(offset, Math.Min(block, fsBytes.Length - offset));
                if (data.IndexOfAnyExcept((byte)0) < 0)
                {
                    disk.Seek(data.Length, SeekOrigin.Current);
                }
                else
                {
                    disk.Write(data);
                }
            }
        }
        else
        {
            disk.Write(fsBytes);
        }

        disk.Write(tail);
        return path;
    }
}
