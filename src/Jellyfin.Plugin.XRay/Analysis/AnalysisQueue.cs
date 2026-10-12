using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// State of an on-demand analysis.
/// </summary>
public class AnalysisStatus
{
    /// <summary>
    /// Gets or sets the state: Queued, Running, Done or Failed.
    /// </summary>
    public string State { get; set; } = "Queued";

    /// <summary>
    /// Gets or sets progress, 0-100.
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets an error message.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Runs on-demand analyses one at a time in the background.
/// </summary>
public sealed class AnalysisQueue : BackgroundService
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, AnalysisStatus> _status = new();
    private readonly XRayAnalyzer _analyzer;
    private readonly ILogger<AnalysisQueue> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisQueue"/> class.
    /// </summary>
    /// <param name="analyzer">Analyzer.</param>
    /// <param name="logger">Logger.</param>
    public AnalysisQueue(XRayAnalyzer analyzer, ILogger<AnalysisQueue> logger)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    /// <summary>
    /// Queues an item unless it is already queued or running.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Its status.</returns>
    public AnalysisStatus Enqueue(Guid itemId)
    {
        var created = false;
        var status = _status.AddOrUpdate(
            itemId,
            _ =>
            {
                created = true;
                return new AnalysisStatus();
            },
            (_, existing) =>
            {
                if (existing.State is "Queued" or "Running")
                {
                    return existing;
                }

                created = true;
                return new AnalysisStatus();
            });
        if (created)
        {
            _channel.Writer.TryWrite(itemId);
        }

        return status;
    }

    /// <summary>
    /// Gets the status of an item's on-demand analysis.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Status or null.</returns>
    public AnalysisStatus? GetStatus(Guid itemId) => _status.GetValueOrDefault(itemId);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var itemId in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var status = _status[itemId];
            if (status.State != "Queued")
            {
                continue;
            }

            status.State = "Running";
            try
            {
                await _analyzer.AnalyzeAsync(itemId, new Progress<double>(p => status.Progress = p), stoppingToken).ConfigureAwait(false);
                status.Progress = 100;
                status.State = "Done";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "X-Ray: analysis of {ItemId} failed", itemId);
                status.State = "Failed";
                status.Error = ex.Message;
            }

            if (_channel.Reader.Count == 0)
            {
                _analyzer.ReleaseEngine();
            }
        }
    }
}
