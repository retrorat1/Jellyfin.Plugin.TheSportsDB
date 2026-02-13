namespace Jellyfin.Plugin.TheSportsDB.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ApiKey { get; set; } = "3";

    // Accept only valid, unique mappings for safety
    private List<LeagueMapping> _leagueMappings = new();
    public List<LeagueMapping> LeagueMappings 
    { 
        get => _leagueMappings;
        set
        {
            // Only accept valid, unique and numeric mappings
            _leagueMappings = value?
                .Where(m => !string.IsNullOrWhiteSpace(m.Name) && IsNumeric(m.LeagueId))
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList() ?? new();
        }
    }

    public PluginConfiguration() { }

    private static bool IsNumeric(string s) => !string.IsNullOrEmpty(s) && s.All(char.IsDigit);
}

public class LeagueMapping
{
    private string _leagueId = string.Empty;
    public string Name { get; set; } = string.Empty;
    
    public string LeagueId
    {
        get => _leagueId;
        set
        {
            // Enforce numeric only, empty if not valid
            _leagueId = (!string.IsNullOrEmpty(value) && value.All(char.IsDigit)) ? value : string.Empty;
        }
    }
}