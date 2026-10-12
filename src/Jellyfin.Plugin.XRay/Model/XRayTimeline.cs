namespace Jellyfin.Plugin.XRay.Model;

/// <summary>
/// The stored X-Ray data for one media item: when each actor is on screen.
/// </summary>
public class XRayTimeline
{
    /// <summary>
    /// Current on-disk format version.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Gets or sets the format version.
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// Gets or sets the media item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets how the timeline was produced ("face" or "manual").
    /// </summary>
    public string Source { get; set; } = "face";

    /// <summary>
    /// Gets or sets when the timeline was produced.
    /// </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the frame sampling interval used, in seconds.
    /// </summary>
    public double SampleIntervalSeconds { get; set; }

    /// <summary>
    /// Gets or sets the appearances, ordered by start time.
    /// </summary>
    public List<XRayAppearance> Appearances { get; set; } = [];
}

/// <summary>
/// A continuous span where one person is on screen.
/// </summary>
public class XRayAppearance
{
    /// <summary>
    /// Gets or sets the Jellyfin person item id.
    /// </summary>
    public Guid PersonId { get; set; }

    /// <summary>
    /// Gets or sets the start position in ticks.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the end position in ticks (exclusive).
    /// </summary>
    public long EndTicks { get; set; }

    /// <summary>
    /// Gets or sets the best match similarity seen in this span (0-1).
    /// </summary>
    public float Confidence { get; set; }
}
