using System.Net;
using System.Net.Sockets;
using FlashTool.Core.Containers;
using FlashTool.Core.IO;
using FlashTool.Core.Operations;
using FlashTool.Core.Sources;

namespace FlashTool.Core.Tests;

public sealed class HttpSourceTests : IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false });

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Image_is_written_straight_from_url(bool supportRanges, bool dropFirstConnection)
    {
        using var dir = new TempDir();
        var image = TestImages.CreateMbrImage(secondPartitionSectors: 20000);
        File.WriteAllBytes(dir.File("src.bin"), image);
        await ReadImageOperation.RunAsync(
            () => HandleBlockDevice.OpenFile(dir.File("src.bin"), writable: false),
            dir.File("remote.img.xz"),
            ContainerRegistry.Default,
            new ReadImageOptions { Verify = false },
            null,
            CancellationToken.None);

        using var server = new TestHttpServer(File.ReadAllBytes(dir.File("remote.img.xz")), supportRanges, dropFirstConnection);
        var source = await HttpStreamSource.CreateAsync(_client, new Uri(server.Url + "files/remote.img.xz"));

        Assert.Equal("remote.img.xz", source.Name);
        Assert.Equal(supportRanges, source.IsSeekable);

        using var container = ContainerRegistry.Default.Open(source);
        Assert.Equal(ContainerFormat.Xz, container.Format);
        Assert.Equal(supportRanges ? image.Length : null, container.Entries[0].Length);

        var result = await WriteImageOperation.RunAsync(
            container,
            container.Entries[0],
            () => HandleBlockDevice.OpenFile(dir.File("target.bin"), image.Length),
            new WriteImageOptions { Verify = true },
            null,
            CancellationToken.None);

        Assert.Equal(image.Length, result.BytesWritten);
        Assert.Equal(image, File.ReadAllBytes(dir.File("target.bin")));
    }

    [Fact]
    public async Task Zip_from_server_without_ranges_is_reported()
    {
        using var dir = new TempDir();
        var zipPath = dir.File("a.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("a.img").Open())
        {
            s.Write(TestImages.CreateMbrImage());
        }

        using var server = new TestHttpServer(File.ReadAllBytes(zipPath), supportRanges: false, dropFirstConnection: false);
        var source = await HttpStreamSource.CreateAsync(_client, new Uri(server.Url + "a.zip"));

        var ex = Assert.Throws<FlashToolException>(() => ContainerRegistry.Default.Open(source));
        Assert.Equal(FlashErrorCode.SourceNotSeekable, ex.Code);
    }

    [Fact]
    public async Task Zip_from_server_with_ranges_is_readable()
    {
        using var dir = new TempDir();
        var image = TestImages.CreateGptImage();
        var zipPath = dir.File("a.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("disk.img").Open())
        {
            s.Write(image);
        }

        using var server = new TestHttpServer(File.ReadAllBytes(zipPath), supportRanges: true, dropFirstConnection: false);
        var source = await HttpStreamSource.CreateAsync(_client, new Uri(server.Url + "a.zip"));
        using var container = ContainerRegistry.Default.Open(source);
        using var reader = container.OpenEntry(container.Entries[0]);
        var data = new byte[image.Length + 10];
        var n = reader.Data.ReadUpTo(data);

        Assert.Equal(image, data.AsSpan(0, n).ToArray());
    }

    /// <summary>Minimal HTTP server with optional range support and a forced connection drop.</summary>
    private sealed class TestHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _content;
        private readonly bool _supportRanges;
        private int _dropsLeft;

        public TestHttpServer(byte[] content, bool supportRanges, bool dropFirstConnection)
        {
            _content = content;
            _supportRanges = supportRanges;
            _dropsLeft = dropFirstConnection ? 1 : 0;
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public void Dispose() => _listener.Close();

        private static int GetFreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            var response = ctx.Response;
            try
            {
                long from = 0;
                long to = _content.Length - 1;
                var range = ctx.Request.Headers["Range"];
                if (_supportRanges && range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var parts = range["bytes=".Length..].Split('-');
                    from = long.Parse(parts[0]);
                    if (parts[1].Length > 0)
                    {
                        to = long.Parse(parts[1]);
                    }

                    response.StatusCode = 206;
                    response.Headers["Content-Range"] = $"bytes {from}-{to}/{_content.Length}";
                }

                response.Headers["Accept-Ranges"] = _supportRanges ? "bytes" : "none";
                var length = (int)(to - from + 1);
                response.ContentLength64 = length;

                // Drop the first large transfer halfway to exercise resuming.
                if (length > 4096 && Interlocked.Exchange(ref _dropsLeft, 0) == 1)
                {
                    await response.OutputStream.WriteAsync(_content.AsMemory((int)from, length / 2));
                    await response.OutputStream.FlushAsync();
                    response.Abort();
                    return;
                }

                await response.OutputStream.WriteAsync(_content.AsMemory((int)from, length));
                response.Close();
            }
            catch (Exception)
            {
                response.Abort();
            }
        }
    }
}
