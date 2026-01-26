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

public class TheSportsDBEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IRemoteImageProvider
{
    private readonly TheSportsDbClient _client;
    private readonly ILogger<TheSportsDBEpisodeProvider> _logger;

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
        if (match == null && matchDate.HasValue && !string.IsNullOrEmpty(leagueId))
        {
            _logger.LogInformation("TheSportsDB: No match by name. Trying lookup by Date {Date} and League {LeagueId}", matchDate.Value, leagueId);
            var dayResults = await _client.GetEventsByDayAsync(matchDate.Value, leagueId, cancellationToken).ConfigureAwait(false);
            
            if (dayResults?.events != null)
            {
                 // We have events for this day. Try to find one that matches the filename parts.
                 // Simple heuristic: Does the event string contain parts of the cleaned filename?
                 // Or just pick the one matching regex of teams if possible.
                 foreach (var ev in dayResults.events)
                 {
                     // If CleanName was "EDM-PIT" and ev.strEvent is "Edmonton Oilers vs Pittsburgh Penguins"
                     // We can try splitting input by non-alpha and matching?
                     
                     // If there's only one event for this league on this day, it's a very strong candidate.
                     if (dayResults.events.Count == 1)
                     {
                         _logger.LogInformation("TheSportsDB: Single event found for date/league. Accepting match: {Event}", ev.strEvent);
                         match = ev;
                         break;
                     }
                     
                     // Otherwise, try to fuzzy match
                     // Checking if the team abbreviations are in the full team names
                     // For now, let's use a loose containment check if the input is short.
                     
                     // If specific "vs" check logic
                     var normalizedAcc = ev.strEvent.Replace(" vs ", " ").Replace(" vs. ", " ").Replace("-", " ");
                     // normalizedAcc: "Edmonton Oilers Pittsburgh Penguins"
                     
                     // Check if "EDM" matches "Edmonton" (StartsWith)
                     // This is tricky without a dictionary. 
                     // But if the user renamed the file to "Minnesota Wild vs Detroit Red Wings", they are fine.
                     // This fallback is explicitly for the "EDM-PIT" case the user encountered.
                     
                     // Let's at least match if the cleaned name is contained in the event name
                     if (ev.strEvent.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase) >= 0)
                     {
                         match = ev;
                         break;
                     }
                     
                     // Experimental: Abbreviation check (StartWith) for first 3 chars
                     // cleanName="EDM-PIT" -> parts ["EDM", "PIT"]
                     // ev.strHomeTeam="Edmonton Oilers", strAwayTeam="Pittsburgh Penguins"
                     var parts = cleanName.Split(new[] { ' ', '-', 'v', 's', '.' }, StringSplitOptions.RemoveEmptyEntries);
                     // Filter out common small words/chars if needed, but 'v' and 's' split handles vs
                     
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
                             match = ev;
                             break;
                         }

                         // Advanced Lookup: If simple StartsWith failed, try to Resolve Team by Abbreviation
                         // This catches cases where Abbreviation != Start of Name (e.g. "MTL" vs "Montreal", "WSH" vs "Washington")
                         // Only do this if we haven't matched yet.
                         if (!match1) match1 = await CheckTeamIdMatch(p1, ev.idHomeTeam, ev.idAwayTeam, cancellationToken).ConfigureAwait(false);
                         if (!match2) match2 = await CheckTeamIdMatch(p2, ev.idHomeTeam, ev.idAwayTeam, cancellationToken).ConfigureAwait(false);

                         if (match1 && match2)
                         {
                             _logger.LogInformation("TheSportsDB: Abbreviation match found (TeamID Lookup): {P1}/{P2} matched Event {Event}", p1, p2, ev.strEvent);
                             match = ev;
                             break;
                         }
                     }
                 }
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
