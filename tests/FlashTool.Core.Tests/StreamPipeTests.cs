using FlashTool.Core.IO;

namespace FlashTool.Core.Tests;

public class StreamPipeTests
{
    [Fact]
    public async Task Seekable_reader_allows_size_probe_and_sequential_reads_only()
    {
        var data = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var pipe = new StreamPipe(16 * 1024);
        using var reader = pipe.CreateReader(data.Length, reportSeekable: true);
        var producer = Task.Run(() =>
        {
            using var writer = pipe.CreateWriter();
            writer.Write(data);
        });

        Assert.True(reader.CanSeek);
        Assert.Equal(data.Length, reader.Seek(0, SeekOrigin.End));
        Assert.Equal(0, reader.Seek(0, SeekOrigin.Begin));

        var result = new byte[data.Length];
        Assert.Equal(1000, reader.ReadUpTo(result.AsSpan(0, 1000)));
        Assert.Equal(1000, reader.Seek(0, SeekOrigin.Current));
        Assert.Throws<NotSupportedException>(() => reader.Seek(0, SeekOrigin.Begin));

        reader.ReadUpTo(result.AsSpan(1000));
        await producer;
        Assert.Equal(data, result);
    }

    [Fact]
    public void Plain_reader_is_not_seekable()
    {
        var pipe = new StreamPipe();
        using var reader = pipe.CreateReader(10);
        Assert.False(reader.CanSeek);
    }
}
