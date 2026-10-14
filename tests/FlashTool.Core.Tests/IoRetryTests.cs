using FlashTool.Core.Containers;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Tests;

public class IoRetryTests
{
    private static readonly IoRetryPolicy Fast = new() { AutomaticAttempts = 3, InitialDelay = TimeSpan.FromMilliseconds(1) };

    [Fact]
    public async Task Transient_write_errors_are_retried_at_the_same_offset()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 8000);
        using var container = Open(dir, image);
        FlakyDevice? flaky = null;

        await WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => flaky = new FlakyDevice(HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length), failWritesAt: 3 * 64 * 1024, failures: 2),
            new WriteImageOptions { ChunkSize = 64 * 1024, IoRetry = Fast },
            null,
            CancellationToken.None);

        Assert.Equal(2, flaky!.FailuresInjected);
        Assert.Equal(image, File.ReadAllBytes(dir.File("target.bin")));
    }

    [Fact]
    public async Task User_is_asked_after_automatic_retries_and_can_retry()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 8000);
        using var container = Open(dir, image);
        var questions = new List<IoErrorContext>();

        var policy = Fast with
        {
            AskUser = (context, _) =>
            {
                questions.Add(context);
                return Task.FromResult(true);
            },
        };

        await WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => new FlakyDevice(HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length), failWritesAt: 2 * 64 * 1024, failures: 4),
            new WriteImageOptions { ChunkSize = 64 * 1024, IoRetry = policy },
            null,
            CancellationToken.None);

        // 3 automatic attempts fail, the user says "retry", attempt 4 fails, attempt 5 succeeds.
        var question = Assert.Single(questions);
        Assert.Equal(IoOperationKind.Write, question.Kind);
        Assert.Equal(2 * 64 * 1024, question.Offset);
        Assert.Equal(3, question.Attempts);
        Assert.Equal(image, File.ReadAllBytes(dir.File("target.bin")));
    }

    [Fact]
    public async Task User_can_cancel_after_a_persistent_error()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 8000);
        using var container = Open(dir, image);
        var policy = Fast with { AskUser = (_, _) => Task.FromResult(false) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => new FlakyDevice(HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length), failWritesAt: 64 * 1024, failures: int.MaxValue),
            new WriteImageOptions { ChunkSize = 64 * 1024, IoRetry = policy },
            null,
            CancellationToken.None));
    }

    [Fact]
    public async Task Read_errors_while_creating_an_image_are_retried()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 8000);
        File.WriteAllBytes(dir.File("source.bin"), image);

        var result = await ReadImageOperation.RunAsync(
            () => new FlakyDevice(HandleBlockDevice.OpenFile(dir.File("source.bin"), writable: false), failReadsAt: 5 * 64 * 1024, failures: 2),
            dir.File("out.img"),
            ContainerRegistry.Default,
            new ReadImageOptions { ChunkSize = 64 * 1024, IoRetry = Fast },
            null,
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(image, File.ReadAllBytes(dir.File("out.img")));
    }

    [Fact]
    public void Logic_errors_are_not_retried()
    {
        Assert.False(IoRetryPolicy.IsRetryable(new FlashToolException(FlashErrorCode.ImageTooLarge)));
        Assert.False(IoRetryPolicy.IsRetryable(new ArgumentException()));
        Assert.False(IoRetryPolicy.IsRetryable(new FileNotFoundException()));
        Assert.True(IoRetryPolicy.IsRetryable(new IOException("CRC error")));
        Assert.True(IoRetryPolicy.IsRetryable(new TimeoutException()));
    }

    private static IImageContainer Open(TempDir dir, byte[] image)
    {
        File.WriteAllBytes(dir.File("image.img"), image);
        return ContainerRegistry.Default.Open(new FileStreamSource(dir.File("image.img")));
    }

    /// <summary>Fails a number of reads or writes that touch a given offset, like a weak sector.</summary>
    private sealed class FlakyDevice(IBlockDevice inner, long failWritesAt = -1, long failReadsAt = -1, int failures = 1) : IBlockDevice
    {
        private int _remaining = failures;

        public int FailuresInjected { get; private set; }

        public long Length => inner.Length;

        public int SectorSize => inner.SectorSize;

        public bool CanWrite => inner.CanWrite;

        public void Read(long offset, Span<byte> buffer)
        {
            MaybeFail(failReadsAt, offset, buffer.Length);
            inner.Read(offset, buffer);
        }

        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            MaybeFail(failWritesAt, offset, buffer.Length);
            inner.Write(offset, buffer);
        }

        public void Flush() => inner.Flush();

        public void Dispose() => inner.Dispose();

        private void MaybeFail(long at, long offset, int length)
        {
            if (at >= offset && at < offset + length && _remaining > 0)
            {
                _remaining--;
                FailuresInjected++;
                throw new IOException("Data error (cyclic redundancy check).");
            }
        }
    }
}
