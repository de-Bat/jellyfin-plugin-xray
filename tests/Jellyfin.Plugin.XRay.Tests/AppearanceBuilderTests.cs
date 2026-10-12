using Jellyfin.Plugin.XRay.Analysis;
using Jellyfin.Plugin.XRay.Model;

namespace Jellyfin.Plugin.XRay.Tests;

public class AppearanceBuilderTests
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void SingleSighting_CoversHalfIntervalEachSide()
    {
        var result = AppearanceBuilder.Build([new Sighting(10, A, 0.6f)], sampleIntervalSeconds: 2, mergeGapSeconds: 5, durationTicks: null);

        var app = Assert.Single(result);
        Assert.Equal(A, app.PersonId);
        Assert.Equal(9 * TicksPerSecond, app.StartTicks);
        Assert.Equal(11 * TicksPerSecond, app.EndTicks);
        Assert.Equal(0.6f, app.Confidence);
    }

    [Fact]
    public void SightingsWithinGap_AreMerged_KeepingBestConfidence()
    {
        var result = AppearanceBuilder.Build(
            [new Sighting(10, A, 0.5f), new Sighting(12, A, 0.7f), new Sighting(18, A, 0.45f)],
            sampleIntervalSeconds: 2,
            mergeGapSeconds: 5,
            durationTicks: null);

        var app = Assert.Single(result);
        Assert.Equal(9 * TicksPerSecond, app.StartTicks);
        Assert.Equal(19 * TicksPerSecond, app.EndTicks);
        Assert.Equal(0.7f, app.Confidence);
    }

    [Fact]
    public void SightingsBeyondGap_AreSeparateAppearances()
    {
        var result = AppearanceBuilder.Build(
            [new Sighting(10, A, 0.5f), new Sighting(30, A, 0.5f)],
            sampleIntervalSeconds: 2,
            mergeGapSeconds: 5,
            durationTicks: null);

        Assert.Equal(2, result.Count);
        Assert.Equal(29 * TicksPerSecond, result[1].StartTicks);
    }

    [Fact]
    public void PeopleAreTrackedIndependently_AndOutputIsSortedByStart()
    {
        var result = AppearanceBuilder.Build(
            [new Sighting(16, B, 0.5f), new Sighting(10, A, 0.5f), new Sighting(12, B, 0.5f)],
            sampleIntervalSeconds: 2,
            mergeGapSeconds: 5,
            durationTicks: null);

        Assert.Equal(2, result.Count);
        Assert.Equal(A, result[0].PersonId);
        Assert.Equal(B, result[1].PersonId);
        Assert.Equal(11 * TicksPerSecond, result[1].StartTicks);
        Assert.Equal(17 * TicksPerSecond, result[1].EndTicks);
    }

    [Fact]
    public void SpansAreClampedToMediaBounds()
    {
        var result = AppearanceBuilder.Build(
            [new Sighting(0, A, 0.5f), new Sighting(99.5, B, 0.5f)],
            sampleIntervalSeconds: 2,
            mergeGapSeconds: 5,
            durationTicks: 100 * TicksPerSecond);

        Assert.Equal(0, result[0].StartTicks);
        Assert.Equal(100 * TicksPerSecond, result[1].EndTicks);
    }

    [Fact]
    public void PeopleAt_ReturnsDistinctPeopleOnScreen_OrderedByConfidence()
    {
        var timeline = new XRayTimeline
        {
            Appearances =
            [
                new XRayAppearance { PersonId = A, StartTicks = 0, EndTicks = 10 * TicksPerSecond, Confidence = 0.5f },
                new XRayAppearance { PersonId = B, StartTicks = 5 * TicksPerSecond, EndTicks = 20 * TicksPerSecond, Confidence = 0.8f },
                new XRayAppearance { PersonId = A, StartTicks = 8 * TicksPerSecond, EndTicks = 12 * TicksPerSecond, Confidence = 0.6f },
            ]
        };

        Assert.Equal([B, A], TimelineQuery.PeopleAt(timeline, 9 * TicksPerSecond));
        Assert.Equal([B], TimelineQuery.PeopleAt(timeline, 15 * TicksPerSecond));
        Assert.Empty(TimelineQuery.PeopleAt(timeline, 20 * TicksPerSecond));
    }
}
