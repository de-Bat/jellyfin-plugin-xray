using System.Buffers.Binary;

namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// Splits a concatenated stream of BMP images (ffmpeg <c>-f image2pipe -c:v bmp</c>) into single frames.
/// </summary>
public static class BmpFrameReader
{
    /// <summary>
    /// Reads frames until the stream ends.
    /// </summary>
    /// <param name="stream">Source stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each complete BMP file.</returns>
    public static async IAsyncEnumerable<byte[]> ReadAllAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var header = new byte[6];
        while (true)
        {
            if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }

            if (header[0] != (byte)'B' || header[1] != (byte)'M')
            {
                throw new InvalidDataException("Frame stream is not a BMP sequence.");
            }

            var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
            if (size <= header.Length || size > 256 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid BMP frame size " + size);
            }

            var frame = new byte[size];
            header.CopyTo(frame, 0);
            if (!await ReadExactAsync(stream, frame.AsMemory(header.Length), cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }

            yield return frame;
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
