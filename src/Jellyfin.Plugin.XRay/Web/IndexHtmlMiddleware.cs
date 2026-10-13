using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Jellyfin.Plugin.XRay.Web;

/// <summary>
/// Serves jellyfin-web's index.html with the X-Ray script tag added in memory, so the overlay works even when
/// the web client folder is read-only (e.g. the official Docker image).
/// </summary>
public sealed class IndexHtmlMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IApplicationPaths _paths;
    private readonly IConfiguration _appConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexHtmlMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="configurationManager">Server configuration (for the base URL).</param>
    /// <param name="paths">Application paths.</param>
    /// <param name="appConfig">Startup configuration (whether the web client is hosted).</param>
    public IndexHtmlMiddleware(RequestDelegate next, IServerConfigurationManager configurationManager, IApplicationPaths paths, IConfiguration appConfig)
    {
        _next = next;
        _configurationManager = configurationManager;
        _paths = paths;
        _appConfig = appConfig;
    }

    /// <summary>
    /// Gets a value indicating whether a request is for jellyfin-web's index page.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Request path, including the base URL.</param>
    /// <param name="baseUrl">Server base URL ("" or e.g. "/jellyfin").</param>
    /// <returns>True for GET/HEAD of <c>{baseUrl}/web/</c> or <c>{baseUrl}/web/index.html</c>.</returns>
    public static bool IsIndexRequest(string method, string? path, string? baseUrl)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return false;
        }

        var web = (baseUrl ?? string.Empty).TrimEnd('/') + "/web/";
        return string.Equals(path, web, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, web + "index.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes index.html from <paramref name="webPath"/> with the script tag added or removed.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <param name="webPath">jellyfin-web folder.</param>
    /// <param name="inject">Whether the tag should be present.</param>
    /// <returns>False when the file could not be read; the request is then left to Jellyfin.</returns>
    public static async Task<bool> TryServeAsync(HttpContext context, string webPath, bool inject)
    {
        string html;
        try
        {
            html = await File.ReadAllTextAsync(Path.Combine(webPath, "index.html"), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var body = Encoding.UTF8.GetBytes(WebInjector.Apply(html, inject));
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = body.Length;
        response.Headers.CacheControl = "no-cache";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>Task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!string.IsNullOrEmpty(_paths.WebPath)
            && _appConfig.HostWebClient()
            && IsIndexRequest(context.Request.Method, context.Request.Path.Value, _configurationManager.GetNetworkConfiguration().BaseUrl)
            && await TryServeAsync(context, _paths.WebPath, Plugin.Instance?.Configuration.InjectClientScript ?? true).ConfigureAwait(false))
        {
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Puts <see cref="IndexHtmlMiddleware"/> in front of Jellyfin's pipeline.
/// </summary>
public sealed class IndexHtmlStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.UseMiddleware<IndexHtmlMiddleware>();
            next(app);
        };
}
