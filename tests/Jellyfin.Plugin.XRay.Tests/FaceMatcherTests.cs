using Jellyfin.Plugin.XRay.Analysis;

namespace Jellyfin.Plugin.XRay.Tests;

public class FaceMatcherTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static float[] Unit(params float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }

    [Fact]
    public void ReturnsBestPerson_WhenAboveThresholdAndMargin()
    {
        var matcher = new FaceMatcher([new(A, Unit(1, 0, 0)), new(B, Unit(0, 1, 0))], threshold: 0.4f, margin: 0.05f);

        var match = matcher.Match(Unit(0.9f, 0.1f, 0));

        Assert.NotNull(match);
        Assert.Equal(A, match.Value.PersonId);
        Assert.True(match.Value.Similarity > 0.9f);
    }

    [Fact]
    public void ReturnsNull_WhenBelowThreshold()
    {
        var matcher = new FaceMatcher([new(A, Unit(1, 0, 0))], threshold: 0.4f, margin: 0.05f);

        Assert.Null(matcher.Match(Unit(0, 0, 1)));
    }

    [Fact]
    public void ReturnsNull_WhenTwoPeopleAreTooClose()
    {
        var matcher = new FaceMatcher([new(A, Unit(1, 0, 0)), new(B, Unit(0, 1, 0))], threshold: 0.4f, margin: 0.05f);

        Assert.Null(matcher.Match(Unit(1, 1, 0)));
    }

    [Fact]
    public void MultipleReferencesForSamePerson_DoNotCompeteOnMargin()
    {
        var matcher = new FaceMatcher([new(A, Unit(1, 0, 0)), new(A, Unit(0.95f, 0.05f, 0))], threshold: 0.4f, margin: 0.05f);

        Assert.Equal(A, matcher.Match(Unit(1, 0.02f, 0))?.PersonId);
    }

    [Fact]
    public void ReferencesLearnedFromTheSameFace_AreIgnored()
    {
        var face = Unit(1, 0, 0);
        var matcher = new FaceMatcher([new(A, Unit(0.3f, 1, 0)), new(A, face, SourceIndex: 7)], threshold: 0.4f, margin: 0.05f);

        Assert.Null(matcher.Match(face, sourceIndex: 7));
        Assert.Equal(1f, matcher.Match(face, sourceIndex: 3)!.Value.Similarity, 3);
    }
}
