using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Plugin.XRay.Model;

namespace Jellyfin.Plugin.XRay.Storage;

/// <summary>
/// Persists timelines as JSON files in the plugin data folder.
/// </summary>
public sealed class TimelineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<Guid, XRayTimeline?> _cache = new();
    private readonly string? _directoryOverride;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimelineStore"/> class.
    /// </summary>
    public TimelineStore()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimelineStore"/> class using a fixed directory.
    /// </summary>
    /// <param name="directory">Storage directory.</param>
    public TimelineStore(string directory)
    {
        _directoryOverride = directory;
    }

    private string Directory => _directoryOverride ?? Path.Combine(Plugin.Instance!.DataFolderPath, "timelines");

    /// <summary>
    /// Gets a timeline.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The timeline or null.</returns>
    public XRayTimeline? Get(Guid itemId)
    {
        return _cache.GetOrAdd(itemId, id =>
        {
            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return null;
            }

            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<XRayTimeline>(fs, JsonOptions);
        });
    }

    /// <summary>
    /// Gets a value indicating whether an item has a timeline.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>True if stored.</returns>
    public bool Exists(Guid itemId) => _cache.TryGetValue(itemId, out var t) ? t is not null : File.Exists(PathFor(itemId));

    /// <summary>
    /// Saves a timeline.
    /// </summary>
    /// <param name="timeline">Timeline.</param>
    public void Save(XRayTimeline timeline)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(timeline.ItemId);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(timeline, JsonOptions));
        File.Move(temp, path, overwrite: true);
        _cache[timeline.ItemId] = timeline;
    }

    /// <summary>
    /// Deletes a timeline.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    public void Delete(Guid itemId)
    {
        File.Delete(PathFor(itemId));
        _cache.TryRemove(itemId, out _);
    }

    private string PathFor(Guid itemId) => Path.Combine(Directory, itemId.ToString("N") + ".json");
}
