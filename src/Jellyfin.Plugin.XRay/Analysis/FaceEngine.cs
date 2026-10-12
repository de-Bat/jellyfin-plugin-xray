using FaceAiSharp;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// A detected face with its identity embedding.
/// </summary>
/// <param name="Box">Face bounding box in image pixels.</param>
/// <param name="Embedding">L2-normalized embedding.</param>
public readonly record struct DetectedFace(RectangleF Box, float[] Embedding);

/// <summary>
/// Face detection (SCRFD) + embedding (ArcFace). Not thread safe.
/// </summary>
public sealed class FaceEngine : IDisposable
{
    private readonly ScrfdDetector _detector;
    private readonly ArcFaceEmbeddingsGenerator _embedder;

    /// <summary>
    /// Initializes a new instance of the <see cref="FaceEngine"/> class.
    /// </summary>
    /// <param name="detectorModelPath">SCRFD model.</param>
    /// <param name="embedderModelPath">ArcFace model.</param>
    public FaceEngine(string detectorModelPath, string embedderModelPath)
    {
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        _detector = new ScrfdDetector(
            new ScrfdDetectorOptions { ModelPath = detectorModelPath, ConfidenceThreshold = 0.6f },
            new SessionOptions { IntraOpNumThreads = threads });
        _embedder = new ArcFaceEmbeddingsGenerator(
            new ArcFaceEmbeddingsGeneratorOptions { ModelPath = embedderModelPath },
            new SessionOptions { IntraOpNumThreads = threads });
    }

    /// <summary>
    /// Detects faces and computes their embeddings.
    /// </summary>
    /// <param name="image">Frame.</param>
    /// <param name="minFaceSize">Minimum face height in pixels.</param>
    /// <param name="maxFaces">Only the largest faces up to this count are embedded.</param>
    /// <returns>Faces.</returns>
    public List<DetectedFace> Analyze(Image<Rgb24> image, int minFaceSize, int maxFaces)
    {
        var faces = _detector.DetectFaces(image)
            .Where(f => f.Landmarks is { Count: >= 5 } && f.Box.Height >= minFaceSize)
            .OrderByDescending(f => f.Box.Width * f.Box.Height)
            .Take(maxFaces);

        var result = new List<DetectedFace>();
        foreach (var face in faces)
        {
            using var aligned = image.Clone();
            ((IFaceEmbeddingsGenerator)_embedder).AlignFaceUsingLandmarks(aligned, face.Landmarks!);
            result.Add(new DetectedFace(face.Box, FaceMatcher.Normalize(_embedder.GenerateEmbedding(aligned))));
        }

        return result;
    }

    /// <summary>
    /// Computes reference embeddings from a portrait: the largest face, and its mirror image.
    /// </summary>
    /// <param name="portrait">Head shot.</param>
    /// <returns>Zero or two embeddings.</returns>
    public List<float[]> ReferenceEmbeddings(Image<Rgb24> portrait)
    {
        var result = new List<float[]>();
        var first = Analyze(portrait, 16, 1);
        using var padded = first.Count == 0 ? Pad(portrait) : null;
        if (padded is not null)
        {
            // Tightly cropped head shots confuse the detector; give it some surroundings.
            first = Analyze(padded, 16, 1);
        }

        if (first.Count == 0)
        {
            return result;
        }

        result.Add(first[0].Embedding);
        using var mirrored = (padded ?? portrait).Clone(x => x.Flip(FlipMode.Horizontal));
        var second = Analyze(mirrored, 16, 1);
        if (second.Count > 0)
        {
            result.Add(second[0].Embedding);
        }

        return result;
    }

    private static Image<Rgb24> Pad(Image<Rgb24> image)
    {
        var canvas = new Image<Rgb24>(image.Width * 2, image.Height * 2, new Rgb24(128, 128, 128));
        canvas.Mutate(x => x.DrawImage(image, new Point(image.Width / 2, image.Height / 2), 1f));
        return canvas;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _detector.Dispose();
        _embedder.Dispose();
    }
}
