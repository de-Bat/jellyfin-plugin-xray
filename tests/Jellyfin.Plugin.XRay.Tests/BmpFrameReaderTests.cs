using Jellyfin.Plugin.XRay.Analysis;

namespace Jellyfin.Plugin.XRay.Tests;

public class BmpFrameReaderTests
{
    private static byte[] FakeBmp(int size, byte fill)
    {
        var b = Enumerable.Repeat(fill, size).ToArray();
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(b, 2);
        return b;
    }

    [Fact]
    public async Task SplitsConcatenatedFrames_AcrossSmallReads()
    {
        var a = FakeBmp(100, 1);
        var b = FakeBmp(57, 2);
        using var stream = new TrickleStream([.. a, .. b]);

        var frames = new List<byte[]>();
        await foreach (var f in BmpFrameReader.ReadAllAsync(stream, CancellationToken.None))
        {
            frames.Add(f);
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal(a, frames[0]);
        Assert.Equal(b, frames[1]);
    }

    [Fact]
    public async Task TruncatedTrailingFrame_IsDropped()
    {
        var a = FakeBmp(40, 1);
        using var stream = new MemoryStream([.. a, .. FakeBmp(40, 2).Take(20)]);

        var frames = new List<byte[]>();
        await foreach (var f in BmpFrameReader.ReadAllAsync(stream, CancellationToken.None))
        {
            frames.Add(f);
        }

        Assert.Single(frames);
    }

    /// <summary>Returns at most 7 bytes per read, like a pipe would.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 7));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
    }
}
