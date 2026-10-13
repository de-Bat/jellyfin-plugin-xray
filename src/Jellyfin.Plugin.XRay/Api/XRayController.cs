using System.Net.Mime;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.XRay.Analysis;
using Jellyfin.Plugin.XRay.Logging;
using Jellyfin.Plugin.XRay.Model;
using Jellyfin.Plugin.XRay.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Api;

/// <summary>
/// X-Ray API.
/// </summary>
[ApiController]
[Route("XRay")]
public class XRayController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly TimelineStore _store;
    private readonly AnalysisQueue _queue;
    private readonly XRayAnalyzer _analyzer;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XRayController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="store">Timeline store.</param>
    /// <param name="queue">Analysis queue.</param>
    /// <param name="analyzer">Analyzer.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="logFile">X-Ray log file.</param>
    public XRayController(
        ILibraryManager libraryManager,
        TimelineStore store,
        AnalysisQueue queue,
        XRayAnalyzer analyzer,
        ILogger<XRayController> logger,
        XRayLogFile logFile)
    {
        _libraryManager = libraryManager;
        _store = store;
        _queue = queue;
        _analyzer = analyzer;
        _logger = logFile.Wrap(logger);
    }

    /// <summary>
    /// Gets the full X-Ray data for an item: cast and every on-screen appearance.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Timeline with people.</returns>
    [HttpGet("Items/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<XRayItemDto> GetItem([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var timeline = _store.Get(itemId);
        var cast = _analyzer.GetCast(item, int.MaxValue);
        var people = new List<XRayPersonDto>();
        foreach (var info in cast)
        {
            var person = _libraryManager.GetPerson(info.Name);
            if (person is not null && people.All(p => p.Id != person.Id))
            {
                people.Add(new XRayPersonDto(person.Id, person.Name, info.Role, person.HasImage(MediaBrowser.Model.Entities.ImageType.Primary, 0)));
            }
        }

        return new XRayItemDto(
            itemId,
            timeline is not null,
            timeline?.Source,
            people,
            timeline?.Appearances.Select(a => new XRayAppearanceDto(a.PersonId, a.StartTicks, a.EndTicks)).ToList() ?? [],
            _queue.GetStatus(itemId));
    }

    /// <summary>
    /// Gets the people on screen at a playback position.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="positionTicks">Playback position in ticks.</param>
    /// <returns>Person ids, most confident first.</returns>
    [HttpGet("Items/{itemId}/Scene")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<Guid>> GetScene([FromRoute] Guid itemId, [FromQuery] long positionTicks)
    {
        var timeline = _store.Get(itemId);
        return timeline is null ? NotFound() : TimelineQuery.PeopleAt(timeline, positionTicks);
    }

    /// <summary>
    /// Queues face analysis of an item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Status.</returns>
    [HttpPost("Items/{itemId}/Analyze")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<AnalysisStatus> Analyze([FromRoute] Guid itemId)
    {
        if (!XRayAnalyzer.CanAnalyze(_libraryManager.GetItemById(itemId)))
        {
            return BadRequest("Item is not a plain video file.");
        }

        return Accepted(_queue.Enqueue(itemId));
    }

    /// <summary>
    /// Queues face analysis of every movie and episode in a folder (and its subfolders).
    /// </summary>
    /// <param name="path">Folder path as seen by the server.</param>
    /// <param name="overwrite">Whether items that already have scene data are analyzed again.</param>
    /// <returns>The queued items.</returns>
    [HttpPost("Folders/Analyze")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<XRayFolderAnalysisDto> AnalyzeFolder([FromQuery] string path, [FromQuery] bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("A folder path is required.");
        }

        var items = _analyzer.FindItems([BaseItemKind.Movie, BaseItemKind.Episode], [path], overwrite);
        foreach (var item in items)
        {
            _queue.Enqueue(item.Id);
        }

        _logger.LogInformation("X-Ray: queued {Count} items in {Path}", items.Count, path);
        return Accepted(new XRayFolderAnalysisDto(path, items.Select(i => i.Id).ToList()));
    }

    /// <summary>
    /// Replaces an item's timeline with manually authored data.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="appearances">Appearances.</param>
    /// <returns>No content.</returns>
    [HttpPost("Items/{itemId}/Timeline")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetTimeline([FromRoute] Guid itemId, [FromBody] List<XRayAppearanceDto> appearances)
    {
        if (_libraryManager.GetItemById(itemId) is null)
        {
            return NotFound();
        }

        _store.Save(new XRayTimeline
        {
            ItemId = itemId,
            Source = "manual",
            Appearances = appearances
                .Where(a => a.EndTicks > a.StartTicks)
                .OrderBy(a => a.StartTicks)
                .Select(a => new XRayAppearance { PersonId = a.PersonId, StartTicks = a.StartTicks, EndTicks = a.EndTicks, Confidence = 1 })
                .ToList()
        });
        return NoContent();
    }

    /// <summary>
    /// Deletes an item's timeline.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Items/{itemId}/Timeline")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult DeleteTimeline([FromRoute] Guid itemId)
    {
        _store.Delete(itemId);
        return NoContent();
    }

    /// <summary>
    /// Gets the web client script.
    /// </summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetClientScript()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.xray.js")!;
        Response.Headers.CacheControl = "public, max-age=3600";
        return File(stream, "application/javascript");
    }
}

/// <summary>
/// X-Ray data for an item.
/// </summary>
/// <param name="ItemId">Item id.</param>
/// <param name="HasTimeline">Whether scene data exists.</param>
/// <param name="Source">"face" or "manual".</param>
/// <param name="People">Cast.</param>
/// <param name="Appearances">On-screen spans.</param>
/// <param name="Analysis">On-demand analysis status, if any.</param>
public record XRayItemDto(
    Guid ItemId,
    bool HasTimeline,
    string? Source,
    IReadOnlyList<XRayPersonDto> People,
    IReadOnlyList<XRayAppearanceDto> Appearances,
    AnalysisStatus? Analysis);

/// <summary>
/// Items queued by a folder analysis.
/// </summary>
/// <param name="Path">Folder.</param>
/// <param name="ItemIds">Queued item ids.</param>
public record XRayFolderAnalysisDto(string Path, IReadOnlyList<Guid> ItemIds);

/// <summary>
/// A cast member.
/// </summary>
/// <param name="Id">Person item id.</param>
/// <param name="Name">Name.</param>
/// <param name="Role">Character.</param>
/// <param name="HasImage">Whether a primary image exists.</param>
public record XRayPersonDto(Guid Id, string Name, string? Role, bool HasImage);

/// <summary>
/// An on-screen span.
/// </summary>
/// <param name="PersonId">Person item id.</param>
/// <param name="StartTicks">Start.</param>
/// <param name="EndTicks">End (exclusive).</param>
public record XRayAppearanceDto(Guid PersonId, long StartTicks, long EndTicks);
