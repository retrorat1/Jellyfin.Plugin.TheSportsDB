namespace Jellyfin.Plugin.TheSportsDB.Providers;

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TheSportsDB.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

public class TheSportsDbClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TheSportsDbClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public TheSportsDbClient(IHttpClientFactory httpClientFactory, ILogger<TheSportsDbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    private string ApiKey => Plugin.Instance?.Configuration.ApiKey ?? "3";
    private string BaseUrl => $"https://www.thesportsdb.com/api/v1/json/{ApiKey}";

    public async Task<RootObject?> SearchLeagueAsync(string name, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/search_all_leagues.php?s={Uri.EscapeDataString(name)}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<RootObject?> GetLeagueAsync(string id, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/lookupleague.php?id={id}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        return await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RootObject?> SearchEventsAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/searchevents.php?e={Uri.EscapeDataString(query)}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<RootObject?> GetEventsBySeasonAsync(string leagueId, string season, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/eventsseason.php?id={leagueId}&s={Uri.EscapeDataString(season)}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<RootObject?> GetEventAsync(string id, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/lookupevent.php?id={id}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<RootObject?> GetEventsByDayAsync(DateTime date, string? leagueId, CancellationToken cancellationToken)
    {
        var d = date.ToString("yyyy-MM-dd");
        var url = $"{BaseUrl}/eventsday.php?d={d}";
        if (!string.IsNullOrEmpty(leagueId))
        {
            url += $"&l={leagueId}";
        }
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<RootObject?> SearchTeamsAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/searchteams.php?t={Uri.EscapeDataString(query)}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }
    
    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken) where T : class
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching data from URL: {Url}", url);
        }

        return null;
    }
}

// Data models
public class RootObject
{
    public List<League>? countrys { get; set; }
    public List<League>? leagues { get; set; }
    public List<Event>? events { get; set; }
    public List<Event>? eventresults { get; set; }
    public List<Team>? teams { get; set; }
}

public class League
{
    public string idLeague { get; set; } = string.Empty;
    public string strLeague { get; set; } = string.Empty;
    public string? strSport { get; set; }
    public string? strDescriptionEN { get; set; }
    public string? strBadge { get; set; }
    public string? strLogo { get; set; }
    public string? strPoster { get; set; }
    public string? strTrophy { get; set; }
    public string? strBanner { get; set; }
    public string? strFanart1 { get; set; }
    public string? strFanart2 { get; set; }
    public string? strFanart3 { get; set; }
    public string? strFanart4 { get; set; }
    public string? intFormedYear { get; set; }
    public string? strWebsite { get; set; }
    public string? strFacebook { get; set; }
    public string? strTwitter { get; set; }
    public string? strYoutube { get; set; }
}

public class Event
{
    public string idEvent { get; set; } = string.Empty;
    public string strEvent { get; set; } = string.Empty;
    public string? strFilename { get; set; }
    public string? strSport { get; set; }
    public string? idLeague { get; set; }
    public string? strLeague { get; set; }
    public string? strSeason { get; set; }
    public string? strDescriptionEN { get; set; }
    public string? strHomeTeam { get; set; }
    public string? strAwayTeam { get; set; }
    public string? intHomeScore { get; set; }
    public string? intAwayScore { get; set; }
    public string? intRound { get; set; }
    public string? dateEvent { get; set; }
    public string? strTime { get; set; }
    public string? strThumb { get; set; }
    public string? strPoster { get; set; }
    public string? strFanart { get; set; }
    public string? strVideo { get; set; }
    public string? idHomeTeam { get; set; }
    public string? idAwayTeam { get; set; }
}

public class Team
{
    public string idTeam { get; set; } = string.Empty;
    public string strTeam { get; set; } = string.Empty;
    public string? strTeamShort { get; set; }
    public string? strAlternate { get; set; }
    public string? intFormedYear { get; set; }
    public string? strSport { get; set; }
    public string? strLeague { get; set; }
    public string? idLeague { get; set; }
    public string? strDescriptionEN { get; set; }
    public string? strTeamBadge { get; set; }
    public string? strTeamJersey { get; set; }
    public string? strTeamLogo { get; set; }
    public string? strTeamFanart1 { get; set; }
    public string? strTeamFanart2 { get; set; }
    public string? strTeamFanart3 { get; set; }
    public string? strTeamFanart4 { get; set; }
    public string? strTeamBanner { get; set; }
}
