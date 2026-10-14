using FlashTool.Core.Containers;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Tests;

/// <summary>
/// Every operation must close the device when it ends, however it ends, so the next
/// operation (on another page) can open the same disk again.
/// </summary>
public class DeviceReleaseTests
{
    private const int Chunk = 64 * 1024;

    private static readonly IoRetryPolicy Fast = new() { AutomaticAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(1) };

    [Fact]
    public async Task Write_verify_read_and_write_again_on_the_same_disk()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000);
        using var container = Open(dir, image);
        var disk = new ExclusiveDisk(dir.File("disk.bin"), image.Length + (4L * 1024 * 1024));

        await Write(container, disk, new WriteImageOptions { ChunkSize = Chunk, Verify = true, ExpandLastPartition = true });
        disk.AssertReleased();

        var verify = await VerifyImageOperation.RunAsync(container, container.Entries[0], () => disk.Open(write: false), new VerifyImageOptions { ChunkSize = Chunk }, null, CancellationToken.None);
        Assert.True(verify.BytesCompared > 0);
        disk.AssertReleased();

        await ReadImageOperation.RunAsync(() => disk.Open(write: false), dir.File("backup.img.xz"), ContainerRegistry.Default, new ReadImageOptions { ChunkSize = Chunk, Verify = true }, null, CancellationToken.None);
        disk.AssertReleased();

        await Write(container, disk, new WriteImageOptions { ChunkSize = Chunk });
        disk.AssertReleased();
        Assert.Equal(4, disk.Opens);
    }

    public static TheoryData<string> WriteFailures => new() { "io-error-abort", "verification", "cancel", "too-large", "not-an-image" };

    [Theory]
    [MemberData(nameof(WriteFailures))]
    public async Task Failed_write_releases_the_disk(string failure)
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000);
        using var container = Open(dir, image);
        var disk = new ExclusiveDisk(dir.File("disk.bin"), failure == "too-large" ? image.Length / 2 : image.Length * 2L);
        using var cts = new CancellationTokenSource();
        var options = new WriteImageOptions { ChunkSize = Chunk, IoRetry = Fast with { AskUser = (_, _) => Task.FromResult(false) } };
        var entryContainer = container;

        switch (failure)
        {
            case "io-error-abort":
                disk.Decorate = d => new FaultDevice(d) { FailWritesAt = 3 * Chunk };
                break;
            case "verification":
                disk.Decorate = d => new FaultDevice(d) { CorruptReadsAt = 5 * Chunk };
                break;
            case "cancel":
                disk.Decorate = d => new FaultDevice(d) { OnWrite = offset => { if (offset >= 4 * Chunk) { cts.Cancel(); } } };
                break;
            case "not-an-image":
                File.WriteAllText(dir.File("notes.txt"), string.Concat(Enumerable.Repeat("not an image\n", 50_000)));
                entryContainer = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("notes.txt")));
                break;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => WriteImageOperation.RunAsync(entryContainer, entryContainer.Entries[0], () => disk.Open(write: true), options, null, cts.Token));
        disk.AssertReleased();

        // The disk is free: the next write succeeds.
        disk.Decorate = null;
        if (failure == "too-large")
        {
            return;
        }

        await Write(container, disk, new WriteImageOptions { ChunkSize = Chunk });
        disk.AssertReleased();
        if (entryContainer != container)
        {
            entryContainer.Dispose();
        }
    }

    [Fact]
    public async Task Cancelled_verify_and_failed_read_release_the_disk()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000);
        using var container = Open(dir, image);
        var disk = new ExclusiveDisk(dir.File("disk.bin"), image.Length);
        await Write(container, disk, new WriteImageOptions { ChunkSize = Chunk });

        using (var cts = new CancellationTokenSource())
        {
            disk.Decorate = d => new FaultDevice(d) { OnRead = offset => { if (offset >= 3 * Chunk) { cts.Cancel(); } } };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifyImageOperation.RunAsync(
                container, container.Entries[0], () => disk.Open(write: false), new VerifyImageOptions { ChunkSize = Chunk }, null, cts.Token));
            disk.AssertReleased();
        }

        // Read error the user gives up on.
        disk.Decorate = d => new FaultDevice(d) { FailReadsAt = 2 * Chunk };
        await Assert.ThrowsAnyAsync<Exception>(() => ReadImageOperation.RunAsync(
            () => disk.Open(write: false),
            dir.File("backup.img.zip"),
            ContainerRegistry.Default,
            new ReadImageOptions { ChunkSize = Chunk, IoRetry = Fast with { AskUser = (_, _) => Task.FromResult(false) } },
            null,
            CancellationToken.None));
        disk.AssertReleased();
        Assert.False(File.Exists(dir.File("backup.img.zip")));

        // Destination that cannot be created.
        disk.Decorate = null;
        await Assert.ThrowsAnyAsync<Exception>(() => ReadImageOperation.RunAsync(
            () => disk.Open(write: false),
            dir.File(Path.Combine("missing-folder", "backup.img")),
            ContainerRegistry.Default,
            new ReadImageOptions { ChunkSize = Chunk },
            null,
            CancellationToken.None));
        disk.AssertReleased();

        await Write(container, disk, new WriteImageOptions { ChunkSize = Chunk });
        disk.AssertReleased();
    }

    private static Task<WriteImageResult> Write(IImageContainer container, ExclusiveDisk disk, WriteImageOptions options)
        => WriteImageOperation.RunAsync(container, container.Entries[0], () => disk.Open(write: true), options, null, CancellationToken.None);

    private static IImageContainer Open(TempDir dir, byte[] image)
    {
        File.WriteAllBytes(dir.File("image.img"), image);
        return ContainerRegistry.Default.Open(new FileStreamSource(dir.File("image.img")));
    }

    /// <summary>
    /// A disk with the sharing rules of a real one opened by FlashTool: one writer
    /// (volumes locked) or any number of readers. Records any use of a closed handle.
    /// </summary>
    private sealed class ExclusiveDisk
    {
        private readonly object _sync = new();
        private readonly string _path;
        private int _readers;
        private bool _writer;
        private int _usedAfterClose;

        public ExclusiveDisk(string path, long size)
        {
            _path = path;
            using var file = File.Create(path);
            file.SetLength(size);
        }

        public Func<IBlockDevice, IBlockDevice>? Decorate { get; set; }

        public int Opens { get; private set; }

        public IBlockDevice Open(bool write)
        {
            lock (_sync)
            {
                if (_writer || (write && _readers > 0))
                {
                    throw new FlashToolException(FlashErrorCode.DeviceLockFailed, "The disk is still open.");
                }

                if (write)
                {
                    _writer = true;
                }
                else
                {
                    _readers++;
                }

                Opens++;
            }

            IBlockDevice device = new Handle(this, HandleBlockDevice.OpenFile(_path, writable: write), write);
            return Decorate?.Invoke(device) ?? device;
        }

        public void AssertReleased()
        {
            lock (_sync)
            {
                Assert.False(_writer, "The disk is still open for writing.");
                Assert.Equal(0, _readers);
            }

            Assert.Equal(0, Volatile.Read(ref _usedAfterClose));
        }

        private void Close(bool write)
        {
            lock (_sync)
            {
                if (write)
                {
                    _writer = false;
                }
                else
                {
                    _readers--;
                }
            }
        }

        private sealed class Handle(ExclusiveDisk disk, IBlockDevice inner, bool write) : IBlockDevice
        {
            private int _closed;

            public long Length => inner.Length;

            public int SectorSize => inner.SectorSize;

            public bool CanWrite => inner.CanWrite;

            public void Read(long offset, Span<byte> buffer)
            {
                CheckOpen();
                inner.Read(offset, buffer);
            }

            public void Write(long offset, ReadOnlySpan<byte> buffer)
            {
                CheckOpen();
                inner.Write(offset, buffer);
            }

            public void Flush()
            {
                CheckOpen();
                inner.Flush();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    inner.Dispose();
                    disk.Close(write);
                }
            }

            private void CheckOpen()
            {
                if (Volatile.Read(ref _closed) != 0)
                {
                    Interlocked.Increment(ref disk._usedAfterClose);
                    throw new ObjectDisposedException(nameof(Handle));
                }
            }
        }
    }

    private sealed class FaultDevice(IBlockDevice inner) : IBlockDevice
    {
        public long FailWritesAt { get; init; } = -1;

        public long FailReadsAt { get; init; } = -1;

        public long CorruptReadsAt { get; init; } = -1;

        public Action<long>? OnWrite { get; init; }

        public Action<long>? OnRead { get; init; }

        public long Length => inner.Length;

        public int SectorSize => inner.SectorSize;

        public bool CanWrite => inner.CanWrite;

        public void Read(long offset, Span<byte> buffer)
        {
            OnRead?.Invoke(offset);
            if (Hits(FailReadsAt, offset, buffer.Length))
            {
                throw new IOException("Simulated bad sector.");
            }

            inner.Read(offset, buffer);
            if (Hits(CorruptReadsAt, offset, buffer.Length))
            {
                buffer[(int)(CorruptReadsAt - offset)] ^= 0xFF;
            }
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            OnWrite?.Invoke(offset);
            if (Hits(FailWritesAt, offset, buffer.Length))
            {
                throw new IOException("Simulated bad sector.");
            }

            inner.Write(offset, buffer);
        }

        public void Flush() => inner.Flush();

        public void Dispose() => inner.Dispose();

        private static bool Hits(long target, long offset, int length) => target >= offset && target < offset + length;
    }
}
