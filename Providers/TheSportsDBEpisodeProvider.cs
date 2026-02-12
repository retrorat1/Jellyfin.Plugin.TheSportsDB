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
        
        // Try to resolve League ID if we don't have it yet
        if (string.IsNullOrEmpty(leagueId) && !string.IsNullOrEmpty(seriesName))
        {
            // Fallback: Try to resolve League ID dynamically if missing from Series context
            // 'SeriesName' is not available on EpisodeInfo in some contexts, so we derive it from the path.
            // Expected structure: .../SeriesName/Season/Episode.mp4 OR .../SeriesName/Episode.mp4
            
            _logger.LogInformation("TheSportsDB: League ID missing. Attempting to resolve league from Path-Derived Series Name: {SeriesName}", seriesName);
            
            // 0. Check User Mappings
            var config = Plugin.Instance?.Configuration;
            if (config != null && config.LeagueMappings != null)
            {
                var mapping = config.LeagueMappings.FirstOrDefault(m => string.Equals(m.Name, seriesName, StringComparison.OrdinalIgnoreCase));
                if (mapping != null && !string.IsNullOrEmpty(mapping.LeagueId))
                {
                     leagueId = mapping.LeagueId;
                     _logger.LogInformation("TheSportsDB: Resolved League ID from User Mappings: {LeagueId} for {SeriesName}", leagueId, seriesName);
                }
            }

            // 1. Internal Map
            if (string.IsNullOrEmpty(leagueId) && KnownLeagueIds.TryGetValue(seriesName, out var knownId))
            {
                 leagueId = knownId;
                 _logger.LogInformation("TheSportsDB: Resolved League ID from internal map: {LeagueId} for {SeriesName}", leagueId, seriesName);
            }

            // 2. Local DB
            if (string.IsNullOrEmpty(leagueId))
            {
                leagueId = await ResolveLeagueIdFromDbAsync(seriesName, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(leagueId))
                {
                    _logger.LogInformation("TheSportsDB: Resolved League ID from local DB: {LeagueId} for {SeriesName}", leagueId, seriesName);
                }
            }

            // 3. API Search (Last Resort)
            if (string.IsNullOrEmpty(leagueId))
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

        // Determine the search name - use filename from path if info.Name is too generic
        var searchName = info.Name;
        
        // If info.Name is the same as seriesName or very short, extract actual filename from path
        if (!string.IsNullOrEmpty(info.Path) && 
            (!string.IsNullOrEmpty(seriesName) && 
             string.Equals(info.Name, seriesName, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var filename = System.IO.Path.GetFileNameWithoutExtension(info.Path);
                if (!string.IsNullOrEmpty(filename))
                {
                    _logger.LogInformation("TheSportsDB: info.Name '{Name}' matches series name. Using filename from path: '{Filename}'", info.Name, filename);
                    searchName = filename;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TheSportsDB: Failed to extract filename from path {Path}", info.Path);
            }
        }
        
        // If file name is "NHL 2026-01-21 Dallas Stars vs Boston Bruins"
        // TSDB Event might be "Dallas Stars vs Boston Bruins"
        // We should try to clean the name.
        
        // Regex to remove "NHL YYYY-MM-DD " prefix?
        // Match: (League)? (YYYY-MM-DD)? (Home vs Away)
        
        var matchDate = MatchDate(searchName);
        var seriesNameToStrip = seriesName;
        var cleanName = CleanName(searchName, seriesNameToStrip);
        var expandedName = await ExpandAbbreviationsAsync(cleanName, leagueId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("TheSportsDB: Search name: '{SearchName}', Cleaned: '{CleanName}', Expanded: '{ExpandedName}', Date: {Date}", searchName, cleanName, expandedName, matchDate);

        var searchResults = await _client.SearchEventsAsync(expandedName, cancellationToken).ConfigureAwait(false);
        if (searchResults?.events != null)
        {
            // Filter by date if we have one
            IEnumerable<Event> candidates = searchResults.events;
            if (matchDate.HasValue)
            {
                candidates = candidates.Where(e => 
                    DateTime.TryParse(e.dateEvent, out var d) && 
                    d.Date >= matchDate.Value.AddDays(-1).Date && 
                    d.Date <= matchDate.Value.AddDays(1).Date);
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

        // Try YYYY MM DD (e.g. 2026 02 08)
        m = Regex.Match(input, @"(\d{4}) (\d{2}) (\d{2})");
        if (m.Success)
        {
             if (DateTime.TryParse($"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", out d)) return d;
        }

        // Try DD MM (e.g. 08 02) - Handle ambiguity by trying both interpretations
        m = Regex.Match(input, @"\b(\d{2}) (\d{2})(?!\d)");
        if (m.Success)
        {
             // Look for year separately
             var yMatch = Regex.Match(input, @"\b(20\d{2})\b");
             int year = DateTime.Now.Year;
             if (yMatch.Success && int.TryParse(yMatch.Groups[1].Value, out var y)) year = y;
             
             int val1 = int.Parse(m.Groups[1].Value);
             int val2 = int.Parse(m.Groups[2].Value);
             
             DateTime? ddMmResult = null;
             DateTime? mmDdResult = null;
             
             // Try DD-MM-YYYY interpretation
             if (val1 >= 1 && val1 <= 31 && val2 >= 1 && val2 <= 12)
             {
                 if (DateTime.TryParseExact($"{m.Groups[1].Value}-{m.Groups[2].Value}-{year}", "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d))
                 {
                     ddMmResult = d;
                 }
             }
             
             // Try MM-DD-YYYY interpretation
             if (val2 >= 1 && val2 <= 31 && val1 >= 1 && val1 <= 12)
             {
                 if (DateTime.TryParseExact($"{m.Groups[2].Value}-{m.Groups[1].Value}-{year}", "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d))
                 {
                     mmDdResult = d;
                 }
             }
             
             // Return the most reasonable date:
             // 1. Prefer dates not too far in the future (within 7 days)
             // 2. If both valid, prefer the one closer to today
             var now = DateTime.Now;
             var futureLimit = now.AddDays(7);
             
             if (ddMmResult.HasValue && mmDdResult.HasValue)
             {
                 // Both are valid, choose the one closer to today
                 var ddDiff = Math.Abs((ddMmResult.Value - now).TotalDays);
                 var mmDiff = Math.Abs((mmDdResult.Value - now).TotalDays);
                 return ddDiff <= mmDiff ? ddMmResult.Value : mmDdResult.Value;
             }
             
             if (ddMmResult.HasValue && ddMmResult.Value <= futureLimit)
             {
                 return ddMmResult.Value;
             }
             
             if (mmDdResult.HasValue && mmDdResult.Value <= futureLimit)
             {
                 return mmDdResult.Value;
             }
             
             // If we have any valid result, return it even if it's in the future
             return ddMmResult ?? mmDdResult;
        }

        return null;
    }
    
    private static readonly Dictionary<string, string> TeamAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // NHL
        { "ANA", "Anaheim Ducks" }, { "BOS", "Boston Bruins" }, { "BUF", "Buffalo Sabres" },
        { "CGY", "Calgary Flames" }, { "CAR", "Carolina Hurricanes" }, { "CHI", "Chicago Blackhawks" },
        { "COL", "Colorado Avalanche" }, { "CBJ", "Columbus Blue Jackets" }, { "DAL", "Dallas Stars" },
        { "DET", "Detroit Red Wings" }, { "EDM", "Edmonton Oilers" }, { "FLA", "Florida Panthers" },
        { "LAK", "Los Angeles Kings" }, { "MIN", "Minnesota Wild" }, { "MTL", "Montreal Canadiens" },
        { "NSH", "Nashville Predators" }, { "NJD", "New Jersey Devils" }, { "NYI", "New York Islanders" },
        { "NYR", "New York Rangers" }, { "OTT", "Ottawa Senators" }, { "PHI", "Philadelphia Flyers" },
        { "PIT", "Pittsburgh Penguins" }, { "SJS", "San Jose Sharks" }, { "SEA", "Seattle Kraken" },
        { "STL", "St. Louis Blues" }, { "TBL", "Tampa Bay Lightning" }, { "TOR", "Toronto Maple Leafs" },
        { "UTA", "Utah Hockey Club" }, { "VAN", "Vancouver Canucks" }, { "VGK", "Vegas Golden Knights" },
        { "WSH", "Washington Capitals" }, { "WPG", "Winnipeg Jets" },
        // NFL (Examples)
        { "ARI", "Arizona Cardinals" }, { "ATL", "Atlanta Falcons" }, { "KKC", "Kansas City Chiefs" },
        { "GBP", "Green Bay Packers" }, { "NEP", "New England Patriots" }
    };

    private async Task<bool> CheckTeamIdMatch(string part, string? idHome, string? idAway, CancellationToken cancellationToken)
    {
        // Don't search for very short/common strings that are not likely teams unless they look like abbreviations (2-4 chars)
        if (part.Length < 2 || part.Length > 4) return false;

        var teamsToCheck = new[] { idHome, idAway };
        foreach (var teamId in teamsToCheck)
        {
            if (string.IsNullOrEmpty(teamId)) continue;

            // TODO: Add caching here.
            var teamResult = await _client.GetTeamAsync(teamId, cancellationToken).ConfigureAwait(false);
            var team = teamResult?.teams?.FirstOrDefault();
            
            if (team != null)
            {
                // 1. Check strTeamShort (API provided abbreviation)
                if (string.Equals(team.strTeamShort, part, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                
                // 2. Check internal Abbreviation Map
                if (TeamAbbreviations.TryGetValue(part, out var fullTeamName))
                {
                    // Check if the team's full name matches our mapped name
                    // We use Contains or fuzzy match because "St. Louis Blues" might be "St Louis Blues" in DB?
                    // But StartsWith is usually safe. 
                    if (team.strTeam.Replace(".", "").StartsWith(fullTeamName.Replace(".", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // 3. Check strTeam (full name) starts with part (e.g. Part="Liverpool", Team="Liverpool FC")
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
             var events = dayResults.events;
             if (!string.IsNullOrEmpty(leagueId))
             {
                 events = events.Where(e => e.idLeague == leagueId).ToList();
             }

             _logger.LogInformation("TheSportsDB: Found {Count} events on {Date}. Checking for match against '{CleanName}'", events.Count, date, cleanName);

             foreach (var ev in events)
             {
                 // 1. Single Event Match (High Confidence if filtered by League)
                 if (events.Count == 1 && !string.IsNullOrEmpty(leagueId))
                 {
                     _logger.LogInformation("TheSportsDB: Single event found for date/league. Accepting match: {Event}", ev.strEvent);
                     return ev;
                 }
                 
                 // 2. Name Containment Match
                 if (ev.strEvent.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase) >= 0)
                 {
                     _logger.LogInformation("TheSportsDB: Name containment match: '{CleanName}' in '{Event}'", cleanName, ev.strEvent);
                     return ev;
                 }
                 
                 // 3. Abbreviation / Parts Match
                 // Use string separators to avoid splitting words like "Kings" on 's' or "Avalanche" on 'v'
                 var parts = cleanName.Split(new[] { " vs ", " Vs ", " VS ", " v ", " V ", "-", ".", " " }, StringSplitOptions.RemoveEmptyEntries);
                 
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

                     _logger.LogDebug("TheSportsDB: Basic prefix match failed for {P1}/{P2} against {Home}/{Away}. Checking IDs...", p1, p2, ev.strHomeTeam, ev.strAwayTeam);

                     // Advanced Lookup: If simple StartsWith failed, try to Resolve Team by Abbreviation
                     if (!match1) match1 = await CheckTeamIdMatch(p1, ev.idHomeTeam, ev.idAwayTeam, cancellationToken).ConfigureAwait(false);
                     if (!match2) match2 = await CheckTeamIdMatch(p2, ev.idHomeTeam, ev.idAwayTeam, cancellationToken).ConfigureAwait(false);

                     if (match1 && match2)
                     {
                         _logger.LogInformation("TheSportsDB: Abbreviation match found (TeamID Lookup): {P1}/{P2} matched Event {Event}", p1, p2, ev.strEvent);
                         return ev;
                     }
                     else 
                     {
                         _logger.LogDebug("TheSportsDB: Failed to match {P1}/{P2} against Event {Event} (Match1: {M1}, Match2: {M2})", p1, p2, ev.strEvent, match1, match2);
                     }
                 }
             }
        }
        else
        {
            _logger.LogInformation("TheSportsDB: No events returned by API for Date {Date}", date);
        }

        return null;
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
            // Search 'leagues' for direct match, or 'teams' to determine league from a team name/abbr
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

    private string CleanName(string input, string? seriesName = null)
    {
        // Remove Dates (YYYY-MM-DD)
        var s = Regex.Replace(input, @"\d{4}-\d{2}-\d{2}", ""); 
        s = Regex.Replace(s, @"\d{2}-\d{2}-\d{4}", ""); 
        s = Regex.Replace(s, @"\d{4}-\d{4}", ""); // Season ranges
        s = Regex.Replace(s, @"\b20\d{2}\b", ""); // Standalone Year (start/end/space-bounded)
        
        // Remove League Prefixes common in filename but not in Event Name
        // E.g. "NHL ", "EPL "
        if (!string.IsNullOrEmpty(seriesName))
        {
            s = s.Replace(seriesName, "", StringComparison.OrdinalIgnoreCase);
        }
        
        // Remove Scene Tags and video quality indicators
        s = Regex.Replace(s, @"\b(720p|1080p|2160p|480p|4K|x264|x265|HEVC|AAC|Fubo|WEBDL|WEB-DL|HDTV|h264|h265|BluRay|BDRip|WEBRip)\b", "", RegexOptions.IgnoreCase);
        
        // Remove frame rate indicators (e.g., 60fps, 30fps)
        s = Regex.Replace(s, @"\d+fps", "", RegexOptions.IgnoreCase);
        
        // Remove common language codes that appear after quality indicators (more specific than all 2-letter codes)
        // Only remove if they appear adjacent to numbers or quality tags to avoid removing team abbreviations
        s = Regex.Replace(s, @"(?<=\d{3,4}p|fps)\s*[A-Z]{2}\b", "", RegexOptions.IgnoreCase);
        
        // Remove codec/source strings
        s = Regex.Replace(s, @"\b(PROPER|REPACK|iNTERNAL|DUBBED|SUBBED|LIMITED|EXTENDED)\b", "", RegexOptions.IgnoreCase);
        
        return s.Trim().Trim('-', ' ', '.');
    }

    private async Task<string> ExpandAbbreviationsAsync(string input, string? leagueId, CancellationToken cancellationToken)
    {
        // Split by common separators to find team parts
        // "TOR-COL" -> "TOR", "COL"
        // "Dallas Stars vs BOS" -> "Dallas Stars", "BOS"
        var parts = input.Split(new[] { " vs ", " Vs ", " VS ", " v ", " V ", "-", " vs. " }, StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length == 2)
        {
            var p1 = parts[0].Trim();
            var p2 = parts[1].Trim();
            
            bool expanded = false;

            // Check Internal Map First
            if (TeamAbbreviations.TryGetValue(p1, out var f1))
            {
                p1 = f1;
                expanded = true;
            }
            // Check DB
            else 
            {
                var dbName = await ResolveTeamNameFromDbAsync(p1, leagueId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(dbName))
                {
                    p1 = dbName;
                    expanded = true;
                }
            }

            // Check Internal Map First
            if (TeamAbbreviations.TryGetValue(p2, out var f2))
            {
                p2 = f2;
                expanded = true;
            }
            // Check DB
            else
            {
                var dbName = await ResolveTeamNameFromDbAsync(p2, leagueId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(dbName))
                {
                    p2 = dbName;
                    expanded = true;
                }
            }

            if (expanded)
            {
                return $"{p1} vs {p2}";
            }
        }

        return input;
    }

    private async Task<string?> ResolveTeamNameFromDbAsync(string abbreviation, string? leagueId, CancellationToken cancellationToken)
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
            // Table structure provided: id, name, sport_id, stripped_name, country, short_name, alternative_names, league_id
            // We search by short_name (abbreviation) to find the full name.
            
            var sql = "SELECT name FROM teams WHERE short_name = $abbr COLLATE NOCASE";
            if (!string.IsNullOrEmpty(leagueId))
            {
                sql += " AND league_id = $leagueId";
                command.Parameters.AddWithValue("$leagueId", leagueId);
            }
            sql += " LIMIT 1";

            command.CommandText = sql;
            command.Parameters.AddWithValue("$abbr", abbreviation);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result != null && result != DBNull.Value)
            {
                return result.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve team abbreviation from DB: {Abbr}", abbreviation);
        }
        return null;
    }
}
