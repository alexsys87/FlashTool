using System.Security.Cryptography;
using FlashTool.Core.Checksums;
using FlashTool.Core.Containers;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Tests;

public class ImageChecksumFileTests
{
    [Theory]
    [InlineData("disk.img")]
    [InlineData("disk.img.gz")]
    [InlineData("disk.img.zip")]
    public async Task Checksum_file_holds_the_hash_of_the_created_file_in_sha256sum_format(string fileName)
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("source.bin"), TestImages.CreateMbrImage(secondPartitionSectors: 8000));

        var result = await CreateAsync(dir, fileName, new ReadImageOptions { WriteChecksumFile = true, ChunkSize = 64 * 1024 });

        var checksumFile = dir.File(fileName + ".sha256");
        var published = SHA256.HashData(File.ReadAllBytes(dir.File(fileName)));
        Assert.Equal(checksumFile, result.ChecksumFilePath);
        Assert.Equal(published, result.Sha256);
        Assert.Null(result.ChecksumFileError);
        Assert.Equal($"{Convert.ToHexString(published).ToLowerInvariant()}  {fileName}\n", File.ReadAllText(checksumFile));

        // No byte order mark: sha256sum -c would choke on it.
        Assert.True(Uri.IsHexDigit((char)File.ReadAllBytes(checksumFile)[0]));
    }

    [Fact]
    public async Task Without_the_option_no_checksum_file_is_written()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("source.bin"), TestImages.CreateMbrImage(secondPartitionSectors: 4000));

        var result = await CreateAsync(dir, "disk.img", new ReadImageOptions { ChunkSize = 64 * 1024 });

        Assert.Null(result.Sha256);
        Assert.Null(result.ChecksumFilePath);
        Assert.Null(result.ChecksumFileError);
        Assert.False(File.Exists(dir.File("disk.img.sha256")));
    }

    [Fact]
    public async Task A_checksum_file_left_over_from_an_earlier_image_is_removed_even_when_none_is_asked_for()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("source.bin"), TestImages.CreateMbrImage(secondPartitionSectors: 4000));
        File.WriteAllText(dir.File("disk.img.sha256"), "stale\n");

        await CreateAsync(dir, "disk.img", new ReadImageOptions { ChunkSize = 64 * 1024 });

        Assert.False(File.Exists(dir.File("disk.img.sha256")));
    }

    [Fact]
    public async Task A_left_over_checksum_file_is_replaced_when_one_is_asked_for()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("source.bin"), TestImages.CreateMbrImage(secondPartitionSectors: 4000));
        File.WriteAllText(dir.File("disk.img.sha256"), "stale\n");

        var result = await CreateAsync(dir, "disk.img", new ReadImageOptions { WriteChecksumFile = true, ChunkSize = 64 * 1024 });

        var text = File.ReadAllText(dir.File("disk.img.sha256"));
        Assert.Equal(ChecksumFile.Format(result.Sha256!, "disk.img"), text);
    }

    [Fact]
    public async Task Cancelling_before_the_operation_is_over_discards_the_image_and_leaves_no_checksum_file()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("source.bin"), TestImages.CreateMbrImage(secondPartitionSectors: 4000));
        File.WriteAllText(dir.File("disk.img"), "an earlier image");
        File.WriteAllText(dir.File("disk.img.sha256"), "stale\n");
        using var cts = new CancellationTokenSource();
        var progress = new Phases(phase =>
        {
            // The image is fully written at this point; the checksum step has not started yet.
            if (phase == OperationPhase.Finalizing)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateAsync(dir, "disk.img", new ReadImageOptions { Verify = false, WriteChecksumFile = true, ChunkSize = 64 * 1024 }, progress, cts.Token));

        Assert.False(File.Exists(dir.File("disk.img")));
        Assert.False(File.Exists(dir.File("disk.img.sha256")));
    }

    [Fact]
    public async Task A_checksum_file_that_cannot_be_written_does_not_fail_the_image()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("source.bin"), TestImages.CreateMbrImage(secondPartitionSectors: 4000));

        // A directory squatting on the name makes the file impossible to write.
        Directory.CreateDirectory(dir.File("disk.img.sha256"));

        var result = await CreateAsync(dir, "disk.img", new ReadImageOptions { WriteChecksumFile = true, ChunkSize = 64 * 1024 });

        Assert.True(File.Exists(dir.File("disk.img")));
        Assert.NotNull(result.Sha256);
        Assert.Null(result.ChecksumFilePath);
        Assert.False(string.IsNullOrEmpty(result.ChecksumFileError));
    }

    [Fact]
    public async Task Hashing_is_reported_as_a_phase_of_its_own_after_verification()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("source.bin"), TestImages.CreateMbrImage(secondPartitionSectors: 4000));
        var progress = new Phases();

        await CreateAsync(dir, "disk.img", new ReadImageOptions { WriteChecksumFile = true, ChunkSize = 64 * 1024 }, progress);

        var phases = progress.Seen;
        Assert.True(phases.Contains(OperationPhase.Hashing));
        Assert.True(phases.LastIndexOf(OperationPhase.Verifying) < phases.IndexOf(OperationPhase.Hashing));
        Assert.True(phases.IndexOf(OperationPhase.Hashing) < phases.IndexOf(OperationPhase.Completed));
    }

    [Fact]
    public async Task What_was_written_is_found_and_enforced_when_the_image_is_written_back()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 8000);
        File.WriteAllBytes(dir.File("source.bin"), image);
        await CreateAsync(dir, "disk.img.gz", new ReadImageOptions { WriteChecksumFile = true, ChunkSize = 64 * 1024 });

        var found = ChecksumDiscovery.FindForFile(dir.File("disk.img.gz"));
        Assert.NotNull(found);
        Assert.Equal("disk.img.gz.sha256", found.Origin);

        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("disk.img.gz")));
        var written = await WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length + 4096),
            new WriteImageOptions { Verify = true, ChunkSize = 64 * 1024, ExpectedSourceSha256 = found.Sha256 },
            null,
            CancellationToken.None);
        Assert.Equal(SourceChecksumStatus.Verified, written.SourceChecksum);

        // A damaged copy no longer matches the file that was written next to it.
        var damaged = File.ReadAllBytes(dir.File("disk.img.gz"));
        damaged[damaged.Length / 2] ^= 0xFF;
        File.WriteAllBytes(dir.File("copy.img.gz"), damaged);
        File.Copy(dir.File("disk.img.gz.sha256"), dir.File("copy.img.gz.sha256"));
        var copyFound = ChecksumDiscovery.FindForFile(dir.File("copy.img.gz"));
        Assert.NotNull(copyFound);
        using var copy = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("copy.img.gz")));
        var ex = await Assert.ThrowsAsync<FlashToolException>(() => WriteImageOperation.RunAsync(
            copy,
            copy.Entries[0],
            () => HandleBlockDevice.OpenFile(dir.File("other.bin"), image.Length + 4096),
            new WriteImageOptions { ChunkSize = 64 * 1024, ExpectedSourceSha256 = copyFound.Sha256 },
            null,
            CancellationToken.None));
        Assert.Equal(FlashErrorCode.SourceChecksumMismatch, ex.Code);
    }

    [Fact]
    public void The_line_follows_the_sha256sum_layout_and_can_be_read_back()
    {
        var hash = SHA256.HashData([1, 2, 3]);

        var line = ChecksumFile.Format(hash, "a b.img.xz");

        Assert.Equal($"{Convert.ToHexString(hash).ToLowerInvariant()}  a b.img.xz\n", line);
        Assert.True(ChecksumFile.TryFind(line, "a b.img.xz", acceptSingleEntry: false, out var parsed));
        Assert.Equal(hash, parsed);
        Assert.Equal("dir/x.img.sha256", ChecksumFile.SidecarPath("dir/x.img"));
    }

    private static Task<ReadImageResult> CreateAsync(
        TempDir dir,
        string destinationName,
        ReadImageOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(dir.File("source.bin"), writable: false),
            dir.File(destinationName),
            ContainerRegistry.Default,
            options,
            progress,
            cancellationToken);

    private sealed class Phases(Action<OperationPhase>? onPhase = null) : IProgress<OperationProgress>
    {
        private readonly List<OperationPhase> _seen = [];

        public List<OperationPhase> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public void Report(OperationProgress value)
        {
            lock (_seen)
            {
                _seen.Add(value.Phase);
            }

            onPhase?.Invoke(value.Phase);
        }
    }
}
