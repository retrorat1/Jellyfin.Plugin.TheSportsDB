namespace Jellyfin.Plugin.TheSportsDB.Providers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Reflection;

public class TheSportsDBMetadataProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IRemoteImageProvider
{
    private readonly TheSportsDbClient _client;
    private readonly ILogger<TheSportsDBMetadataProvider> _logger;

    private static readonly Dictionary<string, string> KnownLeagueIds = new(StringComparer.OrdinalIgnoreCase)
    {
        { "NHL", "4380" },
        { "EPL", "4328" },
        { "NFL", "4391" },
        { "NBA", "4387" },
        { "MLB", "4424" },
        { "UFC", "4463" } 
    };

    public string Name => "TheSportsDB";
    
    public bool Supports(BaseItem item)
    {
        return item is Series;
    }

    public TheSportsDBMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<TheSportsDBMetadataProvider> logger, ILogger<TheSportsDbClient> clientLogger)
    {
        _client = new TheSportsDbClient(httpClientFactory, clientLogger);
        _logger = logger;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        _logger.LogInformation("TheSportsDB: Searching for {Name}", searchInfo.Name);

        var result = await _client.SearchLeagueAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
        var list = new List<RemoteSearchResult>();

        if (result == null) 
        {
             _logger.LogWarning("TheSportsDB: Search result for {Name} was null", searchInfo.Name);
             return list;
        }

        if (result.countrys != null)
        {
            foreach (var league in result.countrys)
            {
                list.Add(new RemoteSearchResult
                {
                    Name = league.strLeague,
                    ProviderIds = { { "TheSportsDB", league.idLeague } },
                    ProductionYear = int.TryParse(league.intFormedYear, out var year) ? year : null,
                    ImageUrl = league.strBadge ?? league.strLogo
                });
            }
        }
        
        if (result.leagues != null)
        {
             foreach (var league in result.leagues)
            {
                // De-duplicate
                if (!list.Any(x => x.ProviderIds.ContainsKey("TheSportsDB") && x.ProviderIds["TheSportsDB"] == league.idLeague))
                {
                     list.Add(new RemoteSearchResult
                    {
                        Name = league.strLeague,
                        ProviderIds = { { "TheSportsDB", league.idLeague } },
                        ProductionYear = int.TryParse(league.intFormedYear, out var year) ? year : null,
                        ImageUrl = league.strBadge ?? league.strLogo
                    });
                }
            }
        }

        _logger.LogInformation("TheSportsDB: Found {Count} results for {Name}", list.Count, searchInfo.Name);

        if (list.Count == 0)
        {
            // 1. Check User-Defined Mappings (Fastest & User Preference)
            var config = Plugin.Instance?.Configuration;
            if (config != null && config.LeagueMappings != null)
            {
                var mapping = config.LeagueMappings.FirstOrDefault(m => string.Equals(m.Name, searchInfo.Name, StringComparison.OrdinalIgnoreCase));
                if (mapping != null && !string.IsNullOrEmpty(mapping.LeagueId))
                {
                     _logger.LogInformation("TheSportsDB: Found {Name} in User Mappings with ID {Id}", searchInfo.Name, mapping.LeagueId);
                     list.Add(new RemoteSearchResult
                     {
                         Name = searchInfo.Name,
                         ProviderIds = { { "TheSportsDB", mapping.LeagueId } }
                     });
                     return list; // Return immediately if user mapping found
                }
            }

            // 2. Fallback: Check internal map
            if (KnownLeagueIds.TryGetValue(searchInfo.Name, out var knownId))
            {
                 _logger.LogInformation("TheSportsDB: Found {Name} in internal map with ID {Id}", searchInfo.Name, knownId);
                 list.Add(new RemoteSearchResult
                 {
                     Name = searchInfo.Name,
                     ProviderIds = { { "TheSportsDB", knownId } }
                 });
            }
            else
            {
                // Fallback: Check local DB
                var dbId = await ResolveLeagueIdFromDbAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(dbId))
                {
                    _logger.LogInformation("TheSportsDB: Found {Name} in local DB with ID {Id}", searchInfo.Name, dbId);
                    list.Add(new RemoteSearchResult
                    {
                        Name = searchInfo.Name,
                        ProviderIds = { { "TheSportsDB", dbId } }
                    });
                }
            }
        }

        return list;
    }

    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        _logger.LogInformation("TheSportsDB: Getting metadata for {Name}", info.Name);

        var id = info.GetProviderId("TheSportsDB");
        if (string.IsNullOrEmpty(id))
        {
             var searchResults = await GetSearchResults(info, cancellationToken).ConfigureAwait(false);
             var first = searchResults.FirstOrDefault();
             if (first != null)
             {
                 id = first.ProviderIds["TheSportsDB"];
             }
        }

        if (string.IsNullOrEmpty(id))
        {
            _logger.LogWarning("TheSportsDB: No ID found for {Name}", info.Name);
            return new MetadataResult<Series>();
        }

        var result = await _client.GetLeagueAsync(id, cancellationToken).ConfigureAwait(false);
        var league = result?.leagues?.FirstOrDefault();

        if (league == null)
        {
            _logger.LogWarning("TheSportsDB: No league found for ID {Id}", id);
            return new MetadataResult<Series>();
        }

        var series = new Series
        {
            Name = league.strLeague,
            Overview = league.strDescriptionEN,
            ExternalId = league.idLeague,
            ProductionYear = int.TryParse(league.intFormedYear, out var year) ? year : null,
            HomePageUrl = string.IsNullOrEmpty(league.strWebsite) ? null : (!league.strWebsite.StartsWith("http") ? "http://" + league.strWebsite : league.strWebsite)
        };

        series.SetProviderId("TheSportsDB", league.idLeague);

        return new MetadataResult<Series>
        {
            Item = series,
            HasMetadata = true
        };
    }

    public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("TheSportsDB: Requesting image {Url}", url);
        return await _client.GetImageResponseAsync(url, cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new[] { ImageType.Primary, ImageType.Backdrop };
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var list = new List<RemoteImageInfo>();
        var id = item.GetProviderId("TheSportsDB");

        if (string.IsNullOrEmpty(id))
        {
            return list;
        }

        var result = await _client.GetLeagueAsync(id, cancellationToken).ConfigureAwait(false);
        var league = result?.leagues?.FirstOrDefault();

        if (league != null)
        {
             if (!string.IsNullOrEmpty(league.strBadge))
                list.Add(new RemoteImageInfo { Url = league.strBadge, Type = ImageType.Primary, ProviderName = "TheSportsDB" });
             else if (!string.IsNullOrEmpty(league.strLogo))
                 list.Add(new RemoteImageInfo { Url = league.strLogo, Type = ImageType.Primary, ProviderName = "TheSportsDB" });
            
            if (!string.IsNullOrEmpty(league.strPoster))
                list.Add(new RemoteImageInfo { Url = league.strPoster, Type = ImageType.Backdrop, ProviderName = "TheSportsDB" });
        }
        
        return list;
    }

    private async Task<string?> ResolveLeagueIdFromDbAsync(string name, CancellationToken cancellationToken)
    {
        try 
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var pluginDir = System.IO.Path.GetDirectoryName(assemblyLocation);
            var dbPath = System.IO.Path.Combine(pluginDir!, "sports_resolver.db");
            
            if (!System.IO.File.Exists(dbPath)) return null;

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            // Search 'leagues' for direct match, use 'teams' or 'league_aliases' for alt names
            command.CommandText = @"
                SELECT id FROM leagues WHERE name = $name COLLATE NOCASE
                UNION
                SELECT league_id FROM teams WHERE name = $name COLLATE NOCASE OR short_name = $name COLLATE NOCASE
                UNION
                SELECT league_id FROM league_aliases WHERE alias = $name COLLATE NOCASE
                LIMIT 1";
            command.Parameters.AddWithValue("$name", name);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result != null && result != DBNull.Value)
            {
                return result.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query local sports_resolver.db for league id");
        }
        return null;
    }
}
