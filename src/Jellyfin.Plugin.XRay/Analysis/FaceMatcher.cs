namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// A reference face embedding for a person.
/// </summary>
/// <param name="PersonId">Person id.</param>
/// <param name="Embedding">L2-normalized embedding.</param>
/// <param name="SourceIndex">Index of the frame face this reference was learned from, or -1 for portraits.</param>
public readonly record struct FaceReference(Guid PersonId, float[] Embedding, int SourceIndex = -1);

/// <summary>
/// A successful match.
/// </summary>
/// <param name="PersonId">Matched person.</param>
/// <param name="Similarity">Cosine similarity.</param>
public readonly record struct FaceMatch(Guid PersonId, float Similarity);

/// <summary>
/// Matches face embeddings against a set of known people.
/// </summary>
public sealed class FaceMatcher
{
    private readonly IReadOnlyList<FaceReference> _references;
    private readonly float _threshold;
    private readonly float _margin;

    /// <summary>
    /// Initializes a new instance of the <see cref="FaceMatcher"/> class.
    /// </summary>
    /// <param name="references">Known faces (L2-normalized).</param>
    /// <param name="threshold">Minimum similarity.</param>
    /// <param name="margin">Minimum lead of the best person over the next best person.</param>
    public FaceMatcher(IReadOnlyList<FaceReference> references, float threshold, float margin)
    {
        _references = references;
        _threshold = threshold;
        _margin = margin;
    }

    /// <summary>
    /// Gets the number of references.
    /// </summary>
    public int Count => _references.Count;

    /// <summary>
    /// Finds the person an embedding belongs to.
    /// </summary>
    /// <param name="embedding">L2-normalized face embedding.</param>
    /// <param name="sourceIndex">Index of the face being matched; references learned from it are ignored.</param>
    /// <returns>The match, or null if nobody is a confident match.</returns>
    public FaceMatch? Match(float[] embedding, int sourceIndex = -1)
    {
        Guid bestId = Guid.Empty;
        float best = float.MinValue;
        float second = float.MinValue;

        // Best similarity per person first, so several references of one person never compete.
        var perPerson = new Dictionary<Guid, float>();
        foreach (var r in _references)
        {
            if (sourceIndex >= 0 && r.SourceIndex == sourceIndex)
            {
                continue;
            }

            var sim = Dot(embedding, r.Embedding);
            if (!perPerson.TryGetValue(r.PersonId, out var prev) || sim > prev)
            {
                perPerson[r.PersonId] = sim;
            }
        }

        foreach (var (id, sim) in perPerson)
        {
            if (sim > best)
            {
                second = best;
                best = sim;
                bestId = id;
            }
            else if (sim > second)
            {
                second = sim;
            }
        }

        if (best < _threshold || (second != float.MinValue && best - second < _margin))
        {
            return null;
        }

        return new FaceMatch(bestId, best);
    }

    /// <summary>
    /// Normalizes a vector to unit length in place.
    /// </summary>
    /// <param name="v">Vector.</param>
    /// <returns>The same vector.</returns>
    public static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(Dot(v, v));
        if (norm > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }

        return v;
    }

    private static float Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        float sum = 0;
        for (var i = 0; i < n; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
