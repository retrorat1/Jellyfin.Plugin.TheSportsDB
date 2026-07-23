namespace Jellyfin.Plugin.TheSportsDB.Providers;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TheSportsDB.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

public class TheSportsDbClient
{
    // Premium plan allows 100 req/min; leave headroom for concurrent scans.
    private const int MaxRequestsPerMinute = 90;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(700);
    private static readonly SemaphoreSlim RateGate = new(1, 1);
    private static readonly Queue<long> RequestTicks = new();
    private static long _lastRequestTicks;

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

    private string ApiKey => Plugin.Instance?.Configuration.ApiKey ?? "123";
    private string BaseUrl => $"https://www.thesportsdb.com/api/v1/json/{ApiKey}";

    public async Task<RootObject?> SearchLeagueAsync(string name, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/search_all_leagues.php?l={Uri.EscapeDataString(name)}";
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

    /// <summary>
    /// Season art by league id + season year (e.g. 4429 + 2022) — dedicated art step after path/filename match.
    /// TheSportsDB has no reliable JSON endpoint for a single season's poster
    /// (<c>search_all_seasons</c> ignores <c>s=</c> and is not used for art).
    /// Scrapes the public season page HTML for <c>posterarchive</c> (Season Poster) then <c>badgearchive</c>.
    /// Prefers pretty URL <c>/season/{id}-{slug}/{year}</c> when <paramref name="leagueSlug"/> is available;
    /// falls back to <c>season.php?l=&amp;s=</c> (no slug required; does not redirect).
    /// </summary>
    public async Task<SeasonArt?> GetSeasonArtBySeasonAsync(
        string leagueId,
        string season,
        CancellationToken cancellationToken,
        string? leagueSlug = null)
    {
        if (string.IsNullOrWhiteSpace(leagueId) || string.IsNullOrWhiteSpace(season))
            return null;

        var seasonKey = season.Trim();
        foreach (var url in BuildSeasonPageUrls(leagueId, seasonKey, leagueSlug))
        {
            var art = await TryScrapeSeasonPageAsync(url, leagueId, seasonKey, cancellationToken)
                .ConfigureAwait(false);
            if (art != null)
                return art;
        }

        return null;
    }

    /// <summary>
    /// Pretty URL first when slug known; <c>season.php</c> always as reliable fallback (no slug).
    /// Id-only <c>/season/{id}/{year}</c> also works on TheSportsDB but is unused — php is enough.
    /// </summary>
    private static IEnumerable<string> BuildSeasonPageUrls(string leagueId, string season, string? leagueSlug)
    {
        var slug = NormalizeLeagueSlug(leagueSlug);
        if (!string.IsNullOrEmpty(slug))
        {
            yield return
                $"https://www.thesportsdb.com/season/{Uri.EscapeDataString(leagueId)}-{slug}/{Uri.EscapeDataString(season)}";
        }

        yield return
            $"https://www.thesportsdb.com/season.php?l={Uri.EscapeDataString(leagueId)}" +
            $"&s={Uri.EscapeDataString(season)}";
    }

    /// <summary>DB look_up uses underscores; site canonicals use hyphens. Both work; prefer hyphens.</summary>
    private static string? NormalizeLeagueSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        // Keep only URL-safe slug chars; look_up is typically "fifa_world_cup"
        var cleaned = slug.Trim().ToLowerInvariant().Replace('_', '-');
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[^a-z0-9\-]+", "-");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"-+", "-").Trim('-');
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    private async Task<SeasonArt?> TryScrapeSeasonPageAsync(
        string url, string leagueId, string season, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("TheSportsDB season page Request: {Url}", url);

            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TheSportsDB season page HTTP {StatusCode} for {Url}",
                    (int)response.StatusCode,
                    url);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (poster, badge) = ExtractSeasonArtFromHtml(html);

            if (string.IsNullOrEmpty(poster) && string.IsNullOrEmpty(badge))
            {
                _logger.LogWarning(
                    "TheSportsDB: No poster/badge on season page for league {LeagueId} season {Season} ({Url})",
                    leagueId,
                    season,
                    url);
                return null;
            }

            _logger.LogInformation(
                "TheSportsDB: Scraped season art for {LeagueId}/{Season} via {Url} " +
                "(poster={Poster}, badge={Badge})",
                leagueId,
                season,
                url,
                poster ?? "(none)",
                badge ?? "(none)");

            return new SeasonArt
            {
                strSeason = season,
                strPoster = poster,
                strBadge = badge
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TheSportsDB: Failed season page art lookup for {Url}",
                url);
            return null;
        }
    }

    /// <summary>
    /// Prefer labeled Season Poster / Season Badge hrefs, then any posterarchive / badgearchive URL.
    /// Primary preference is posterarchive; badgearchive is the fallback image source.
    /// </summary>
    private static (string? Poster, string? Badge) ExtractSeasonArtFromHtml(string html)
    {
        // Exact labels from the season page, e.g.:
        // <b>Season Poster</b><a href='https://r2.thesportsdb.com/images/media/league/posterarchive/...jpg'>
        var poster = FirstGroup(html,
            @"(?is)<b>\s*Season Poster\s*</b>\s*<a[^>]+href\s*=\s*['""]([^'""]+)['""]");
        var badge = FirstGroup(html,
            @"(?is)<b>\s*Season Badge\s*</b>\s*<a[^>]+href\s*=\s*['""]([^'""]+)['""]");

        poster ??= FirstMatch(html,
            @"https://(?:www\.|r2\.)?thesportsdb\.com/images/media/league/posterarchive/[a-zA-Z0-9]+\.(?:jpg|png|webp)");
        badge ??= FirstMatch(html,
            @"https://(?:www\.|r2\.)?thesportsdb\.com/images/media/league/badgearchive/[a-zA-Z0-9]+\.(?:jpg|png|webp)");

        if (poster == null)
        {
            var rel = FirstMatch(html, @"images/media/league/posterarchive/[a-zA-Z0-9]+\.(?:jpg|png|webp)");
            if (rel != null)
                poster = "https://r2.thesportsdb.com/" + rel.TrimStart('/');
        }

        if (badge == null)
        {
            var rel = FirstMatch(html, @"images/media/league/badgearchive/[a-zA-Z0-9]+\.(?:jpg|png|webp)");
            if (rel != null)
                badge = "https://r2.thesportsdb.com/" + rel.TrimStart('/');
        }

        return (poster, badge);
    }

    private static string? FirstMatch(string input, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            input, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Value : null;
    }

    private static string? FirstGroup(string input, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            input, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value.Trim() : null;
    }

    public async Task<RootObject?> GetEventAsync(string id, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/lookupevent.php?id={id}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<RootObject?> GetEventsByDayAsync(DateTime date, string? sportName, string? leagueId, string? leagueName, CancellationToken cancellationToken)
    {
        var d = date.ToString("yyyy-MM-dd");
        var url = $"{BaseUrl}/eventsday.php?d={d}";
        
        // Filter by sport if available (e.g. &s=Soccer)
        if (!string.IsNullOrEmpty(sportName))
        {
            url += $"&s={Uri.EscapeDataString(sportName)}";
        }
        
        // eventsday.php currently expects a numeric league id in 'l' (for example l=4370 for Formula 1).
        // Slugs/names (for example "formula_1") return "Invalid League ID passed".
        if (!string.IsNullOrEmpty(leagueId))
        {
            url += $"&l={Uri.EscapeDataString(leagueId)}";
        }
        
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<RootObject?> SearchTeamsAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/searchteams.php?t={Uri.EscapeDataString(query)}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }

    public async Task<RootObject?> GetTeamAsync(string id, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/lookupteam.php?id={id}";
        return await GetJsonAsync<RootObject>(url, cancellationToken);
    }
    
    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken) where T : class
    {
        const int maxAttempts = 4;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("TheSportsDB API Request: {Url}", url);

                using var client = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var delay = response.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(65);
                    if (delay < TimeSpan.FromSeconds(5))
                        delay = TimeSpan.FromSeconds(65);

                    _logger.LogWarning(
                        "TheSportsDB rate limited (429) for {Url}; waiting {Seconds}s (attempt {Attempt}/{Max})",
                        url,
                        delay.TotalSeconds,
                        attempt,
                        maxAttempts);

                    ResetRateWindow();
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "TheSportsDB HTTP {StatusCode} for {Url}",
                        (int)response.StatusCode,
                        url);
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var backoff = TimeSpan.FromSeconds(Math.Min(30, 5 * attempt));
                _logger.LogWarning(
                    ex,
                    "TheSportsDB request error for {Url}; retrying in {Seconds}s (attempt {Attempt}/{Max})",
                    url,
                    backoff.TotalSeconds,
                    attempt,
                    maxAttempts);
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching data from URL: {Url}", url);
                return null;
            }
        }

        return null;
    }

    private static async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay = TimeSpan.Zero;

            await RateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow.Ticks;
                var windowTicks = TimeSpan.FromMinutes(1).Ticks;

                while (RequestTicks.Count > 0 && now - RequestTicks.Peek() >= windowTicks)
                    RequestTicks.Dequeue();

                if (RequestTicks.Count >= MaxRequestsPerMinute)
                {
                    var waitTicks = RequestTicks.Peek() + windowTicks - now;
                    delay = TimeSpan.FromTicks(Math.Max(waitTicks, 0)) + TimeSpan.FromMilliseconds(50);
                }
                else if (_lastRequestTicks > 0)
                {
                    var sinceLast = TimeSpan.FromTicks(now - _lastRequestTicks);
                    if (sinceLast < MinInterval)
                        delay = MinInterval - sinceLast;
                }

                if (delay <= TimeSpan.Zero)
                {
                    _lastRequestTicks = now;
                    RequestTicks.Enqueue(now);
                    return;
                }
            }
            finally
            {
                RateGate.Release();
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ResetRateWindow()
    {
        RateGate.Wait();
        try
        {
            RequestTicks.Clear();
            _lastRequestTicks = 0;
        }
        finally
        {
            RateGate.Release();
        }
    }
}

// Data models
public class RootObject
{
    public List<League>? countrys { get; set; }
    public List<League>? leagues { get; set; }
    public List<Event>? events { get; set; }
    public List<Event>? @event { get; set; }
    public List<Event>? eventresults { get; set; }
    public List<Team>? teams { get; set; }
    public List<SeasonArt>? seasons { get; set; }
}

public class SeasonArt
{
    public string strSeason { get; set; } = string.Empty;
    public string? strPoster { get; set; }
    public string? strBadge { get; set; }
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
    public string? dateFirstEvent { get; set; }
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
