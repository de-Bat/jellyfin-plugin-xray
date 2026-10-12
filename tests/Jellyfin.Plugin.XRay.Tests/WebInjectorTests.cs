using Jellyfin.Plugin.XRay.Web;

namespace Jellyfin.Plugin.XRay.Tests;

public class WebInjectorTests
{
    private const string Html = "<html><body><div id=\"app\"></div></body></html>";

    [Fact]
    public void Inject_AddsTagBeforeBodyEnd_Once()
    {
        var once = WebInjector.Apply(Html, inject: true);
        var twice = WebInjector.Apply(once, inject: true);

        Assert.Contains(WebInjector.ScriptTag + "</body>", once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Remove_RestoresOriginal()
    {
        Assert.Equal(Html, WebInjector.Apply(WebInjector.Apply(Html, inject: true), inject: false));
    }

    [Fact]
    public void Inject_ReplacesOutdatedTag()
    {
        var old = Html.Replace("</body>", "<script data-xray-plugin defer src=\"../XRay/ClientScript?v=0.0.1\"></script></body>");

        var updated = WebInjector.Apply(old, inject: true);

        Assert.DoesNotContain("v=0.0.1", updated);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(updated, "data-xray-plugin"));
    }
}
