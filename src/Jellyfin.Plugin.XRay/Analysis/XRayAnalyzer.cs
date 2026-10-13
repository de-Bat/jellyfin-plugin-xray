using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.XRay.Configuration;
using Jellyfin.Plugin.XRay.Logging;
using Jellyfin.Plugin.XRay.Model;
using Jellyfin.Plugin.XRay.Runtime;
using Jellyfin.Plugin.XRay.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// Builds X-Ray timelines by recognizing cast faces in sampled video frames.
/// </summary>
public sealed class XRayAnalyzer : IDisposable
{
    private const int MaxFacesPerFrame = 8;
    private const int MaxLearnedReferencesPerPerson = 24;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ModelManager _modelManager;
    private readonly TimelineStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FaceEngine? _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="XRayAnalyzer"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaEncoder">Media encoder (for the ffmpeg path).</param>
    /// <param name="modelManager">Model manager.</param>
    /// <param name="store">Timeline store.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="logFile">X-Ray log file.</param>
    public XRayAnalyzer(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        ModelManager modelManager,
        TimelineStore store,
        IHttpClientFactory httpClientFactory,
        ILogger<XRayAnalyzer> logger,
        XRayLogFile logFile)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _modelManager = modelManager;
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logFile.Wrap(logger);
    }

    private static string FaceCacheDirectory => Path.Combine(Plugin.Instance!.DataFolderPath, "faces");

    /// <summary>
    /// Gets a value indicating whether an item can be analyzed.
    /// </summary>
    /// <param name="item">Item.</param>
    /// <returns>True for plain video files.</returns>
    public static bool CanAnalyze(BaseItem? item)
        => item is Video { VideoType: VideoType.VideoFile } video && !string.IsNullOrEmpty(video.Path) && item is not Trailer;

    /// <summary>
    /// Finds the items that can be analyzed: plain video files with at least one cast member.
    /// </summary>
    /// <param name="kinds">Item types to include.</param>
    /// <param name="folders">Folders to limit the search to; empty means all libraries.</param>
    /// <param name="includeAnalyzed">Whether items that already have scene data are included.</param>
    /// <returns>Items.</returns>
    public IReadOnlyList<BaseItem> FindItems(IReadOnlyList<BaseItemKind> kinds, IReadOnlyList<string> folders, bool includeAnalyzed)
    {
        if (kinds.Count == 0)
        {
            return [];
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [.. kinds],
                IsVirtualItem = false,
                Recursive = true,
            })
            .Where(i => CanAnalyze(i)
                && FolderFilter.Includes(folders, i.Path)
                && (includeAnalyzed || !_store.Exists(i.Id))
                && GetCast(i, 1).Count > 0)
            .ToList();
    }

    /// <summary>
    /// Gets the cast members considered for recognition, in billing order.
    /// </summary>
    /// <param name="item">Item.</param>
    /// <param name="max">Maximum count.</param>
    /// <returns>Cast.</returns>
    public IReadOnlyList<PersonInfo> GetCast(BaseItem item, int max)
        => _libraryManager.GetPeople(item)
            .Where(p => p.Type is PersonKind.Actor or PersonKind.GuestStar)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(max)
            .ToList();

    /// <summary>
    /// Analyzes an item and stores its timeline.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="progress">Progress 0-100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The timeline.</returns>
    public async Task<XRayTimeline> AnalyzeAsync(Guid itemId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId) ?? throw new ArgumentException("Item not found: " + itemId);
        if (!CanAnalyze(item))
        {
            throw new ArgumentException($"Item {item.Name} is not a plain video file and cannot be analyzed.");
        }

        var config = Plugin.Instance!.Configuration;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _modelManager.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            _engine ??= new FaceEngine(_modelManager.DetectorModelPath, _modelManager.EmbedderModelPath);

            var references = await LoadReferencesAsync(item, config, cancellationToken).ConfigureAwait(false);
            var timeline = new XRayTimeline
            {
                ItemId = item.Id,
                Source = "face",
                SampleIntervalSeconds = config.SampleIntervalSeconds,
            };

            if (references.Count == 0)
            {
                _logger.LogWarning("X-Ray: no usable cast portraits for {Name}; storing empty timeline", item.Name);
                _store.Save(timeline);
                return timeline;
            }

            var faces = await ScanAsync(item, config, progress, cancellationToken).ConfigureAwait(false);
            var sightings = MatchWithSelfTraining(faces, references, config);
            timeline.Appearances = AppearanceBuilder.Build(sightings, config.SampleIntervalSeconds, config.MergeGapSeconds, item.RunTimeTicks);
            _store.Save(timeline);

            _logger.LogInformation(
                "X-Ray: analyzed {Name}: {Frames} faces, {Sightings} recognized, {People} people, {Appearances} appearances",
                item.Name,
                faces.Count,
                sightings.Count,
                timeline.Appearances.Select(a => a.PersonId).Distinct().Count(),
                timeline.Appearances.Count);
            progress?.Report(100);
            return timeline;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Frees the loaded models until the next analysis.
    /// </summary>
    public void ReleaseEngine()
    {
        if (_gate.Wait(0))
        {
            try
            {
                _engine?.Dispose();
                _engine = null;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _engine?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Matches faces against portraits, then adds the most confident frame faces as extra references
    /// (actors rarely look exactly like their head shot) and matches again.
    /// </summary>
    internal static List<Sighting> MatchWithSelfTraining(
        IReadOnlyList<(double Time, float[] Embedding)> faces,
        IReadOnlyList<FaceReference> references,
        PluginConfiguration config)
    {
        var threshold = (float)config.MatchThreshold;
        var margin = (float)config.MatchMargin;
        var first = new FaceMatcher(references, threshold, margin);

        var learnThreshold = Math.Max(threshold + 0.12f, 0.5f);
        var learned = faces
            .Select((f, i) => (Index: i, f.Embedding, Match: first.Match(f.Embedding)))
            .Where(x => x.Match is { } m && m.Similarity >= learnThreshold)
            .GroupBy(x => x.Match!.Value.PersonId)
            .SelectMany(g => g.OrderByDescending(x => x.Match!.Value.Similarity)
                .Take(MaxLearnedReferencesPerPerson)
                .Select(x => new FaceReference(g.Key, x.Embedding, x.Index)));

        // Learned references never vouch for the face they came from, so similarities stay honest.
        var second = new FaceMatcher([.. references, .. learned], threshold, margin);
        var sightings = new List<Sighting>();
        for (var i = 0; i < faces.Count; i++)
        {
            if (second.Match(faces[i].Embedding, i) is { } m)
            {
                sightings.Add(new Sighting(faces[i].Time, m.PersonId, m.Similarity));
            }
        }

        return sightings;
    }

    private async Task<List<(double Time, float[] Embedding)>> ScanAsync(
        BaseItem item,
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var faces = new List<(double, float[])>();
        var duration = item.RunTimeTicks is > 0 ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : 0;
        await foreach (var frame in FrameExtractor.ExtractAsync(
            _mediaEncoder.EncoderPath, item.Path, config.SampleIntervalSeconds, config.FrameWidth, cancellationToken).ConfigureAwait(false))
        {
            using var image = Image.Load<Rgb24>(frame.Bmp);
            foreach (var face in _engine!.Analyze(image, config.MinFaceSize, MaxFacesPerFrame))
            {
                faces.Add((frame.TimeSeconds, face.Embedding));
            }

            if (duration > 0)
            {
                progress?.Report(Math.Min(99, frame.TimeSeconds / duration * 100));
            }
        }

        return faces;
    }

    private async Task<List<FaceReference>> LoadReferencesAsync(BaseItem item, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var references = new List<FaceReference>();
        foreach (var info in GetCast(item, config.MaxCastMembers))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var person = _libraryManager.GetPerson(info.Name);
            var image = person?.GetImageInfo(ImageType.Primary, 0);
            if (person is null || image is null)
            {
                continue;
            }

            try
            {
                foreach (var embedding in await GetPortraitEmbeddingsAsync(person, image, cancellationToken).ConfigureAwait(false))
                {
                    references.Add(new FaceReference(person.Id, embedding));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "X-Ray: could not use portrait of {Person}", info.Name);
            }
        }

        _logger.LogInformation("X-Ray: {Count} reference faces for {Name}", references.Count, item.Name);
        return references;
    }

    private async Task<List<float[]>> GetPortraitEmbeddingsAsync(Person person, ItemImageInfo image, CancellationToken cancellationToken)
    {
        var key = image.Path + "|" + image.DateModified.Ticks;
        var cachePath = Path.Combine(FaceCacheDirectory, person.Id.ToString("N") + ".json");
        if (File.Exists(cachePath))
        {
            var cached = JsonSerializer.Deserialize<PortraitCache>(await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false));
            if (cached?.Key == key)
            {
                return cached.Embeddings;
            }
        }

        byte[] bytes;
        if (image.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            bytes = await _httpClientFactory.CreateClient().GetByteArrayAsync(image.Path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            bytes = await File.ReadAllBytesAsync(image.Path, cancellationToken).ConfigureAwait(false);
        }

        using var portrait = Image.Load<Rgb24>(bytes);
        var embeddings = _engine!.ReferenceEmbeddings(portrait);
        Directory.CreateDirectory(FaceCacheDirectory);
        await File.WriteAllBytesAsync(cachePath, JsonSerializer.SerializeToUtf8Bytes(new PortraitCache(key, embeddings)), cancellationToken).ConfigureAwait(false);
        return embeddings;
    }

    private sealed record PortraitCache(string Key, List<float[]> Embeddings);
}
