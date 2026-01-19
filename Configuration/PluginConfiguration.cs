namespace Jellyfin.Plugin.TheSportsDB.Configuration;

using MediaBrowser.Model.Plugins;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ApiKey { get; set; } = "3";

    public PluginConfiguration()
    {
    }
}
