using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace FlashTool.Core.Sources;

/// <summary>
/// Read-only HTTP stream. With range support it is seekable and transparently
/// reconnects (resuming at the current position) after network failures.
/// </summary>
public sealed class HttpRangeStream : Stream
{
    private const int MaxRetries = 8;
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _client;
    private readonly Uri _uri;
    private readonly long? _length;
    private readonly bool _seekable;
    private readonly EntityTagHeaderValue? _etag;
    private readonly CancellationToken _cancellationToken;

    private HttpResponseMessage? _response;
    private Stream? _body;
    private long _bodyPosition;
    private long _position;

    public HttpRangeStream(HttpClient client, Uri uri, long? length, bool seekable, EntityTagHeaderValue? etag, CancellationToken cancellationToken)
    {
        _client = client;
        _uri = uri;
        _length = length;
        _seekable = seekable;
        _etag = etag;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => true;

    public override bool CanSeek => _seekable;

    public override bool CanWrite => false;

    public override long Length => _length ?? throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var n = Read(rented, 0, buffer.Length);
            rented.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || (_length.HasValue && _position >= _length.Value))
        {
            return 0;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationToken);
        var attempt = 0;
        while (true)
        {
            try
            {
                await EnsureBodyAsync(linked.Token).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                timeout.CancelAfter(BodyReadTimeout);
                var n = await _body!.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);

                if (n == 0 && _length.HasValue && _position < _length.Value)
                {
                    throw new IOException("Connection closed before the end of the content.");
                }

                _position += n;
                _bodyPosition += n;
                return n;
            }
            catch (Exception ex) when (IsTransient(ex) && !linked.IsCancellationRequested && _seekable && attempt < MaxRetries)
            {
                attempt++;
                ResetBody();
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            {
                throw new FlashToolException(FlashErrorCode.HttpError, "The server stopped sending data.", null, 0, "timeout");
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!_seekable)
        {
            throw new NotSupportedException();
        }

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ResetBody();
        }

        base.Dispose(disposing);
    }

    private async Task EnsureBodyAsync(CancellationToken cancellationToken)
    {
        if (_body != null && _bodyPosition == _position)
        {
            return;
        }

        if (_body != null && !_seekable)
        {
            throw new NotSupportedException("Stream is not seekable.");
        }

        ResetBody();

        var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        if (_seekable)
        {
            request.Headers.Range = new RangeHeaderValue(_position, null);
            if (_etag != null && !_etag.IsWeak)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(_etag);
            }
        }

        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            if (_seekable && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // 200 means the server ignored the range (or the file changed, see If-Range).
                throw new FlashToolException(
                    FlashErrorCode.HttpError,
                    $"Server returned HTTP {(int)response.StatusCode} for a range request.",
                    null,
                    (int)response.StatusCode,
                    response.ReasonPhrase);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
            }

            if (_seekable && response.Content.Headers.ContentRange?.From is { } from && from != _position)
            {
                throw new FlashToolException(FlashErrorCode.HttpError, "Server returned an unexpected range.", null, (int)response.StatusCode, "range");
            }

            _response = response;
            _body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            _bodyPosition = _position;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void ResetBody()
    {
        _body?.Dispose();
        _body = null;
        _response?.Dispose();
        _response = null;
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        IOException => true,
        HttpRequestException h => h.StatusCode is null or >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        OperationCanceledException => true, // per-read timeout
        _ => false,
    };
}
