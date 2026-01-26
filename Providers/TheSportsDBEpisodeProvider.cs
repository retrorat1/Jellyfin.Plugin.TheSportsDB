namespace Jellyfin.Plugin.TheSportsDB.Providers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Reflection;

public class TheSportsDBEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IRemoteImageProvider
{
    private readonly TheSportsDbClient _client;
    private readonly ILogger<TheSportsDBEpisodeProvider> _logger;

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
        return item is Episode;
    }

    public TheSportsDBEpisodeProvider(IHttpClientFactory httpClientFactory, ILogger<TheSportsDBEpisodeProvider> logger, ILogger<TheSportsDbClient> clientLogger)
    {
        _client = new TheSportsDbClient(httpClientFactory, clientLogger);
        _logger = logger;
        _logger.LogInformation("TheSportsDB: Episode Provider loaded.");
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        // Episode search is redundant usually as Jellyfin calls GetMetadata mostly directly for episodes
        // But if manual search is used:
        _logger.LogInformation("TheSportsDB: Searching events for {Name}", searchInfo.Name);
        var result = await _client.SearchEventsAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
        var list = new List<RemoteSearchResult>();

        if (result?.events != null)
        {
            foreach (var ev in result.events)
            {
                list.Add(new RemoteSearchResult
                {
                    Name = ev.strEvent,
                    ProviderIds = { { "TheSportsDB", ev.idEvent } },
                    ProductionYear = DateTime.TryParse(ev.dateEvent, out var date) ? date.Year : null,
                    ImageUrl = ev.strThumb,
                    PremiereDate = DateTime.TryParse(ev.dateEvent, out var dt) ? dt : null
                });
            }
        }
        
        return list;
    }

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        _logger.LogInformation("TheSportsDB: Getting episode metadata for {Name}", info.Name);

        Event? match = null;
        string? seriesName = null;

        // Try to derive Series Name from Path first, as we need it for League lookup and cleaning
        if (!string.IsNullOrEmpty(info.Path))
        {
            try 
            {
                var dir = System.IO.Path.GetDirectoryName(info.Path);
                if (dir != null)
                {
                    var folderName = System.IO.Path.GetFileName(dir);
                    
                    // Check if this is a Season folder (digits or "Season")
                    if (Regex.IsMatch(folderName, @"\d{4}|Season", RegexOptions.IgnoreCase))
                    {
                        var parent = System.IO.Path.GetDirectoryName(dir);
                        if (parent != null)
                        {
                            seriesName = System.IO.Path.GetFileName(parent);
                        }
                    }
                    else
                    {
                        // Maybe the folder itself is the Series Name (e.g. .../NHL/Game.mp4)
                        seriesName = folderName;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TheSportsDB: Failed to derive series name from path {Path}", info.Path);
            }
        }

        // Strategy 1: If we have multiple providers, maybe we have an ID?
        var eventId = info.GetProviderId("TheSportsDB");
        if (!string.IsNullOrEmpty(eventId))
        {
             // TODO: Lookup specific event by ID
        }

        // Strategy 2: Use Series ID + Season + Date/Name to find event
        var leagueId = info.SeriesProviderIds.GetValueOrDefault("TheSportsDB");
        
        // If we don't have league ID, we can't do exact season lookup easily.
        // We'll rely on info.SeriesName and info.Name
        
        if (!string.IsNullOrEmpty(leagueId))
        {
            // Resolve Season if needed
        }
        else if (!string.IsNullOrEmpty(seriesName))
        {
            // Fallback: Try to resolve League ID dynamically if missing from Series context
            // 'SeriesName' is not available on EpisodeInfo in some contexts, so we derive it from the path.
            // Expected structure: .../SeriesName/Season/Episode.mp4 OR .../SeriesName/Episode.mp4
            
            _logger.LogInformation("TheSportsDB: League ID missing. Attempting to resolve league from Path-Derived Series Name: {SeriesName}", seriesName);
            
            if (KnownLeagueIds.TryGetValue(seriesName, out var knownId))
            {
                 leagueId = knownId;
                 _logger.LogInformation("TheSportsDB: Resolved League ID from internal map: {LeagueId} for {SeriesName}", leagueId, seriesName);
            }
            else
            {
                // Try Local DB
                leagueId = await ResolveLeagueIdFromDbAsync(seriesName, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(leagueId))
                {
                    _logger.LogInformation("TheSportsDB: Resolved League ID from local DB: {LeagueId} for {SeriesName}", leagueId, seriesName);
                }
                else
                {
                    var leagueResult = await _client.SearchLeagueAsync(seriesName, cancellationToken).ConfigureAwait(false);
                    if (leagueResult?.countrys != null)
                    {
                         var l = leagueResult.countrys.FirstOrDefault();
                         if (l != null) 
                         {
                             leagueId = l.idLeague;
                             _logger.LogInformation("TheSportsDB: Resolved League ID: {LeagueId} for Series: {SeriesName}", leagueId, seriesName);
                         }
                    }
                    
                    if (string.IsNullOrEmpty(leagueId) && leagueResult?.leagues != null)
                    {
                         var l = leagueResult.leagues.FirstOrDefault();
                         if (l != null) 
                         {
                             leagueId = l.idLeague;
                             _logger.LogInformation("TheSportsDB: Resolved League ID: {LeagueId} for Series: {SeriesName}", leagueId, seriesName);
                         }
                    }
                }
            }
        }

        // Let's try Search by Name first as it is versatile
        var searchName = info.Name;
        
        // If file name is "NHL 2026-01-21 Dallas Stars vs Boston Bruins"
        // TSDB Event might be "Dallas Stars vs Boston Bruins"
        // We should try to clean the name.
        
        // Regex to remove "NHL YYYY-MM-DD " prefix?
        // Match: (League)? (YYYY-MM-DD)? (Home vs Away)
        
        var matchDate = MatchDate(info.Name);
        var seriesNameToStrip = seriesName;
        var cleanName = CleanName(info.Name, seriesNameToStrip);

        _logger.LogDebug("TheSportsDB: Cleaned name: {CleanName}, Date: {Date}", cleanName, matchDate);

        var searchResults = await _client.SearchEventsAsync(cleanName, cancellationToken).ConfigureAwait(false);
        if (searchResults?.events != null)
        {
            // Filter by date if we have one
            IEnumerable<Event> candidates = searchResults.events;
            if (matchDate.HasValue)
            {
                candidates = candidates.Where(e => DateTime.TryParse(e.dateEvent, out var d) && d.Date == matchDate.Value.Date);
            }
            
            // Filter by League if known
            if (!string.IsNullOrEmpty(leagueId))
            {
                 candidates = candidates.Where(e => e.idLeague == leagueId);
            }

            match = candidates.FirstOrDefault();
        }

        // Fallback: If no match by name, but we have a Date and League ID
        // This is useful for abbreviated filenames like "2026-01-22-EDM-PIT" which SearchEventsAsync won't match.
        // Fallback: If no match by name, but we have a Date and League ID
        // This is useful for abbreviated filenames like "2026-01-22-EDM-PIT" which SearchEventsAsync won't match.
        if (match == null && matchDate.HasValue)
        {
             // 1. Try Exact Date
             match = await FindMatchOnDateAsync(matchDate.Value, leagueId, cleanName, cancellationToken).ConfigureAwait(false);
             
             // 2. Try Next Day (UTC vs Local Time issue). E.g. File=24th (Sat), API=25th (Sun UTC)
             if (match == null)
             {
                 _logger.LogInformation("TheSportsDB: No match on exact date. Checking T+1 day ({Date}) for timezone offset.", matchDate.Value.AddDays(1));
                 match = await FindMatchOnDateAsync(matchDate.Value.AddDays(1), leagueId, cleanName, cancellationToken).ConfigureAwait(false);
             }
             
             // 3. Try Prev Day
             if (match == null)
             {
                 _logger.LogInformation("TheSportsDB: No match on T+1. Checking T-1 day ({Date}) for timezone offset.", matchDate.Value.AddDays(-1));
                 match = await FindMatchOnDateAsync(matchDate.Value.AddDays(-1), leagueId, cleanName, cancellationToken).ConfigureAwait(false);
             }
        }

        // Fallback: If no match by name, and we have LeagueID + Season
        // This is expensive (fetching all events for season), maybe skip for now unless requested.

        if (match == null)
        {
             return new MetadataResult<Episode>();
        }

        var ep = new Episode
        {
            Name = match.strEvent?.Trim(),
            Overview = match.strDescriptionEN?.Trim(),
            PremiereDate = DateTime.TryParse(match.dateEvent, out var d) ? d : null,
            ProductionYear = DateTime.TryParse(match.dateEvent, out var dy) ? dy.Year : null,
        };
        
        ep.SetProviderId("TheSportsDB", match.idEvent);

        if (DateTime.TryParse(match.strTime, out var t))
        {
            // Time is usually local? or UTC? TSDB uses GMT usually.
            // ep.PremiereDate might need time component.
        }

        // Map Production Year to Season Number if possible to assist grouping
        if (ep.ProductionYear.HasValue)
        {
            ep.ParentIndexNumber = ep.ProductionYear.Value;
        }

        var res = new MetadataResult<Episode>
        {
            Item = ep,
            HasMetadata = true
        };

        return res;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _client.GetImageResponseAsync(url, cancellationToken);
    }
    
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new[] { ImageType.Primary, ImageType.Backdrop };
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var list = new List<RemoteImageInfo>();
        var id = item.GetProviderId("TheSportsDB");

        if (!string.IsNullOrEmpty(id))
        {
            var result = await _client.GetEventAsync(id, cancellationToken).ConfigureAwait(false);
            var ev = result?.events?.FirstOrDefault();
            
            if (ev != null)
            {
                if (!string.IsNullOrEmpty(ev.strThumb))
                {
                    list.Add(new RemoteImageInfo
                    {
                        Url = ev.strThumb,
                        ProviderName = "TheSportsDB",
                        Type = ImageType.Primary
                    });
                }

                // Use Fanart as Backdrop if available
                if (!string.IsNullOrEmpty(ev.strFanart))
                {
                    list.Add(new RemoteImageInfo
                    {
                        Url = ev.strFanart,
                        ProviderName = "TheSportsDB",
                        Type = ImageType.Backdrop
                    });
                }
                else if (!string.IsNullOrEmpty(ev.strPoster))
                {
                    // Fallback to poster if no fanart, though poster is usually vertical. 
                    // Keeping it as backup or secondary might be okay, but typically Backdrops are 16:9.
                    // Let's stick to Fanart for Backdrop for now to avoid bad crops, 
                    // or maybe only use strFanart. 
                    // TheSportsDB often has strFanart for events.
                }
            }
        }
        
        return list;
    }

    private DateTime? MatchDate(string input)
    {
        // Try YYYY-MM-DD
        var m = Regex.Match(input, @"(\d{4}-\d{2}-\d{2})");
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return d;
        
        // Try DD-MM-YYYY
        m = Regex.Match(input, @"(\d{2}-\d{2}-\d{4})");
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d)) return d;

        return null;
    }
    
    private async Task<bool> CheckTeamIdMatch(string part, string? idHome, string? idAway, CancellationToken cancellationToken)
    {
        // Don't search for very short/common strings that are not likely teams unless they look like abbreviations (2-4 chars)
        if (part.Length < 2 || part.Length > 4) return false;

        // Strategy: Instead of searching for the abbreviation (which is unreliable),
        // fetch the actual teams involved in the event and check their abbreviations.
        
        var teamsToCheck = new[] { idHome, idAway };
        foreach (var teamId in teamsToCheck)
        {
            if (string.IsNullOrEmpty(teamId)) continue;

            // TODO: Add caching here to avoid spamming the API for the same team ID across multiple checks
            var teamResult = await _client.GetTeamAsync(teamId, cancellationToken).ConfigureAwait(false);
            var team = teamResult?.teams?.FirstOrDefault();
            
            if (team != null)
            {
                // Check strTeamShort
                if (string.Equals(team.strTeamShort, part, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                
                // Also check strTeam (full name) just in case
                if (team.strTeam.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        
        return false;
    }
    
    private async Task<Event?> FindMatchOnDateAsync(DateTime date, string? leagueId, string cleanName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("TheSportsDB: Trying lookup by Date {Date} and League {LeagueId}", date, leagueId ?? "Any");
        var dayResults = await _client.GetEventsByDayAsync(date, leagueId, cancellationToken).ConfigureAwait(false);
        
        if (dayResults?.events != null)
        {
             foreach (var ev in dayResults.events)
             {
                 // 1. Single Event Match (High Confidence if filtered by League)
                 if (dayResults.events.Count == 1 && !string.IsNullOrEmpty(leagueId))
                 {
                     _logger.LogInformation("TheSportsDB: Single event found for date/league. Accepting match: {Event}", ev.strEvent);
                     return ev;
                 }
                 
                 // 2. Name Containment Match
                 if (ev.strEvent.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase) >= 0)
                 {
                     return ev;
                 }
                 
                 // 3. Abbreviation / Parts Match
                 var parts = cleanName.Split(new[] { ' ', '-', 'v', 's', '.' }, StringSplitOptions.RemoveEmptyEntries);
                 
                 if (parts.Length >= 2 && !string.IsNullOrEmpty(ev.strHomeTeam) && !string.IsNullOrEmpty(ev.strAwayTeam))
                 {
                     var p1 = parts[0];
                     var p2 = parts[1];
                     
                     // Check Part 1 against Home OR Away
                     bool match1 = ev.strHomeTeam.StartsWith(p1, StringComparison.OrdinalIgnoreCase) || 
                                   ev.strAwayTeam.StartsWith(p1, StringComparison.OrdinalIgnoreCase);
                                   
                     // Check Part 2 against Home OR Away
                     bool match2 = ev.strHomeTeam.StartsWith(p2, StringComparison.OrdinalIgnoreCase) || 
                                   ev.strAwayTeam.StartsWith(p2, StringComparison.OrdinalIgnoreCase);
                                   
                     if (match1 && match2)
                     {
                         _logger.LogInformation("TheSportsDB: Abbreviation match found (StartsWith): {P1}/{P2} in {Home}/{Away}", p1, p2, ev.strHomeTeam, ev.strAwayTeam);
                         return ev;
                     }

                     // Advanced Lookup: If simple StartsWith failed, try to Resolve Team by Abbreviation
                     if (!match1) match1 = await CheckTeamIdMatch(p1, ev.idHomeTeam, ev.idAwayTeam, cancellationToken).ConfigureAwait(false);
                     if (!match2) match2 = await CheckTeamIdMatch(p2, ev.idHomeTeam, ev.idAwayTeam, cancellationToken).ConfigureAwait(false);

                     if (match1 && match2)
                     {
                         _logger.LogInformation("TheSportsDB: Abbreviation match found (TeamID Lookup): {P1}/{P2} matched Event {Event}", p1, p2, ev.strEvent);
                         return ev;
                     }
                 }
             }
        }
        return null;
    }
    private async Task<string?> ResolveLeagueIdFromDbAsync(string name, CancellationToken cancellationToken)
    {
        try 
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var pluginDir = System.IO.Path.GetDirectoryName(assemblyLocation);
            var dbPath = System.IO.Path.Combine(pluginDir!, "sports_leagues.db");
            
            if (!System.IO.File.Exists(dbPath)) return null;

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT league_id FROM sports_leagues WHERE league_name = $name COLLATE NOCASE
                UNION
                SELECT league_id FROM alternative_names WHERE alt_name = $name COLLATE NOCASE
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
            _logger.LogError(ex, "Failed to query local sports_leagues.db");
        }
        return null;
    }

    private string CleanName(string input, string? seriesName = null)
    {
        // Remove Dates
        var s = Regex.Replace(input, @"\d{4}-\d{2}-\d{2}", ""); // YYYY-MM-DD
        s = Regex.Replace(s, @"\d{2}-\d{2}-\d{4}", ""); // DD-MM-YYYY
        
        // Remove League Prefixes common in filename but not in Event Name
        // E.g. "NHL ", "EPL "
        if (!string.IsNullOrEmpty(seriesName))
        {
            s = s.Replace(seriesName, "", StringComparison.OrdinalIgnoreCase);
        }

        // Hardcoded fallbacks only if seriesName wasn't sufficient or provided
        s = s.Replace("NHL", "", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("EPL", "", StringComparison.OrdinalIgnoreCase);
        
        return s.Trim().Trim('-', ' ', '.');
    }
}
