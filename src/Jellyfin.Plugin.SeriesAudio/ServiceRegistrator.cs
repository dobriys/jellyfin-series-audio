using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesAudio;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton(sp => new PreferenceStore(
            Path.Combine(sp.GetRequiredService<IServerApplicationPaths>().DataPath, "series-audio", "preferences.json"),
            sp.GetRequiredService<ILogger<PreferenceStore>>()));

        var original = services.Last(d => d.ServiceType == typeof(IMediaSourceManager));
        if (original.Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Series Audio requires Jellyfin 12.4's singleton media source service.");
        services.Remove(original);
        services.AddKeyedSingleton<IMediaSourceManager>("SeriesAudio.Original", (sp, _) =>
            (IMediaSourceManager)(original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
        services.AddSingleton<IMediaSourceManager>(sp => new PreferredMediaSources(
            sp.GetRequiredKeyedService<IMediaSourceManager>("SeriesAudio.Original"),
            sp.GetRequiredService<PreferenceStore>()));

        services.AddSingleton<StartupAudioCorrection>();
        services.AddSingleton<IEventConsumer<PlaybackStartEventArgs>>(sp => sp.GetRequiredService<StartupAudioCorrection>());
        services.AddSingleton<IEventConsumer<PlaybackProgressEventArgs>>(sp => sp.GetRequiredService<StartupAudioCorrection>());
    }
}
