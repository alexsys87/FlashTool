using System.Buffers.Binary;
using FlashTool.Core.Containers;
using FlashTool.Core.Sources;
using ICSharpCode.SharpZipLib.BZip2;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace FlashTool.Core.Tests;

/// <summary>.zst and .bz2 files as other tools write them (zstd, pzstd, pbzip2), not just our own output.</summary>
public class ZstdBzip2Tests
{
    [Theory]
    [InlineData("core-image.wic.zst", ContainerFormat.Zstd, "core-image.wic")]
    [InlineData("core-image.wic.bz2", ContainerFormat.Bzip2, "core-image.wic")]
    [InlineData("rootfs.zst", ContainerFormat.Zstd, "rootfs.img")]
    public void Detected_by_content_and_extension(string fileName, ContainerFormat format, string innerName)
    {
        using var dir = new TempDir();
        var data = Pattern(100_000);
        File.WriteAllBytes(dir.File(fileName), format == ContainerFormat.Zstd ? ZstdFrame(data, pledge: true) : Bzip2(data));

        Assert.Equal(format, ContainerRegistry.Default.FromFileName(fileName).Format);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File(fileName)));
        Assert.Equal(format, container.Format);
        Assert.Equal(innerName, container.Entries.Single().Name);
        Assert.Equal(data, ReadAll(container));
    }

    [Fact]
    public void Zstd_size_is_the_sum_of_all_frames()
    {
        using var dir = new TempDir();
        var first = Pattern(300_000, seed: 1);
        var second = Pattern(123_457, seed: 2);
        File.WriteAllBytes(dir.File("multi.img.zst"), [.. ZstdFrame(first, pledge: true), .. ZstdFrame(second, pledge: true)]);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("multi.img.zst")));
        Assert.Equal(first.Length + second.Length, container.Entries[0].Length);
        Assert.Equal([.. first, .. second], ReadAll(container));
    }

    [Fact]
    public void Zstd_written_by_pzstd_with_skippable_frames_is_read()
    {
        using var dir = new TempDir();
        var first = Pattern(200_000, seed: 3);
        var second = Pattern(50_000, seed: 4);
        var frame1 = ZstdFrame(first, pledge: true);
        var frame2 = ZstdFrame(second, pledge: true);
        File.WriteAllBytes(dir.File("p.img.zst"), [.. Skippable(frame1.Length), .. frame1, .. Skippable(frame2.Length), .. frame2]);

        Assert.True(new ZstdContainerHandler().IsMatch(File.ReadAllBytes(dir.File("p.img.zst")).AsSpan(0, 64)));
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("p.img.zst")));
        Assert.Equal(ContainerFormat.Zstd, container.Format);
        Assert.Equal(first.Length + second.Length, container.Entries[0].Length);
        Assert.Equal([.. first, .. second], ReadAll(container));
    }

    [Fact]
    public void Zstd_without_declared_size_has_unknown_length()
    {
        using var dir = new TempDir();
        var data = Pattern(500_000);
        File.WriteAllBytes(dir.File("streamed.img.zst"), ZstdFrame(data, pledge: false));

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("streamed.img.zst")));
        Assert.Null(container.Entries[0].Length);
        Assert.Equal(data, ReadAll(container));
    }

    [Fact]
    public void Zstd_created_by_handler_declares_its_size()
    {
        using var dir = new TempDir();
        var data = Pattern(3_000_000);
        var handler = new ZstdContainerHandler();
        using (var sink = handler.Create(File.Create(dir.File("out.img.zst")), "out.img", data.Length, new CompressionSettings(CompressionPreset.Fast, Threads: 4)))
        {
            sink.Write(data);
        }

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("out.img.zst")));
        Assert.Equal(data.Length, container.Entries[0].Length);
        Assert.Equal(data, ReadAll(container));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Zstd_cut_short_while_creating_closes_quietly_and_is_not_a_valid_image(int threads)
    {
        using var dir = new TempDir();
        var path = dir.File("partial.img.zst");
        var data = Pattern(1_000_000);
        var handler = new ZstdContainerHandler();
        var sink = handler.Create(File.Create(path), "partial.img", data.Length * 2L, new CompressionSettings(Threads: threads));
        sink.Write(data);

        // Must not throw: the caller is already failing and its error must surface.
        sink.Dispose();

        // Either nothing was flushed yet, or the frame is left unterminated.
        if (ContainerRegistry.Default.Detect(File.ReadAllBytes(path)).Format == ContainerFormat.Zstd)
        {
            using var container = ContainerRegistry.Default.Open(new FileStreamSource(path));
            Assert.ThrowsAny<Exception>(() => ReadAll(container));
        }

        // The file handle is released, so the caller can delete the partial file.
        File.Delete(path);
    }

    [Fact]
    public void Truncated_zstd_is_an_error()
    {
        using var dir = new TempDir();
        var compressed = ZstdFrame(Pattern(400_000), pledge: true);
        File.WriteAllBytes(dir.File("cut.img.zst"), compressed[..(compressed.Length / 2)]);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("cut.img.zst")));
        Assert.Null(container.Entries[0].Length);
        Assert.ThrowsAny<Exception>(() => ReadAll(container));
    }

    [Fact]
    public void Bzip2_concatenated_streams_from_pbzip2_are_all_read()
    {
        using var dir = new TempDir();
        var parts = Enumerable.Range(0, 5).Select(i => Pattern(250_000 + i, seed: i)).ToList();

        // pbzip2 writes one complete stream per block; trailing zeros are ignored like bzip2 does.
        File.WriteAllBytes(dir.File("p.img.bz2"), [.. parts.SelectMany(Bzip2), .. new byte[64]]);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("p.img.bz2")));
        Assert.Equal(ContainerFormat.Bzip2, container.Format);
        Assert.Null(container.Entries[0].Length);
        Assert.Equal(parts.SelectMany(p => p).ToArray(), ReadAll(container));
    }

    [Theory]
    [InlineData(1, 3_000_000)]
    [InlineData(4, 3_000_000)]
    [InlineData(4, 0)]
    public void Bzip2_created_by_handler_reads_back(int threads, int length)
    {
        using var dir = new TempDir();
        var data = Pattern(length);
        var handler = new Bzip2ContainerHandler();
        using (var sink = handler.Create(File.Create(dir.File("out.img.bz2")), "out.img", data.Length, new CompressionSettings(Threads: threads)))
        {
            // Odd-sized writes cross chunk boundaries.
            for (var offset = 0; offset < data.Length; offset += 77_777)
            {
                sink.Write(data, offset, Math.Min(77_777, data.Length - offset));
            }
        }

        var compressed = File.ReadAllBytes(dir.File("out.img.bz2"));
        Assert.True(handler.IsMatch(compressed));
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("out.img.bz2")));
        Assert.Equal(ContainerFormat.Bzip2, container.Format);
        Assert.Equal(data, ReadAll(container));
    }

    [Fact]
    public void Truncated_bzip2_is_invalid_data()
    {
        using var dir = new TempDir();
        var compressed = Bzip2(Pattern(400_000));
        File.WriteAllBytes(dir.File("cut.img.bz2"), compressed[..(compressed.Length / 2)]);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("cut.img.bz2")));
        Assert.Throws<InvalidDataException>(() => ReadAll(container));
    }

    [Theory]
    [InlineData(ContainerFormat.Zstd)]
    [InlineData(ContainerFormat.Bzip2)]
    public void Read_from_a_source_that_cannot_seek(ContainerFormat format)
    {
        using var dir = new TempDir();
        var data = Pattern(700_000);
        var path = dir.File("image");
        File.WriteAllBytes(path, format == ContainerFormat.Zstd ? ZstdFrame(data, pledge: true) : Bzip2(data));

        using var container = ContainerRegistry.Default.Open(new SequentialSource(path));
        Assert.Equal(format, container.Format);
        Assert.Null(container.Entries[0].Length);
        Assert.Equal(data, ReadAll(container));
    }

    private static byte[] ReadAll(IImageContainer container)
    {
        using var reader = container.OpenEntry(container.Entries[0]);
        using var output = new MemoryStream();
        reader.Data.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Compressible but not trivial data, like a real file system.</summary>
    private static byte[] Pattern(int length, int seed = 0)
    {
        var data = new byte[length];
        var random = new Random(seed);
        for (var i = 0; i < length; i += 4096)
        {
            var block = data.AsSpan(i, Math.Min(4096, length - i));
            if (random.Next(3) == 0)
            {
                random.NextBytes(block);
            }
            else
            {
                block.Fill((byte)random.Next(256));
            }
        }

        return data;
    }

    private static byte[] ZstdFrame(byte[] data, bool pledge)
    {
        using var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, 3, leaveOpen: true))
        {
            zstd.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            if (pledge)
            {
                zstd.SetPledgedSrcSize((ulong)data.Length);
            }

            zstd.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>pzstd's skippable frame holding the size of the frame that follows.</summary>
    private static byte[] Skippable(int nextFrameSize)
    {
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0x184D2A50);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), (uint)nextFrameSize);
        return frame;
    }

    private static byte[] Bzip2(byte[] data)
    {
        using var output = new MemoryStream();
        using (var bzip2 = new BZip2OutputStream(output, 9) { IsStreamOwner = false })
        {
            bzip2.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Like an HTTP server without Range support.</summary>
    private sealed class SequentialSource(string path) : IStreamSource
    {
        public string Name => Path.GetFileName(path);

        public string Location => path;

        public long? Length => new FileInfo(path).Length;

        public bool IsSeekable => false;

        public bool IsRemote => true;

        public Stream Open(CancellationToken cancellationToken = default) => new ForwardOnly(File.OpenRead(path));
    }

    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
