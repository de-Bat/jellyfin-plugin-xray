using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// A decoded sample frame.
/// </summary>
/// <param name="TimeSeconds">Position in seconds.</param>
/// <param name="Bmp">BMP file bytes.</param>
public readonly record struct SampledFrame(double TimeSeconds, byte[] Bmp);

/// <summary>
/// Samples frames from a video with ffmpeg at a fixed interval.
/// </summary>
public static class FrameExtractor
{
    /// <summary>
    /// Builds the ffmpeg argument list.
    /// </summary>
    /// <param name="inputPath">Media path or URL.</param>
    /// <param name="intervalSeconds">Seconds between frames.</param>
    /// <param name="width">Maximum output width.</param>
    /// <returns>Arguments.</returns>
    public static IReadOnlyList<string> BuildArguments(string inputPath, double intervalSeconds, int width)
    {
        var fps = (1.0 / intervalSeconds).ToString("0.######", CultureInfo.InvariantCulture);
        return
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", inputPath,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={fps},scale='min({width},iw)':-2:flags=bilinear",
            "-pix_fmt", "bgr24",
            "-f", "image2pipe", "-c:v", "bmp", "-"
        ];
    }

    /// <summary>
    /// Streams sampled frames. Frame <c>n</c> is at <c>n * interval</c> seconds.
    /// </summary>
    /// <param name="ffmpegPath">ffmpeg executable.</param>
    /// <param name="inputPath">Media path or URL.</param>
    /// <param name="intervalSeconds">Seconds between frames.</param>
    /// <param name="width">Maximum output width.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Frames.</returns>
    public static async IAsyncEnumerable<SampledFrame> ExtractAsync(
        string ffmpegPath,
        string inputPath,
        double intervalSeconds,
        int width,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArguments(inputPath, intervalSeconds, width))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && stderr.Length < 4000)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
        {
            // Not permitted on every platform; analysis still works at normal priority.
        }

        var index = 0;
        var completed = false;
        try
        {
            await foreach (var bmp in BmpFrameReader.ReadAllAsync(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false))
            {
                yield return new SampledFrame(index * intervalSeconds, bmp);
                index++;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            if (!completed && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }

        if (process.ExitCode != 0 && index == 0)
        {
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {stderr}");
        }
    }
}
