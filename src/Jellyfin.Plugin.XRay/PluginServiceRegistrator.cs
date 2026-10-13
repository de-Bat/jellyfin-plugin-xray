using Jellyfin.Plugin.XRay.Analysis;
using Jellyfin.Plugin.XRay.Logging;
using Jellyfin.Plugin.XRay.Runtime;
using Jellyfin.Plugin.XRay.Storage;
using Jellyfin.Plugin.XRay.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// Registers X-Ray services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp => new XRayLogFile(sp.GetRequiredService<IApplicationPaths>().LogDirectoryPath));
        serviceCollection.AddSingleton<ModelManager>();
        serviceCollection.AddSingleton<TimelineStore>();
        serviceCollection.AddSingleton<XRayAnalyzer>();
        serviceCollection.AddSingleton<AnalysisQueue>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<AnalysisQueue>());
        serviceCollection.AddHostedService<WebInjector>();
        serviceCollection.AddTransient<IStartupFilter, IndexHtmlStartupFilter>();
    }
}
