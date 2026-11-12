using System.Buffers.Binary;
using System.Text;

namespace FlashTool.Core.Tests;

/// <summary>
/// A minimal exFAT writer and reader for tests, independent of the code under test: adds files and
/// directories to a freshly formatted volume (contiguous or deliberately fragmented) and reads a whole
/// tree back. Works on a volume image file; names are ASCII.
/// </summary>
internal sealed class ExFatTestVolume : IDisposable
{
    private const uint EndOfChain = 0xFFFFFFFF;
    private readonly FileStream _file;
    private readonly long _base;
    private readonly int _bps;
    private readonly long _fatOffset;
    private readonly long _heapOffset;
    private readonly long _bitmapPosition;

    public ExFatTestVolume(string path, long volumeOffset = 0)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        _base = volumeOffset;
        var boot = ReadAt(0, 512);
        Assert.Equal("EXFAT   ", Encoding.ASCII.GetString(boot, 3, 8));
        _bps = 1 << boot[108];
        ClusterSize = _bps << boot[109];
        TotalSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(72));
        _fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(80)) * (long)_bps;
        _heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(88)) * (long)_bps;
        ClusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(92));
        RootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96));

        var root = ReadAt(ClusterPosition(RootCluster), ClusterSize);
        for (var i = 0; i < root.Length; i += 32)
        {
            if (root[i] == 0x81)
            {
                BitmapCluster = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(i + 20));
                BitmapBytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(i + 24));
                _bitmapPosition = ClusterPosition(BitmapCluster);
            }
        }

        Assert.NotEqual(0u, BitmapCluster);
    }

    public int ClusterSize { get; }

    public long TotalSectors { get; }

    public long ClusterCount { get; }

    public uint RootCluster { get; }

    public uint BitmapCluster { get; }

    public long BitmapBytes { get; }

    public long BytesPerSector => _bps;

    public void Dispose() => _file.Dispose();

    /// <summary>Adds a file; fragmented files take every other free cluster and get a FAT chain.</summary>
    public void AddFile(uint directory, string name, byte[] data, bool fragmented)
    {
        var count = (data.Length + ClusterSize - 1) / ClusterSize;
        var clusters = Allocate(count, fragmented);
        for (var i = 0; i < count; i++)
        {
            var chunk = data.AsSpan(i * ClusterSize, Math.Min(ClusterSize, data.Length - (i * ClusterSize)));
            WriteAt(ClusterPosition(clusters[i]), chunk);
            if (fragmented)
            {
                WriteFat(clusters[i], i == count - 1 ? EndOfChain : clusters[i + 1]);
            }
        }

        AddEntrySet(directory, name, isDirectory: false, count > 0 ? clusters[0] : 0, data.Length, contiguous: !fragmented && count > 0);
    }

    /// <summary>Adds an empty one-cluster directory and returns its cluster.</summary>
    public uint AddDirectory(uint parent, string name, bool contiguous)
    {
        var cluster = Allocate(1, false)[0];
        WriteAt(ClusterPosition(cluster), new byte[ClusterSize]);
        if (!contiguous)
        {
            WriteFat(cluster, EndOfChain);
        }

        AddEntrySet(parent, name, isDirectory: true, cluster, ClusterSize, contiguous);
        return cluster;
    }

    public void SetVolumeFlags(ushort flags)
    {
        // Not part of the boot checksum.
        var raw = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(raw, flags);
        WriteAt(106, raw);
    }

    /// <summary>All files of the volume by path ("dir/name"), read through FAT chains or contiguous runs.</summary>
    public Dictionary<string, byte[]> ReadTree()
    {
        var files = new Dictionary<string, byte[]>();
        ReadDirectory(ReadChain(RootCluster, -1, contiguous: false), string.Empty, files);
        return files;
    }

    public bool IsAllocated(long cluster)
    {
        var b = ReadAt(_bitmapPosition + ((cluster - 2) / 8), 1)[0];
        return (b & (1 << (int)((cluster - 2) % 8))) != 0;
    }

    private void ReadDirectory(byte[] data, string prefix, Dictionary<string, byte[]> files)
    {
        for (var i = 0; i + 32 <= data.Length && data[i] != 0;)
        {
            if (data[i] != 0x85)
            {
                i += 32;
                continue;
            }

            var secondaries = data[i + 1];
            var isDirectory = (data[i + 4] & 0x10) != 0;
            var stream = i + 32;
            var contiguous = (data[stream + 1] & 2) != 0;
            int nameLength = data[stream + 3];
            var first = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(stream + 20));
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(stream + 24));
            var name = new StringBuilder();
            for (var j = 2; j <= secondaries; j++)
            {
                name.Append(Encoding.Unicode.GetString(data, i + (j * 32) + 2, 30));
            }

            var path = prefix + name.ToString(0, nameLength);
            var content = size == 0 ? [] : ReadChain(first, (size + ClusterSize - 1) / ClusterSize, contiguous);
            if (isDirectory)
            {
                ReadDirectory(content, path + "/", files);
            }
            else
            {
                files[path] = content[..(int)size];
            }

            i += (secondaries + 1) * 32;
        }
    }

    private byte[] ReadChain(uint first, long count, bool contiguous)
    {
        var result = new MemoryStream();
        var cluster = first;
        for (long i = 0; count < 0 || i < count; i++)
        {
            Assert.InRange(cluster, 2u, (uint)(ClusterCount + 1));
            Assert.True(IsAllocated(cluster), $"cluster {cluster} is in use but free in the bitmap");
            result.Write(ReadAt(ClusterPosition(cluster), ClusterSize));
            if (contiguous)
            {
                cluster++;
                continue;
            }

            cluster = BinaryPrimitives.ReadUInt32LittleEndian(ReadAt(_fatOffset + (cluster * 4L), 4));
            if (cluster == EndOfChain)
            {
                Assert.True(count < 0 || i == count - 1, "chain shorter than the file");
                break;
            }
        }

        return result.ToArray();
    }

    private uint[] Allocate(int count, bool fragmented)
    {
        var clusters = new List<uint>();
        var bitmap = ReadAt(_bitmapPosition, (int)BitmapBytes);
        if (!fragmented)
        {
            // First fit of a whole run.
            for (long start = 0, run = 0, bit = 0; bit < ClusterCount && clusters.Count == 0; bit++)
            {
                run = (bitmap[bit / 8] & (1 << (int)(bit % 8))) == 0 ? run + 1 : 0;
                start = bit - run + 1;
                if (run == count)
                {
                    clusters.AddRange(Enumerable.Range(0, count).Select(i => (uint)(start + i + 2)));
                }
            }
        }
        else
        {
            var skip = false;
            for (long bit = 0; bit < ClusterCount && clusters.Count < count; bit++)
            {
                if ((bitmap[bit / 8] & (1 << (int)(bit % 8))) == 0)
                {
                    if (!skip)
                    {
                        clusters.Add((uint)(bit + 2));
                    }

                    skip = !skip;
                }
            }
        }

        Assert.Equal(count, clusters.Count);
        foreach (var c in clusters)
        {
            bitmap[(c - 2) / 8] |= (byte)(1 << (int)((c - 2) % 8));
        }

        WriteAt(_bitmapPosition, bitmap);
        return clusters.ToArray();
    }

    private void AddEntrySet(uint directory, string name, bool isDirectory, uint first, long size, bool contiguous)
    {
        var nameEntries = (name.Length + 14) / 15;
        var set = new byte[(2 + nameEntries) * 32];
        set[0] = 0x85;
        set[1] = (byte)(1 + nameEntries);
        set[4] = (byte)(isDirectory ? 0x10 : 0x20);

        var stream = set.AsSpan(32, 32);
        stream[0] = 0xC0;
        stream[1] = (byte)(contiguous ? 3 : 1);
        stream[3] = (byte)name.Length;
        ushort hash = 0;
        foreach (var b in Encoding.Unicode.GetBytes(name.ToUpperInvariant()))
        {
            hash = (ushort)(((hash & 1) != 0 ? 0x8000 : 0) + (hash >> 1) + b);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(stream[4..], hash);
        BinaryPrimitives.WriteUInt64LittleEndian(stream[8..], (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(stream[20..], first);
        BinaryPrimitives.WriteUInt64LittleEndian(stream[24..], (ulong)size);

        var nameBytes = Encoding.Unicode.GetBytes(name);
        for (var j = 0; j < nameEntries; j++)
        {
            var entry = set.AsSpan((2 + j) * 32, 32);
            entry[0] = 0xC1;
            nameBytes.AsSpan(j * 30, Math.Min(30, nameBytes.Length - (j * 30))).CopyTo(entry[2..]);
        }

        ushort checksum = 0;
        for (var i = 0; i < set.Length; i++)
        {
            if (i is 2 or 3)
            {
                continue;
            }

            checksum = (ushort)(((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(2), checksum);

        // Test directories are one cluster long.
        var position = ClusterPosition(directory);
        var data = ReadAt(position, ClusterSize);
        var free = 0;
        while (data[free] != 0)
        {
            free += 32;
        }

        Assert.True(free + set.Length <= data.Length, "test directory is full");
        WriteAt(position + free, set);
    }

    private void WriteFat(uint cluster, uint value)
    {
        var raw = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, value);
        WriteAt(_fatOffset + (cluster * 4L), raw);
    }

    private long ClusterPosition(long cluster) => _heapOffset + ((cluster - 2) * ClusterSize);

    private byte[] ReadAt(long position, int count)
    {
        var data = new byte[count];
        _file.Position = _base + position;
        _file.ReadExactly(data);
        return data;
    }

    private void WriteAt(long position, ReadOnlySpan<byte> data)
    {
        _file.Position = _base + position;
        _file.Write(data);
    }
}
