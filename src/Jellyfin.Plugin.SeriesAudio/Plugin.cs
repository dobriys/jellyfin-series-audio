using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SeriesAudio;

public sealed class PluginConfiguration : BasePluginConfiguration;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer)
    : BasePlugin<PluginConfiguration>(paths, serializer)
{
    public override string Name => "Series Audio";
    public override string Description => "Озвучка сезона для каждого пользователя, общая для устройств.";
    public override Guid Id => Guid.Parse("f8f6c53c-43c2-4448-a56c-e6d6a18436b4");
}
