using Jellyfin.Plugin.XRay.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.XRay.Tests;

public sealed class XRayLogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xray-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Wrap_WritesFormattedMessagesToDailyFile()
    {
        var file = new XRayLogFile(_dir);
        var logger = file.Wrap(NullLogger<XRayLogFileTests>.Instance);

        logger.LogInformation("X-Ray: {Count} items to analyze", 3);
        logger.LogError(new InvalidOperationException("boom"), "X-Ray: failed {Name}", "Heat");
        logger.LogTrace("not written");

        var text = File.ReadAllText(file.GetPath(DateTime.Now));
        Assert.Contains("[INF] XRayLogFileTests: X-Ray: 3 items to analyze", text);
        Assert.Contains("[ERR] XRayLogFileTests: X-Ray: failed Heat", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
        Assert.DoesNotContain("not written", text);
    }

    [Fact]
    public void Write_DeletesFilesOlderThanKeepDays()
    {
        var file = new XRayLogFile(_dir);
        var today = new DateTime(2026, 10, 13, 12, 0, 0);
        Directory.CreateDirectory(_dir);
        var old = file.GetPath(today.AddDays(-XRayLogFile.KeepDays));
        var recent = file.GetPath(today.AddDays(-(XRayLogFile.KeepDays - 1)));
        var unrelated = Path.Combine(_dir, "log_20200101.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.WriteAllText(unrelated, "jellyfin");

        file.Write(today, LogLevel.Information, "Test", "hello", null);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
        Assert.Contains("hello", File.ReadAllText(file.GetPath(today)));
    }
}
