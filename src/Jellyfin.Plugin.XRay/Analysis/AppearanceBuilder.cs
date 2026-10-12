using Jellyfin.Plugin.XRay.Model;

namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// One recognized face in one sampled frame.
/// </summary>
/// <param name="TimeSeconds">Frame position in seconds.</param>
/// <param name="PersonId">Recognized person.</param>
/// <param name="Similarity">Match similarity.</param>
public readonly record struct Sighting(double TimeSeconds, Guid PersonId, float Similarity);

/// <summary>
/// Turns per-frame sightings into continuous on-screen appearances.
/// </summary>
public static class AppearanceBuilder
{
    /// <summary>
    /// Builds appearances. Each sighting covers half a sample interval on either side; sightings of the
    /// same person whose spans are at most <paramref name="mergeGapSeconds"/> apart are merged.
    /// </summary>
    /// <param name="sightings">Sightings in any order.</param>
    /// <param name="sampleIntervalSeconds">Frame sampling interval.</param>
    /// <param name="mergeGapSeconds">Maximum gap that is bridged.</param>
    /// <param name="durationTicks">Media duration used for clamping, if known.</param>
    /// <returns>Appearances sorted by start time.</returns>
    public static List<XRayAppearance> Build(
        IEnumerable<Sighting> sightings,
        double sampleIntervalSeconds,
        double mergeGapSeconds,
        long? durationTicks)
    {
        var half = TimeSpan.FromSeconds(sampleIntervalSeconds / 2).Ticks;
        var gap = TimeSpan.FromSeconds(mergeGapSeconds).Ticks;
        var max = durationTicks ?? long.MaxValue;
        var result = new List<XRayAppearance>();

        foreach (var group in sightings.GroupBy(s => s.PersonId))
        {
            XRayAppearance? current = null;
            foreach (var s in group.OrderBy(s => s.TimeSeconds))
            {
                var center = TimeSpan.FromSeconds(s.TimeSeconds).Ticks;
                var start = Math.Max(0, center - half);
                var end = Math.Min(max, center + half);

                if (current is not null && start - current.EndTicks <= gap)
                {
                    current.EndTicks = Math.Max(current.EndTicks, end);
                    current.Confidence = Math.Max(current.Confidence, s.Similarity);
                    continue;
                }

                current = new XRayAppearance { PersonId = group.Key, StartTicks = start, EndTicks = end, Confidence = s.Similarity };
                result.Add(current);
            }
        }

        result.Sort((a, b) => a.StartTicks != b.StartTicks ? a.StartTicks.CompareTo(b.StartTicks) : a.PersonId.CompareTo(b.PersonId));
        return result;
    }
}
