namespace Jellyfin.Plugin.TheSportsDB.Configuration;

using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ApiKey { get; set; } = "3";

    public List<LeagueMapping> LeagueMappings { get; set; } = new();

    public PluginConfiguration()
    {
    }
}

public class LeagueMapping
{
    public string Name { get; set; } = string.Empty;
    public string LeagueId { get; set; } = string.Empty;
}
