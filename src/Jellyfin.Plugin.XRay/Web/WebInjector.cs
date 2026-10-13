using System.Text.RegularExpressions;
using Jellyfin.Plugin.XRay.Logging;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Web;

/// <summary>
/// Adds (or removes) the X-Ray client script tag in jellyfin-web's index.html.
/// </summary>
public sealed partial class WebInjector : IHostedService
{
    private const string Marker = "data-xray-plugin";

    private readonly IApplicationPaths _paths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebInjector"/> class.
    /// </summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="logFile">X-Ray log file.</param>
    public WebInjector(IApplicationPaths paths, ILogger<WebInjector> logger, XRayLogFile logFile)
    {
        _paths = paths;
        _logger = logFile.Wrap(logger);
    }

    /// <summary>
    /// Gets the script tag that loads the client.
    /// </summary>
    public static string ScriptTag
        => $"<script {Marker} defer src=\"../XRay/ClientScript?v={typeof(Plugin).Assembly.GetName().Version}\"></script>";

    /// <summary>
    /// Returns html with exactly one current X-Ray script tag (or none).
    /// </summary>
    /// <param name="html">index.html content.</param>
    /// <param name="inject">Whether the tag should be present.</param>
    /// <returns>Updated html.</returns>
    public static string Apply(string html, bool inject)
    {
        var cleaned = ExistingTag().Replace(html, string.Empty);
        if (!inject)
        {
            return cleaned;
        }

        var idx = cleaned.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? cleaned + ScriptTag : cleaned.Insert(idx, ScriptTag);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Update(Plugin.Instance?.Configuration.InjectClientScript ?? true);
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += (_, _) => Update(Plugin.Instance.Configuration.InjectClientScript);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Updates index.html.
    /// </summary>
    /// <param name="inject">Whether the tag should be present.</param>
    public void Update(bool inject)
    {
        var path = string.IsNullOrEmpty(_paths.WebPath) ? null : Path.Combine(_paths.WebPath, "index.html");
        if (path is null || !File.Exists(path))
        {
            _logger.LogInformation("X-Ray: jellyfin-web index.html not found; load {Script} manually", ScriptTag);
            return;
        }

        try
        {
            var html = File.ReadAllText(path);
            var updated = Apply(html, inject);
            if (updated != html)
            {
                File.WriteAllText(path, updated);
                _logger.LogInformation("X-Ray: {Action} client script in {Path}", inject ? "injected" : "removed", path);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not a problem: IndexHtmlMiddleware adds the tag when Jellyfin serves the page.
            _logger.LogDebug(
                "X-Ray: {Path} is read-only ({Message}); the script tag is added when the page is served instead",
                path,
                ex.Message);
        }
    }

    [GeneratedRegex("<script data-xray-plugin[^>]*></script>", RegexOptions.IgnoreCase)]
    private static partial Regex ExistingTag();
}
