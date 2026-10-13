using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.XRay.Configuration;

/// <summary>
/// X-Ray plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the number of seconds between analyzed frames.
    /// </summary>
    public double SampleIntervalSeconds { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the width frames are scaled to before face detection.
    /// </summary>
    public int FrameWidth { get; set; } = 960;

    /// <summary>
    /// Gets or sets the minimum cosine similarity for a face to count as an actor.
    /// </summary>
    public double MatchThreshold { get; set; } = 0.40;

    /// <summary>
    /// Gets or sets the minimum similarity margin between the best and second-best actor.
    /// </summary>
    public double MatchMargin { get; set; } = 0.05;

    /// <summary>
    /// Gets or sets the minimum face height in pixels (in the scaled frame) to consider.
    /// </summary>
    public int MinFaceSize { get; set; } = 36;

    /// <summary>
    /// Gets or sets the maximum gap in seconds between sightings that is still merged into one appearance.
    /// </summary>
    public double MergeGapSeconds { get; set; } = 20.0;

    /// <summary>
    /// Gets or sets the maximum number of cast members (by billing order) to look for.
    /// </summary>
    public int MaxCastMembers { get; set; } = 40;

    /// <summary>
    /// Gets or sets a value indicating whether the scheduled task analyzes movies.
    /// </summary>
    public bool AnalyzeMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the scheduled task analyzes episodes.
    /// </summary>
    public bool AnalyzeEpisodes { get; set; } = true;

    /// <summary>
    /// Gets or sets the folders the scheduled task is limited to, one path per line. Empty means all libraries.
    /// </summary>
    public string AnalyzeFolders { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether X-Ray also writes its messages to a dedicated log file
    /// (<c>xray_yyyyMMdd.log</c> in Jellyfin's log folder).
    /// </summary>
    public bool WriteLogFile { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the client script is injected into jellyfin-web's index.html.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets the NuGet feed base used to download the ONNX runtime and face models.
    /// </summary>
    public string NuGetFeed { get; set; } = "https://api.nuget.org/v3-flatcontainer";
}
