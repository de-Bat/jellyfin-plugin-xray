using Jellyfin.Plugin.XRay.Model;

namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// Queries over a stored timeline.
/// </summary>
public static class TimelineQuery
{
    /// <summary>
    /// Gets the people on screen at a position, most confident first.
    /// </summary>
    /// <param name="timeline">Timeline.</param>
    /// <param name="positionTicks">Playback position.</param>
    /// <returns>Distinct person ids.</returns>
    public static List<Guid> PeopleAt(XRayTimeline timeline, long positionTicks)
    {
        return timeline.Appearances
            .Where(a => a.StartTicks <= positionTicks && positionTicks < a.EndTicks)
            .GroupBy(a => a.PersonId)
            .Select(g => (Id: g.Key, Confidence: g.Max(a => a.Confidence)))
            .OrderByDescending(x => x.Confidence)
            .Select(x => x.Id)
            .ToList();
    }
}
