namespace Jellyfin.Plugin.TheSportsDB.Providers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

public class TheSportsDBEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>
{
    private readonly TheSportsDbClient _client;
    private readonly ILogger<TheSportsDBEpisodeProvider> _logger;

    public string Name => "TheSportsDB";

    public TheSportsDBEpisodeProvider(IHttpClientFactory httpClientFactory, ILogger<TheSportsDBEpisodeProvider> logger, ILogger<TheSportsDbClient> clientLogger)
    {
        _client = new TheSportsDbClient(httpClientFactory, clientLogger);
        _logger = logger;
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
            string? season = null;
            if (info.IndexNumber.HasValue && info.ParentIndexNumber.HasValue) // S01E01 style
            {
               // This works for "Season 2025" if mapped correctly?
               // Standard Sports naming: Season 2025-2026.
               // Jellyfin "ParentIndexNumber" is usually an Integer (1, 2).
               // The user file structure is "2025-2026". Jellyfin might not parse this as an Integer Index.
               // It might put it in "SeasonName" or ignore it.
            }
            
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

        // Fallback: If no match by name, and we have LeagueID + Season
        // This is expensive (fetching all events for season), maybe skip for now unless requested.

        if (match == null)
        {
             return new MetadataResult<Episode>();
        }

        var ep = new Episode
        {
            Name = match.strEvent,
            Overview = match.strDescriptionEN,
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
