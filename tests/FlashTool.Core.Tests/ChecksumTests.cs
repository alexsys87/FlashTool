using System.IO.Compression;
using System.Security.Cryptography;
using FlashTool.Core.Checksums;
using FlashTool.Core.Containers;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Tests;

public class ChecksumTests
{
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    // ---- Sha256Hex ----

    [Theory]
    [InlineData(EmptyHash)]
    [InlineData("  E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855\n")]
    [InlineData("sha256:" + EmptyHash)]
    [InlineData("SHA256: " + EmptyHash)]
    public void Sha256Hex_parses_common_spellings(string text)
    {
        Assert.True(Sha256Hex.TryParse(text, out var hash));
        Assert.Equal(SHA256.HashData([]), hash);
        Assert.Equal(EmptyHash, Sha256Hex.Format(hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8555")]
    [InlineData("g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("e3b0c442 98fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void Sha256Hex_rejects_malformed_values(string text)
    {
        Assert.False(Sha256Hex.TryParse(text, out _));
    }

    [Fact]
    public void Sha256Hex_rejects_null()
    {
        Assert.False(Sha256Hex.TryParse(null, out _));
    }

    // ---- ChecksumFile ----

    [Fact]
    public void Checksum_file_understands_text_and_binary_mode_lines()
    {
        var content = $"{HashA}  image-a.img.xz\n{HashB} *image-b.img.xz\n";

        Assert.True(ChecksumFile.TryFind(content, "image-a.img.xz", acceptSingleEntry: false, out var a));
        Assert.Equal(HashA, Sha256Hex.Format(a));
        Assert.True(ChecksumFile.TryFind(content, "image-b.img.xz", acceptSingleEntry: false, out var b));
        Assert.Equal(HashB, Sha256Hex.Format(b));
        Assert.False(ChecksumFile.TryFind(content, "image-c.img.xz", acceptSingleEntry: false, out _));
    }

    [Fact]
    public void Checksum_file_matches_names_without_directory_and_ignoring_case()
    {
        var content = $"{HashA}  ./out/targets/Image-A.IMG.XZ\n{HashB}  C:\\dl\\other.img\n";

        Assert.True(ChecksumFile.TryFind(content, "image-a.img.xz", acceptSingleEntry: false, out var a));
        Assert.Equal(HashA, Sha256Hex.Format(a));
        Assert.True(ChecksumFile.TryFind(content, "other.img", acceptSingleEntry: false, out var b));
        Assert.Equal(HashB, Sha256Hex.Format(b));
    }

    [Fact]
    public void Checksum_file_understands_bsd_style_lines()
    {
        var content = $"SHA256 (image.img.xz) = {HashA}\nSHA256 (other.img) = {HashB}\n";

        Assert.True(ChecksumFile.TryFind(content, "other.img", acceptSingleEntry: false, out var hash));
        Assert.Equal(HashB, Sha256Hex.Format(hash));
    }

    [Fact]
    public void Checksum_file_with_only_a_hash_is_taken_for_a_sidecar_but_not_for_a_list()
    {
        var content = HashA + "\n";

        Assert.True(ChecksumFile.TryFind(content, "whatever.img", acceptSingleEntry: true, out var hash));
        Assert.Equal(HashA, Sha256Hex.Format(hash));
        Assert.False(ChecksumFile.TryFind(content, "whatever.img", acceptSingleEntry: false, out _));
    }

    [Fact]
    public void A_single_entry_for_another_file_counts_only_for_sidecars()
    {
        var content = $"{HashA}  other.img\n";

        Assert.True(ChecksumFile.TryFind(content, "mine.img", acceptSingleEntry: true, out _));
        Assert.False(ChecksumFile.TryFind(content, "mine.img", acceptSingleEntry: false, out _));
    }

    [Fact]
    public void Several_entries_without_a_matching_name_are_never_guessed()
    {
        var content = $"{HashA}  one.img\n{HashB}  two.img\n";

        Assert.False(ChecksumFile.TryFind(content, "mine.img", acceptSingleEntry: true, out _));
    }

    [Fact]
    public void Checksum_file_tolerates_comments_blank_lines_crlf_and_a_byte_order_mark()
    {
        var content = $"\uFEFF# generated\r\n\r\n{HashA}  x.img\r\n";

        Assert.True(ChecksumFile.TryFind(content, "x.img", acceptSingleEntry: false, out var hash));
        Assert.Equal(HashA, Sha256Hex.Format(hash));
    }

    // ---- HashingStreamSource ----

    [Fact]
    public void Hashing_source_knows_the_digest_after_a_full_sequential_read()
    {
        var data = RandomBytes(100_000);
        var source = new HashingStreamSource(new MemorySource(data));

        var stream = source.Open();
        var buffer = new byte[4096];
        while (stream.Read(buffer, 0, buffer.Length) > 0)
        {
        }

        Assert.True(source.TryGetSha256(out var whileOpen));
        Assert.Equal(SHA256.HashData(data), whileOpen);

        stream.Dispose();
        Assert.True(source.TryGetSha256(out var afterClose));
        Assert.Equal(SHA256.HashData(data), afterClose);
    }

    [Fact]
    public async Task Hashing_source_hashes_async_reads_too()
    {
        var data = RandomBytes(300_000);
        var source = new HashingStreamSource(new MemorySource(data));

        await using (var stream = source.Open())
        {
            await stream.CopyToAsync(Stream.Null);
        }

        Assert.True(source.TryGetSha256(out var digest));
        Assert.Equal(SHA256.HashData(data), digest);
    }

    [Fact]
    public void A_partial_read_gives_no_digest()
    {
        var data = RandomBytes(50_000);
        var source = new HashingStreamSource(new MemorySource(data));

        using var stream = source.Open();
        _ = stream.Read(new byte[25_000], 0, 25_000);

        Assert.False(source.TryGetSha256(out _));
    }

    [Fact]
    public void A_jump_into_the_middle_spoils_the_pass_but_a_return_to_the_start_begins_a_new_one()
    {
        var data = RandomBytes(50_000);
        var source = new HashingStreamSource(new MemorySource(data));

        using var stream = source.Open();
        _ = stream.Read(new byte[1000], 0, 1000);
        stream.Seek(500, SeekOrigin.Begin);
        stream.CopyTo(Stream.Null);
        Assert.False(source.TryGetSha256(out _));

        stream.Seek(0, SeekOrigin.Begin);
        stream.CopyTo(Stream.Null);
        Assert.True(source.TryGetSha256(out var digest));
        Assert.Equal(SHA256.HashData(data), digest);
    }

    [Fact]
    public void A_completed_pass_survives_a_later_rewind()
    {
        var data = RandomBytes(20_000);
        var source = new HashingStreamSource(new MemorySource(data));

        using var stream = source.Open();
        stream.CopyTo(Stream.Null);
        stream.Position = 0;
        _ = stream.Read(new byte[10], 0, 10);

        Assert.True(source.TryGetSha256(out var digest));
        Assert.Equal(SHA256.HashData(data), digest);
    }

    [Fact]
    public void Streams_that_only_probe_the_head_do_not_count()
    {
        var data = RandomBytes(40_000);
        var source = new HashingStreamSource(new MemorySource(data));

        using (var probe = source.Open())
        {
            _ = probe.Read(new byte[512], 0, 512);
        }

        Assert.False(source.TryGetSha256(out _));

        using (var full = source.Open())
        {
            full.CopyTo(Stream.Null);
        }

        Assert.True(source.TryGetSha256(out var digest));
        Assert.Equal(SHA256.HashData(data), digest);
    }

    [Fact]
    public void Without_a_known_length_the_end_of_the_stream_decides()
    {
        var data = RandomBytes(10_000);
        var source = new HashingStreamSource(new MemorySource(data, knownLength: false));

        using var stream = source.Open();
        var buffer = new byte[data.Length];
        Assert.Equal(data.Length, stream.Read(buffer, 0, buffer.Length));
        Assert.False(source.TryGetSha256(out _));

        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
        Assert.True(source.TryGetSha256(out var digest));
        Assert.Equal(SHA256.HashData(data), digest);
    }

    // ---- WriteImageOperation ----

    [Fact]
    public async Task Local_file_with_the_right_checksum_is_verified_before_anything_is_written()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 4000);
        File.WriteAllBytes(dir.File("disk.img"), image);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("disk.img")));
        var progress = new CollectedProgress();

        var result = await WriteAsync(container, dir.File("target.bin"), image.Length + (1024 * 1024), SHA256.HashData(image), progress);

        Assert.Equal(SourceChecksumStatus.Verified, result.SourceChecksum);
        Assert.Equal(image, File.ReadAllBytes(dir.File("target.bin")).AsSpan(0, image.Length).ToArray());

        var phases = progress.Phases();
        var lastCheck = phases.LastIndexOf(OperationPhase.CheckingSource);
        Assert.True(lastCheck >= 0);
        Assert.True(phases.IndexOf(OperationPhase.Writing) > lastCheck);
    }

    [Fact]
    public async Task Local_file_with_the_wrong_checksum_is_rejected_without_opening_the_device()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 4000);
        File.WriteAllBytes(dir.File("disk.img"), image);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("disk.img")));
        var wrong = SHA256.HashData([1, 2, 3]);

        var ex = await Assert.ThrowsAsync<FlashToolException>(() => WriteAsync(container, dir.File("target.bin"), image.Length + 4096, wrong));

        Assert.Equal(FlashErrorCode.SourceChecksumMismatch, ex.Code);
        Assert.Equal(Sha256Hex.Format(wrong), ex.Arguments[0]);
        Assert.Equal(Sha256Hex.Format(SHA256.HashData(image)), ex.Arguments[1]);
        Assert.False(File.Exists(dir.File("target.bin")));
    }

    [Fact]
    public async Task The_checksum_is_the_one_of_the_file_as_published_not_of_the_unpacked_image()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 4000);
        var gz = Gzip(image);
        File.WriteAllBytes(dir.File("disk.img.gz"), gz);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("disk.img.gz")));

        var result = await WriteAsync(container, dir.File("target.bin"), image.Length + 4096, SHA256.HashData(gz));
        Assert.Equal(SourceChecksumStatus.Verified, result.SourceChecksum);

        var ex = await Assert.ThrowsAsync<FlashToolException>(() => WriteAsync(container, dir.File("other.bin"), image.Length + 4096, SHA256.HashData(image)));
        Assert.Equal(FlashErrorCode.SourceChecksumMismatch, ex.Code);
    }

    [Fact]
    public async Task Without_an_expected_value_nothing_is_checked()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 4000);
        File.WriteAllBytes(dir.File("disk.img"), image);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("disk.img")));

        var result = await WriteAsync(container, dir.File("target.bin"), image.Length + 4096, expected: null);

        Assert.Equal(SourceChecksumStatus.NotChecked, result.SourceChecksum);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Download_with_the_right_checksum_is_verified_while_it_streams(bool supportRanges, bool dropFirstConnection)
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000);
        var gz = Gzip(image);
        using var server = new HttpSourceTests.TestHttpServer(gz, supportRanges, dropFirstConnection);
        using var http = new HttpClient();
        var source = new HashingStreamSource(await HttpStreamSource.CreateAsync(http, new Uri(server.Url + "disk.img.gz"), CancellationToken.None));
        using var container = ContainerRegistry.Default.Open(source);

        var result = await WriteAsync(container, dir.File("target.bin"), image.Length + 4096, SHA256.HashData(gz));

        Assert.Equal(SourceChecksumStatus.Verified, result.SourceChecksum);
        Assert.Equal(image, File.ReadAllBytes(dir.File("target.bin")).AsSpan(0, image.Length).ToArray());
    }

    [Fact]
    public async Task A_damaged_download_leaves_the_partition_table_unwritten()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000);
        var gz = Gzip(image);
        using var server = new HttpSourceTests.TestHttpServer(gz, supportRanges: true, dropFirstConnection: false);
        using var http = new HttpClient();
        var source = new HashingStreamSource(await HttpStreamSource.CreateAsync(http, new Uri(server.Url + "disk.img.gz"), CancellationToken.None));
        using var container = ContainerRegistry.Default.Open(source);
        var wrong = SHA256.HashData([9, 9, 9]);

        var ex = await Assert.ThrowsAsync<FlashToolException>(() => WriteAsync(container, dir.File("target.bin"), image.Length + 4096, wrong));

        Assert.Equal(FlashErrorCode.SourceChecksumMismatchAfterWrite, ex.Code);
        Assert.Equal(Sha256Hex.Format(wrong), ex.Arguments[0]);
        Assert.Equal(Sha256Hex.Format(SHA256.HashData(gz)), ex.Arguments[1]);

        // The data after the first chunk went out (the image is random from 1 MiB on), but the chunk
        // with the partition table did not.
        var target = File.ReadAllBytes(dir.File("target.bin"));
        const int Chunk = 64 * 1024;
        const int Tail = 2 * 1024 * 1024;
        Assert.True(target.AsSpan(0, Chunk).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(image.AsSpan(Tail, 4096).ToArray(), target.AsSpan(Tail, 4096).ToArray());
    }

    [Fact]
    public async Task A_zip_read_by_url_cannot_be_verified_but_is_still_written()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 4000);
        var zip = Zip("disk.img", image);
        using var server = new HttpSourceTests.TestHttpServer(zip, supportRanges: true, dropFirstConnection: false);
        using var http = new HttpClient();
        var source = new HashingStreamSource(await HttpStreamSource.CreateAsync(http, new Uri(server.Url + "disk.img.zip"), CancellationToken.None));
        using var container = ContainerRegistry.Default.Open(source);

        var result = await WriteAsync(container, dir.File("target.bin"), image.Length + 4096, SHA256.HashData(zip));

        Assert.Equal(SourceChecksumStatus.Unverifiable, result.SourceChecksum);
        Assert.Equal(image, File.ReadAllBytes(dir.File("target.bin")).AsSpan(0, image.Length).ToArray());
    }

    [Fact]
    public async Task A_zip_on_disk_is_hashed_up_front_like_any_local_file()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 4000);
        var zip = Zip("disk.img", image);
        File.WriteAllBytes(dir.File("disk.img.zip"), zip);
        using var container = ContainerRegistry.Default.Open(new FileStreamSource(dir.File("disk.img.zip")));

        var result = await WriteAsync(container, dir.File("target.bin"), image.Length + 4096, SHA256.HashData(zip));

        Assert.Equal(SourceChecksumStatus.Verified, result.SourceChecksum);
    }

    [Fact]
    public async Task A_download_that_is_not_hashed_cannot_be_verified()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 4000);
        var gz = Gzip(image);
        using var server = new HttpSourceTests.TestHttpServer(gz, supportRanges: true, dropFirstConnection: false);
        using var http = new HttpClient();
        using var container = ContainerRegistry.Default.Open(await HttpStreamSource.CreateAsync(http, new Uri(server.Url + "disk.img.gz"), CancellationToken.None));

        var result = await WriteAsync(container, dir.File("target.bin"), image.Length + 4096, SHA256.HashData(gz));

        Assert.Equal(SourceChecksumStatus.Unverifiable, result.SourceChecksum);
    }

    // ---- ChecksumDiscovery ----

    [Fact]
    public void Discovery_prefers_the_sidecar_of_the_image()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("disk.img.xz"), "x");
        File.WriteAllText(dir.File("disk.img.xz.sha256"), HashA + "\n");
        File.WriteAllText(dir.File("SHA256SUMS"), $"{HashB}  disk.img.xz\n");

        var found = ChecksumDiscovery.FindForFile(dir.File("disk.img.xz"));

        Assert.NotNull(found);
        Assert.Equal(HashA, Sha256Hex.Format(found.Sha256));
        Assert.Equal("disk.img.xz.sha256", found.Origin);
    }

    [Fact]
    public void Discovery_reads_the_list_of_the_directory_by_file_name()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("disk.img.xz"), "x");
        File.WriteAllText(dir.File("SHA256SUMS"), $"{HashA}  other.img.xz\n{HashB} *disk.img.xz\n");

        var found = ChecksumDiscovery.FindForFile(dir.File("disk.img.xz"));

        Assert.NotNull(found);
        Assert.Equal(HashB, Sha256Hex.Format(found.Sha256));
        Assert.Equal("SHA256SUMS", found.Origin);
    }

    [Fact]
    public void Discovery_finds_nothing_when_the_list_does_not_mention_the_image()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("disk.img.xz"), "x");
        File.WriteAllText(dir.File("SHA256SUMS"), $"{HashA}  other.img.xz\n");

        Assert.Null(ChecksumDiscovery.FindForFile(dir.File("disk.img.xz")));
    }

    [Fact]
    public async Task Discovery_by_url_falls_back_from_sidecars_to_the_list()
    {
        var handler = new FakeHttp(uri => uri.AbsolutePath switch
        {
            "/dl/raspios.img.xz.sha256" => Text(System.Net.HttpStatusCode.NotFound, "no"),
            "/dl/raspios.img.xz.sha256sum" => Text(System.Net.HttpStatusCode.OK, "<html>soft 404</html>"),
            "/dl/SHA256SUMS" => Text(System.Net.HttpStatusCode.OK, $"{HashA}  other.img.xz\n{HashB}  raspios.img.xz\n"),
            _ => Text(System.Net.HttpStatusCode.NotFound, "no"),
        });
        using var http = new HttpClient(handler);

        var found = await ChecksumDiscovery.FindForUrlAsync(http, new Uri("https://example.test/dl/raspios.img.xz"), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(HashB, Sha256Hex.Format(found.Sha256));
        Assert.Equal("https://example.test/dl/SHA256SUMS", found.Origin);
    }

    [Fact]
    public async Task Discovery_by_url_accepts_a_sidecar_with_only_the_hash()
    {
        var handler = new FakeHttp(uri => uri.AbsolutePath == "/dl/a%20b.img.xz.sha256"
            ? Text(System.Net.HttpStatusCode.OK, HashA + "\n")
            : Text(System.Net.HttpStatusCode.NotFound, "no"));
        using var http = new HttpClient(handler);

        var found = await ChecksumDiscovery.FindForUrlAsync(http, new Uri("https://example.test/dl/a%20b.img.xz"), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(HashA, Sha256Hex.Format(found.Sha256));
    }

    [Fact]
    public async Task Discovery_by_url_does_not_guess_neighbours_of_parameterized_links()
    {
        var handler = new FakeHttp(_ => Text(System.Net.HttpStatusCode.OK, HashA));
        using var http = new HttpClient(handler);

        var found = await ChecksumDiscovery.FindForUrlAsync(http, new Uri("https://example.test/dl/raspios.img.xz?token=1"), CancellationToken.None);

        Assert.Null(found);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Discovery_by_url_treats_network_errors_as_nothing_found()
    {
        using var http = new HttpClient(new FakeHttp(_ => throw new HttpRequestException("offline")));

        Assert.Null(await ChecksumDiscovery.FindForUrlAsync(http, new Uri("https://example.test/dl/x.img.xz"), CancellationToken.None));
    }

    [Fact]
    public async Task Discovery_by_url_still_honours_cancellation()
    {
        using var http = new HttpClient(new FakeHttp(_ => Text(System.Net.HttpStatusCode.NotFound, "no")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ChecksumDiscovery.FindForUrlAsync(http, new Uri("https://example.test/dl/x.img.xz"), cts.Token));
    }

    // ---- helpers ----

    private static Task<WriteImageResult> WriteAsync(
        IImageContainer container,
        string targetPath,
        long deviceSize,
        byte[]? expected,
        IProgress<OperationProgress>? progress = null) =>
        WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(targetPath, deviceSize),
            new WriteImageOptions { Verify = true, ChunkSize = 64 * 1024, ExpectedSourceSha256 = expected },
            progress,
            CancellationToken.None);

    private static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Zip(string entryName, byte[] data)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest).Open();
            entry.Write(data);
        }

        return output.ToArray();
    }

    private static HttpResponseMessage Text(System.Net.HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private sealed class MemorySource(byte[] data, bool knownLength = true) : IStreamSource
    {
        public string Name => "memory.bin";

        public string Location => "memory://test";

        public long? Length => knownLength ? data.Length : null;

        public bool IsSeekable => true;

        public bool IsRemote => true;

        public Stream Open(CancellationToken cancellationToken = default) => new MemoryStream(data, writable: false);
    }

    private sealed class CollectedProgress : IProgress<OperationProgress>
    {
        private readonly List<OperationProgress> _items = [];

        public void Report(OperationProgress value)
        {
            lock (_items)
            {
                _items.Add(value);
            }
        }

        public List<OperationPhase> Phases()
        {
            lock (_items)
            {
                return _items.Select(p => p.Phase).ToList();
            }
        }
    }

    private sealed class FakeHttp(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
