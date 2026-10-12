using System.Net.Http.Headers;

namespace Jellyfin.Plugin.XRay.Runtime;

/// <summary>
/// Read-only seekable stream over an HTTP resource using range requests, so a
/// <see cref="System.IO.Compression.ZipArchive"/> can pull single entries out of a large remote package.
/// </summary>
internal sealed class HttpRangeStream : Stream
{
    private const int ChunkSize = 1 << 20;

    private readonly HttpClient _client;
    private readonly Uri _uri;
    private readonly long _length;
    private byte[] _buffer = [];
    private long _bufferStart = -1;
    private long _position;

    private HttpRangeStream(HttpClient client, Uri uri, long length)
    {
        _client = client;
        _uri = uri;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    /// <summary>
    /// Opens the stream, or returns null if the server does not support range requests.
    /// </summary>
    /// <param name="client">HTTP client.</param>
    /// <param name="uri">Resource.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stream, or null.</returns>
    public static async Task<HttpRangeStream?> TryOpenAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var total = response.Content.Headers.ContentRange?.Length;
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent || total is null)
        {
            return null;
        }

        // Follow redirects once so later range requests hit the final host directly.
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        return new HttpRangeStream(client, finalUri, total.Value);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length || count == 0)
        {
            return 0;
        }

        if (_bufferStart < 0 || _position < _bufferStart || _position >= _bufferStart + _buffer.Length)
        {
            Fill(_position);
        }

        var available = (int)(_bufferStart + _buffer.Length - _position);
        var n = Math.Min(count, available);
        Array.Copy(_buffer, _position - _bufferStart, buffer, offset, n);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Fill(long start)
    {
        var end = Math.Min(_length, start + ChunkSize) - 1;
        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = _client.Send(request, HttpCompletionOption.ResponseContentRead);
        response.EnsureSuccessStatusCode();
        using var content = response.Content.ReadAsStream();
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        _buffer = ms.ToArray();
        _bufferStart = start;
        if (_buffer.Length == 0)
        {
            throw new IOException("Empty range response from " + _uri);
        }
    }
}
