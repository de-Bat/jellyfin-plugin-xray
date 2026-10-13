using System.Text;
using Jellyfin.Plugin.XRay.Web;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.XRay.Tests;

public sealed class IndexHtmlMiddlewareTests : IDisposable
{
    private const string Html = "<html><body><div id=\"app\"></div></body></html>";

    private readonly string _webPath = Path.Combine(Path.GetTempPath(), "xray-web-" + Guid.NewGuid().ToString("N"));

    public IndexHtmlMiddlewareTests()
    {
        Directory.CreateDirectory(_webPath);
        File.WriteAllText(Path.Combine(_webPath, "index.html"), Html);
    }

    public void Dispose() => Directory.Delete(_webPath, true);

    [Theory]
    [InlineData("GET", "/web/", "", true)]
    [InlineData("GET", "/web/index.html", "", true)]
    [InlineData("HEAD", "/web/index.html", "", true)]
    [InlineData("GET", "/jellyfin/web/index.html", "/jellyfin", true)]
    [InlineData("GET", "/jellyfin/web/", "/jellyfin/", true)]
    [InlineData("GET", "/web/index.html", "/jellyfin", false)]
    [InlineData("GET", "/web/main.bundle.js", "", false)]
    [InlineData("GET", "/web", "", false)]
    [InlineData("POST", "/web/index.html", "", false)]
    public void IsIndexRequest_MatchesOnlyTheIndexPage(string method, string path, string baseUrl, bool expected)
    {
        Assert.Equal(expected, IndexHtmlMiddleware.IsIndexRequest(method, path, baseUrl));
    }

    [Fact]
    public async Task TryServe_AddsTagWithoutTouchingFile()
    {
        var context = NewContext("GET");

        Assert.True(await IndexHtmlMiddleware.TryServeAsync(context, _webPath, inject: true));

        var body = ReadBody(context);
        Assert.Contains(WebInjector.ScriptTag + "</body>", body);
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(Html, File.ReadAllText(Path.Combine(_webPath, "index.html")));
    }

    [Fact]
    public async Task TryServe_RemovesTagWhenDisabled()
    {
        File.WriteAllText(Path.Combine(_webPath, "index.html"), WebInjector.Apply(Html, inject: true));
        var context = NewContext("GET");

        Assert.True(await IndexHtmlMiddleware.TryServeAsync(context, _webPath, inject: false));

        Assert.Equal(Html, ReadBody(context));
    }

    [Fact]
    public async Task TryServe_Head_SendsNoBody()
    {
        var context = NewContext("HEAD");

        Assert.True(await IndexHtmlMiddleware.TryServeAsync(context, _webPath, inject: true));

        Assert.True(context.Response.ContentLength > 0);
        Assert.Equal(string.Empty, ReadBody(context));
    }

    [Fact]
    public async Task TryServe_MissingFile_LeavesRequestToJellyfin()
    {
        var context = NewContext("GET");

        Assert.False(await IndexHtmlMiddleware.TryServeAsync(context, Path.Combine(_webPath, "missing"), inject: true));
    }

    private static DefaultHttpContext NewContext(string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
}
