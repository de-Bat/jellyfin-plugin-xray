using Jellyfin.Data.Enums;
using Jellyfin.Plugin.XRay.Analysis;
using Jellyfin.Plugin.XRay.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.ScheduledTasks;

/// <summary>
/// Builds X-Ray timelines for every movie/episode (in the configured folders) that does not have one yet.
/// </summary>
public class AnalyzeLibraryTask : IScheduledTask
{
    private readonly XRayAnalyzer _analyzer;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyzeLibraryTask"/> class.
    /// </summary>
    /// <param name="analyzer">Analyzer.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="logFile">X-Ray log file.</param>
    public AnalyzeLibraryTask(XRayAnalyzer analyzer, ILogger<AnalyzeLibraryTask> logger, XRayLogFile logFile)
    {
        _analyzer = analyzer;
        _logger = logFile.Wrap(logger);
    }

    /// <inheritdoc />
    public string Name => "Build X-Ray scene data";

    /// <inheritdoc />
    public string Key => "XRayAnalyzeLibrary";

    /// <inheritdoc />
    public string Description => "Recognizes cast members in movies and episodes so X-Ray can show who is in the current scene.";

    /// <inheritdoc />
    public string Category => "X-Ray";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var kinds = new List<BaseItemKind>();
        if (config.AnalyzeMovies)
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (config.AnalyzeEpisodes)
        {
            kinds.Add(BaseItemKind.Episode);
        }

        var folders = FolderFilter.Parse(config.AnalyzeFolders);
        var items = _analyzer.FindItems(kinds, folders, includeAnalyzed: false);

        if (folders.Count > 0)
        {
            _logger.LogInformation("X-Ray: {Count} items to analyze in {Folders}", items.Count, string.Join(", ", folders));
        }
        else
        {
            _logger.LogInformation("X-Ray: {Count} items to analyze", items.Count);
        }

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = i;
                var itemProgress = new Progress<double>(p => progress.Report((index + (p / 100)) / items.Count * 100));
                _logger.LogInformation("X-Ray: analyzing {Name} ({Path})", items[i].Name, items[i].Path);
                try
                {
                    await _analyzer.AnalyzeAsync(items[i].Id, itemProgress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "X-Ray: failed to analyze {Name}", items[i].Name);
                }
            }
        }
        finally
        {
            _analyzer.ReleaseEngine();
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
                MaxRuntimeTicks = TimeSpan.FromHours(5).Ticks,
            }
        ];
    }
}
