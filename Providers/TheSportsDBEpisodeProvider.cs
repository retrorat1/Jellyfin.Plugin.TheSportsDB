using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TheSportsDB.Providers
{
    public class TheSportsDBEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IRemoteImageProvider
    {
        private readonly TheSportsDbClient _client;
        private readonly ILogger<TheSportsDBEpisodeProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SportsResolverDb _sportsResolverDb;

        private static readonly string[] LeagueNameStrips = new[]
        {
            // Longest/most-specific phrases MUST come first to avoid partial strips
            "English Premier League",
            "Spanish La Liga",           // before "La Liga" and "Spanish"
            "ICC Mens T20 World Cup",    // before "ICC"
            "ICC Womens T20 World Cup",
            "ICC Cricket World Cup",
            "La Liga",
            "Spanish",
            "NHL", "EPL", "NFL", "NBA", "MLB", "UFC", "ICC",
        };
        private static readonly string[] SuffixStrips = new[]
        {
            "Early Prelims", "Early Card", "Main Card", "Main Event", "Prelims", "Fight-BB",
            "Kickoff", "Pre Show", "Post Show", "Weigh-in", "Face Off"
        };

        public string Name => "TheSportsDB";
        public bool Supports(BaseItem item) => item is Episode;

        public TheSportsDBEpisodeProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<TheSportsDBEpisodeProvider> logger,
            ILogger<TheSportsDbClient> clientLogger,
            IApplicationPaths applicationPaths
        )
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _client = new TheSportsDbClient(httpClientFactory, clientLogger);
            string pluginLocation = typeof(TheSportsDBEpisodeProvider).Assembly.Location;
            string dbPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(pluginLocation) ?? "", "sports_resolver.db");
            _sportsResolverDb = new SportsResolverDb(dbPath);
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            _logger.LogInformation("TheSportsDB: Searching events for {Name}", searchInfo.Name);
            var result = await _client.SearchEventsAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            var list = new List<RemoteSearchResult>();
            var eventList = result?.events ?? result?.@event;
            if (eventList != null)
            {
                foreach (var ev in eventList)
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
            var result = new MetadataResult<Episode>();
            var rawName = info.Name;
            var path = info.Path;
            string? seriesName = null;

            // Capture the raw filename (without extension) early, before any cleaning,
            // for use in strFilename matching against the TheSportsDB API field.
            string rawFilename = !string.IsNullOrEmpty(info.Path)
                ? System.IO.Path.GetFileNameWithoutExtension(info.Path)
                : rawName;

            if (!string.IsNullOrEmpty(path))
            {
                seriesName = GetSeriesNameFromPath(path);
                _logger.LogInformation("TheSportsDB: Series Name resolved from path: \"{SeriesName}\"", seriesName);
            }

            _logger.LogInformation("TheSportsDB: GetMetadata called for Item: \"{ItemName}\", Series: \"{SeriesName}\", Folder: \"{Path}\"", rawName, seriesName, info.Path);

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                 _logger.LogWarning("TheSportsDB: Configuration is null.");
            }

            // League mapping from config/DB
            string? leagueId = null;
            if (config != null && !string.IsNullOrEmpty(seriesName))
            {
                var mapping = config.LeagueMappings.FirstOrDefault(m => string.Equals(m.Name, seriesName, StringComparison.OrdinalIgnoreCase));
                if (mapping != null)
                {
                    leagueId = mapping.LeagueId;
                    _logger.LogInformation("TheSportsDB: Series \"{Series}\" mapped found in Config -> LeagueId: \"{Id}\"", seriesName, leagueId);
                }
            }
            if (string.IsNullOrEmpty(leagueId) && !string.IsNullOrEmpty(seriesName))
            {
                leagueId = _sportsResolverDb.GetLeagueIdFromAlias(seriesName);
                if (!string.IsNullOrEmpty(leagueId))
                {
                    _logger.LogInformation("TheSportsDB: Series \"{Series}\" resolved via DB Alias to LeagueId: \"{Id}\"", seriesName, leagueId);
                }
            }

            // Get league slug (API name)
            string? leagueSlug = null;
            if (!string.IsNullOrEmpty(leagueId))
            {
                leagueSlug = _sportsResolverDb.GetLeagueSlug(leagueId);
            }

            _logger.LogInformation("TheSportsDB: Resolved League Slug: \"{Slug}\"", leagueSlug);

            if (!string.IsNullOrEmpty(info.Path))
            {
                string filename = System.IO.Path.GetFileNameWithoutExtension(info.Path);
                if (rawName.Length <= 4 || string.Equals(rawName, seriesName, StringComparison.OrdinalIgnoreCase) || LeagueNameStrips.Any(s => string.Equals(rawName, s, StringComparison.OrdinalIgnoreCase)))
                {
                     _logger.LogInformation("TheSportsDB: info.Name \"{Name}\" seems invalid. Using filename \"{Filename}\" instead.", rawName, filename);
                     rawName = filename;
                }
            }

            string cleanName = CleanEpisodeName(rawName, out string? cardType, out DateTime? date);

            // STRICT DB TEAM MAPPING
            string resolvedCleanName = cleanName;
            string teamAname = null, teamBname = null;

            var vsIdx = cleanName.IndexOf(" vs ", StringComparison.OrdinalIgnoreCase);
            if (vsIdx > 0 && leagueId != null)
            {
                var teamAAbbr = cleanName.Substring(0, vsIdx).Trim();
                var teamBAbbr = cleanName.Substring(vsIdx + 4).Trim();

                teamAname = _sportsResolverDb.GetTeamFullName(teamAAbbr, leagueId);
                teamBname = _sportsResolverDb.GetTeamFullName(teamBAbbr, leagueId);

                _logger.LogInformation($"Team parsing from filename: {teamAAbbr} ({teamAname}), {teamBAbbr} ({teamBname}) in league {leagueId}");

                if (string.IsNullOrEmpty(teamAname)) teamAname = teamAAbbr;
                if (string.IsNullOrEmpty(teamBname)) teamBname = teamBAbbr;
                resolvedCleanName = $"{teamAname} vs {teamBname}";
            }
            else if (cleanName.Contains("-") && leagueId != null)
            {
                var parts = cleanName.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var teamAAbbr = parts[parts.Length - 2].Trim();
                    var teamBAbbr = parts[parts.Length - 1].Trim();

                    if (teamAAbbr.Length <= 5 && teamBAbbr.Length <= 5)
                    {
                        teamAname = _sportsResolverDb.GetTeamFullName(teamAAbbr, leagueId);
                        teamBname = _sportsResolverDb.GetTeamFullName(teamBAbbr, leagueId);

                        _logger.LogInformation($"Team parsing from hyphen split: {teamAAbbr} ({teamAname}), {teamBAbbr} ({teamBname}) in league {leagueId}");

                        if (string.IsNullOrEmpty(teamAname)) teamAname = teamAAbbr;
                        if (string.IsNullOrEmpty(teamBname)) teamBname = teamBAbbr;
                        resolvedCleanName = $"{teamAname} vs {teamBname}";
                    }
                }
            }

            // STRONGLY pass resolved team names
            var eventMatch = await FindEventByDayAsync(teamAname, teamBname, rawFilename, leagueId, leagueSlug, date, cancellationToken);

            if (eventMatch != null)
            {
                // Fetch full, clean metadata via lookupevent for guaranteed single-event response
                var fullEventResult = await _client.GetEventAsync(eventMatch.idEvent, cancellationToken).ConfigureAwait(false);
                var ev = fullEventResult?.events?.FirstOrDefault() ?? fullEventResult?.@event?.FirstOrDefault() ?? eventMatch;

                result.HasMetadata = true;
                var displayTitle = ev.strEvent;

                // Suffixes (Prelims, etc.)
                var suffixes = new[] { "Early Prelims", "Prelims", "Main Card", "Weigh-in", "Post Show", "Pre Show", "Kickoff", "Press Conference" };
                foreach (var s in suffixes)
                {
                    if (rawName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (displayTitle.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            displayTitle += $" ({s})";
                        }
                        break;
                    }
                }

                result.Item = new Episode
                {
                    Name = displayTitle,
                    Overview = BuildOverview(ev.strDescriptionEN, ev.strEvent),
                    PremiereDate = DateTime.TryParse(ev.dateEvent, out var d) ? d : null,
                    ProductionYear = DateTime.TryParse(ev.dateEvent, out var d2) ? d2.Year : null,
                };
                result.Item.ProviderIds["TheSportsDB"] = ev.idEvent;

                if (!string.IsNullOrEmpty(ev.strThumb))
                {
                    result.Item.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = ev.strThumb }, 0);
                }
                else if (!string.IsNullOrEmpty(ev.strPoster))
                {
                    result.Item.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = ev.strPoster }, 0);
                }
                if (!string.IsNullOrEmpty(ev.strFanart))
                {
                    result.Item.SetImage(new ItemImageInfo { Type = ImageType.Backdrop, Path = ev.strFanart }, 0);
                }
            }
            return result;
        }

        private static string? GetSeriesNameFromPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir == null) return null;
            var folderName = System.IO.Path.GetFileName(dir);
            if (Regex.IsMatch(folderName ?? "", @"\d{4}|Season", RegexOptions.IgnoreCase))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir != null ? System.IO.Path.GetFileName(dir) : null;
        }

        private static string CleanEpisodeName(string raw, out string? cardType, out DateTime? fileDate)
        {
            string name = raw;
            name = name.Replace('.', ' ');
            name = Regex.Replace(name, @"\bUtd\b", "United", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\b(RS|PS)\b", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"(\d{2})(\d{3,4}p)", "$1 $2");
            name = Regex.Replace(name, @"\b\d{3,4}p(?:[a-zA-Z0-9]+)?\b", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\b\d{2,3}fps\b", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\b\d{1,2}_\d{2}\b", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\bPart\s?\d+\b", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\bUFC\s\d{3}\b", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\bUFC\sFight\sNight\b", "", RegexOptions.IgnoreCase);

            var noiseTags = new[] { "Fubo", "Peacock", "Sky", "TNT", "Amazon", "BBC", "ITV", "HDTV", "WEB-DL", "WEBRip", "X264", "H264", "X265", "H265", "HEVC", "AAC", "MKV", "MP4" };
            foreach (var tag in noiseTags)
            {
                name = Regex.Replace(name, @"\b" + Regex.Escape(tag) + @"\b", "", RegexOptions.IgnoreCase);
            }
            cardType = null;
            foreach (var s in SuffixStrips)
            {
                if (name.Contains(s, StringComparison.OrdinalIgnoreCase))
                    cardType = s;
                name = Regex.Replace(name, @"\b" + Regex.Escape(s) + @"\b", "", RegexOptions.IgnoreCase);
            }
            foreach (var s in LeagueNameStrips)
            {
                name = Regex.Replace(name, @"\b" + Regex.Escape(s) + @"\b", "", RegexOptions.IgnoreCase);
            }
            name = Regex.Replace(name, @"\s+", " ").Trim();

            fileDate = null;
            var mIso = Regex.Match(name, @"(\d{4})[ \.\-_](\d{2})[ \.\-_](\d{2})");
            if (mIso.Success)
            {
                if (int.TryParse(mIso.Groups[1].Value, out int year) &&
                    int.TryParse(mIso.Groups[2].Value, out int month) &&
                    int.TryParse(mIso.Groups[3].Value, out int day))
                {
                     try { fileDate = new DateTime(year, month, day); } catch { }
                     name = name.Replace(mIso.Value, "").Trim();
                }
            }
            else
            {
                var m = Regex.Match(name, @"(\d{2})[ \.\-_]?(\d{2})[ \.\-_]?(\d{4})");
                if (m.Success)
                {
                    if (int.TryParse(m.Groups[3].Value, out int year) &&
                        int.TryParse(m.Groups[2].Value, out int month) &&
                        int.TryParse(m.Groups[1].Value, out int day))
                    {
                         try { fileDate = new DateTime(year, month, day); } catch { }
                         name = name.Replace(m.Value, "").Trim();
                    }
                }
                else
                {
                    var mSplit = Regex.Match(name, @"\b(19|20)(\d{2})\b.*?\b(\d{2})\s+(\d{2})\b");
                    if (mSplit.Success)
                    {
                        string yearStr = mSplit.Groups[1].Value + mSplit.Groups[2].Value;
                        if (int.TryParse(yearStr, out int year) &&
                            int.TryParse(mSplit.Groups[3].Value, out int p1) &&
                            int.TryParse(mSplit.Groups[4].Value, out int p2))
                        {
                            try {
                                fileDate = new DateTime(year, p2, p1); 
                                name = name.Replace(yearStr, "").Replace($"{mSplit.Groups[3].Value} {mSplit.Groups[4].Value}", "").Trim();
                            } catch { }
                        }
                    }
                }
            }
            name = Regex.Replace(name, @"\s+", " ").Trim();
            name = name.Trim('-', ' ', '~', '_');

            return name;
        }

        // Match Priority 1: strFilename exact match (primary — before any team resolution)
        // Match Priority 2: strHomeTeam/strAwayTeam match + date match (only when teams are known)
        private async Task<Event?> FindEventByDayAsync(
            string? teamAname, string? teamBname,
            string rawFilename,
            string? leagueId, string? leagueSlug,
            DateTime? fileDate,
            CancellationToken cancellationToken)
        {
            if (leagueId == null || !fileDate.HasValue)
                return null;

            bool teamsKnown = !string.IsNullOrEmpty(teamAname) && !string.IsNullOrEmpty(teamBname);

            int[] dateOffsets = new[] { 0, 1, -1, 2, -2 };
            foreach (int offset in dateOffsets)
            {
                DateTime dateParam = fileDate.Value.AddDays(offset);
                _logger.LogInformation("TheSportsDB: Searching events by day: LeagueId={LeagueId}, Date={Date}", leagueId, dateParam.ToString("yyyy-MM-dd"));

                var eventsResult = await _client.GetEventsByDayAsync(dateParam, null, leagueId, leagueSlug, cancellationToken).ConfigureAwait(false);
                var evList = eventsResult?.events ?? eventsResult?.@event;
                if (evList == null || evList.Count == 0) continue;

                // Priority 1: strFilename exact match against raw unstripped filename
                foreach (var ev in evList)
                {
                    if (!string.IsNullOrEmpty(ev.strFilename) && !string.IsNullOrEmpty(rawFilename) &&
                        string.Equals(ev.strFilename.Trim(), rawFilename.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("TheSportsDB: Match via strFilename: \"{StrFilename}\"", ev.strFilename);
                        return ev;
                    }
                }

                // Priority 2: strHomeTeam/strAwayTeam match + date match (only when teams are known)
                if (teamsKnown)
                {
                    foreach (var ev in evList)
                    {
                        bool homeawayMatch =
                            (!string.IsNullOrEmpty(ev.strHomeTeam) && !string.IsNullOrEmpty(ev.strAwayTeam)) &&
                            (
                                (string.Equals(ev.strHomeTeam, teamAname, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(ev.strAwayTeam, teamBname, StringComparison.OrdinalIgnoreCase)) ||
                                (string.Equals(ev.strHomeTeam, teamBname, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(ev.strAwayTeam, teamAname, StringComparison.OrdinalIgnoreCase))
                            );
                        bool dateMatch =
                            DateTime.TryParse(ev.dateEvent, out var evDate) &&
                            evDate.Date == dateParam.Date;

                        if (homeawayMatch && dateMatch)
                        {
                            _logger.LogInformation("TheSportsDB: Match via team names: {EventName}, Home={Home}, Away={Away}, Date={Date}",
                                ev.strEvent, ev.strHomeTeam, ev.strAwayTeam, ev.dateEvent);
                            return ev;
                        }
                    }
                }
            }

            _logger.LogWarning("TheSportsDB: No event match found for {TeamA} vs {TeamB} on {Date}", teamAname, teamBname, fileDate?.ToString("yyyy-MM-dd"));
            return null;
        }

        private static string BuildOverview(string? desc, string? title)
        {
            if (string.IsNullOrEmpty(desc)) return "";
            desc = desc.Trim();
            if (desc.Length <= 1000) return desc;
            int idx = desc.LastIndexOf('.', 1000);
            if (idx >= 0) return desc.Substring(0, idx + 1).Trim() + "...";
            return desc.Substring(0, 1000).Trim() + "...";
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Banner };

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();
            var id = item.GetProviderId("TheSportsDB");

            if (string.IsNullOrEmpty(id)) return list;

            var result = await _client.GetEventAsync(id, cancellationToken).ConfigureAwait(false);
            var ev = result?.events?.FirstOrDefault() ?? result?.@event?.FirstOrDefault();

            if (ev != null)
            {
                if (!string.IsNullOrEmpty(ev.strThumb))
                    list.Add(new RemoteImageInfo { Url = ev.strThumb, Type = ImageType.Primary, ProviderName = Name });
                else if (!string.IsNullOrEmpty(ev.strPoster))
                    list.Add(new RemoteImageInfo { Url = ev.strPoster, Type = ImageType.Primary, ProviderName = Name });

                if (!string.IsNullOrEmpty(ev.strFanart))
                    list.Add(new RemoteImageInfo { Url = ev.strFanart, Type = ImageType.Backdrop, ProviderName = Name });
            }

            return list;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }
    }
}