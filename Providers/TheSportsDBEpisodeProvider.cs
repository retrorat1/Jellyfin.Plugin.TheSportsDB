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

        // Strategy 1: If we have multiple providers, maybe we have an ID?
        var eventId = info.GetProviderId("TheSportsDB");
        if (!string.IsNullOrEmpty(eventId))
        {
             // TODO: Lookup specific event by ID (need simple lookup endpoint, or use search)
             // _client.GetEventAsync... (Not implemented yet, but search handles it usually)
        }

        // Strategy 2: Use Series ID + Season + Date/Name to find event
        var leagueId = info.SeriesProviderIds.GetValueOrDefault("TheSportsDB");
        
        // If we don't have league ID, we can't do exact season lookup easily.
        // We'll rely on info.SeriesName and info.Name
        
        if (!string.IsNullOrEmpty(leagueId))
        {
            // Resolve Season
            // TODO: Future logic to resolve Season from IndexNumber/ParentIndexNumber
            // if (info.IndexNumber.HasValue && info.ParentIndexNumber.HasValue) 
            // {
            //    // This works for "Season 2025" if mapped correctly?
            //    // Standard Sports naming: Season 2025-2026.
            // }
            
            // Try extracting season from path or use current year if needed?
            // Actually, best bet is to fuzzy search the event name first if reliable.
        }

        // Let's try Search by Name first as it is versatile
        var searchName = info.Name;
        
        // If file name is "NHL 2026-01-21 Dallas Stars vs Boston Bruins"
        // TSDB Event might be "Dallas Stars vs Boston Bruins"
        // We should try to clean the name.
        
        // Regex to remove "NHL YYYY-MM-DD " prefix?
        // Match: (League)? (YYYY-MM-DD)? (Home vs Away)
        
        var matchDate = MatchDate(info.Name);
        var cleanName = CleanName(info.Name);

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
                     if (parts.Length >= 2 && !string.IsNullOrEmpty(ev.strHomeTeam) && !string.IsNullOrEmpty(ev.strAwayTeam))
                     {
                         bool homeMatch = ev.strHomeTeam.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase) || 
                                          ev.strAwayTeam.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase);
                         bool awayMatch = ev.strHomeTeam.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase) || 
                                          ev.strAwayTeam.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase);
                                          
                         if (homeMatch && awayMatch)
                         {
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
        if (m.Success && DateTime.TryParse(m.Groups[1].Value, out var d)) return d;
        
        // Try DD-MM-YYYY
        m = Regex.Match(input, @"(\d{2}-\d{2}-\d{4})");
        if (m.Success && DateTime.TryParse(m.Groups[1].Value, out d)) return d;

        return null;
    }
    
    private string CleanName(string input)
    {
        // Remove Dates
        var s = Regex.Replace(input, @"\d{4}-\d{2}-\d{2}", ""); // YYYY-MM-DD
        s = Regex.Replace(s, @"\d{2}-\d{2}-\d{4}", ""); // DD-MM-YYYY
        
        // Remove League Prefixes common in filename but not in Event Name
        // E.g. "NHL ", "EPL "
        // Use a simple heuristic: "Team vs Team" is usually what we want.
        // If " vs " or " vs. " exists, try to grab surrounding words?
        // Or just strip common separators.
        
        // Heuristic: Remove anything that looks like a series name if possible.
        // For now, let's just trim excess/punctuation.
        s = s.Replace("NHL", "", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("EPL", "", StringComparison.OrdinalIgnoreCase);
        
        return s.Trim().Trim('-', ' ');
    }
}
