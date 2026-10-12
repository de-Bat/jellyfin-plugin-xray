using Jellyfin.Data.Enums;
using Jellyfin.Plugin.XRay.Analysis;
using Jellyfin.Plugin.XRay.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.ScheduledTasks;

/// <summary>
/// Builds X-Ray timelines for every movie/episode that does not have one yet.
/// </summary>
public class AnalyzeLibraryTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly XRayAnalyzer _analyzer;
    private readonly TimelineStore _store;
    private readonly ILogger<AnalyzeLibraryTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyzeLibraryTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="analyzer">Analyzer.</param>
    /// <param name="store">Timeline store.</param>
    /// <param name="logger">Logger.</param>
    public AnalyzeLibraryTask(ILibraryManager libraryManager, XRayAnalyzer analyzer, TimelineStore store, ILogger<AnalyzeLibraryTask> logger)
    {
        _libraryManager = libraryManager;
        _analyzer = analyzer;
        _store = store;
        _logger = logger;
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

        if (kinds.Count == 0)
        {
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [.. kinds],
                IsVirtualItem = false,
                Recursive = true,
            })
            .Where(i => XRayAnalyzer.CanAnalyze(i) && !_store.Exists(i.Id) && _analyzer.GetCast(i, 1).Count > 0)
            .ToList();

        _logger.LogInformation("X-Ray: {Count} items to analyze", items.Count);
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = i;
                var itemProgress = new Progress<double>(p => progress.Report((index + (p / 100)) / items.Count * 100));
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
