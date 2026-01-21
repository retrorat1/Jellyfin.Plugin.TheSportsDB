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
