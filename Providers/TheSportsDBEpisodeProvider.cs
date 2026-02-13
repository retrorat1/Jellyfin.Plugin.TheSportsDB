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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TheSportsDB.Providers
{
    public class TheSportsDBEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IRemoteImageProvider
    {
        private readonly TheSportsDbClient _client;
        private readonly ILogger<TheSportsDBEpisodeProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        private static readonly Dictionary<string, string> KnownLeagueIds = new(StringComparer.OrdinalIgnoreCase)
        {
            { "NHL", "4380" },
            { "EPL", "4328" },
            { "NFL", "4391" },
            { "NBA", "4387" },
            { "MLB", "4424" },
            { "UFC", "4443" }
        };
        private static readonly string[] LeagueNameStrips = new[]
        {
            "English Premier League", "NHL", "EPL", "NFL", "NBA", "MLB", "UFC", "La Liga", "Spanish La Liga"
        };
        private static readonly string[] SuffixStrips = new[]
        {
            "Prelims", "Early Prelims", "Early Card", "Main Card", "Main Event", "Fight-BB"
        };

        public string Name => "TheSportsDB";
        public bool Supports(BaseItem item) => item is Episode;

        public TheSportsDBEpisodeProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<TheSportsDBEpisodeProvider> logger,
            ILogger<TheSportsDbClient> clientLogger
        )
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _client = new TheSportsDbClient(httpClientFactory, clientLogger);
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
            _logger.LogInformation("TheSportsDB: Getting episode metadata for \"{Name}\"", info.Name);

            string? seriesName = GetSeriesNameFromPath(info.Path);
            var config = Plugin.Instance?.Configuration;
            string? leagueId =
                config?.LeagueMappings?.FirstOrDefault(x => x.Name.Equals(seriesName, StringComparison.OrdinalIgnoreCase))?.LeagueId
                ?? (KnownLeagueIds.TryGetValue(seriesName ?? "", out var lid) ? lid : null);

            string cleanName = CleanEpisodeName(info.Name, out string? cardType, out DateTime? date);
            var eventMatch = await FindMatchWithSwapAndCleanAsync(cleanName, leagueId, date, cancellationToken);

            var result = new MetadataResult<Episode>();
            if (eventMatch != null)
            {
                result.HasMetadata = true;
                result.Item = new Episode
                {
                    Name = eventMatch.strEvent?.Trim(),
                    Overview = BuildOverview(cardType, eventMatch.strDescriptionEN),
                    PremiereDate = DateTime.TryParse(eventMatch.dateEvent, out var d) ? d : (DateTime?)null,
                    ProductionYear = DateTime.TryParse(eventMatch.dateEvent, out var dy) ? dy.Year : (int?)null,
                };
                result.Item.ProviderIds["TheSportsDB"] = eventMatch.idEvent;
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
            // Skip if this folder is a season folder
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
            name = Regex.Replace(name, @"\d{2,4}fp[s]?\b", "", RegexOptions.IgnoreCase);

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
            var m = Regex.Match(raw, @"(\d{2})[ _\-]?(\d{2})[ _\-]?(\d{2,4})");
            if (m.Success)
            {
                int year = m.Groups[3].Value.Length == 2 ? 2000 + int.Parse(m.Groups[3].Value) : int.Parse(m.Groups[3].Value);
                int month = int.Parse(m.Groups[2].Value);
                int day = int.Parse(m.Groups[1].Value);
                try { fileDate = new DateTime(year, month, day); } catch { }
            }
            return name;
        }

        private async Task<Event?> FindMatchWithSwapAndCleanAsync(string cleanName, string? leagueId, DateTime? fileDate, CancellationToken cancellationToken)
        {
            Event? match = null;
            match = await FindMatchAsync(cleanName, leagueId, fileDate, cancellationToken);
            if (match != null) return match;

            if (fileDate.HasValue)
            {
                var vsIdx = cleanName.IndexOf(" vs ", StringComparison.OrdinalIgnoreCase);
                if (vsIdx > 0)
                {
                    var teamA = cleanName.Substring(0, vsIdx).Trim();
                    var teamB = cleanName.Substring(vsIdx + 4).Trim();
                    string swapped = $"{teamB} vs {teamA}";
                    match = await FindMatchAsync(swapped, leagueId, fileDate, cancellationToken);
                }
            }
            return match;
        }

        private async Task<Event?> FindMatchAsync(string name, string? leagueId, DateTime? fileDate, CancellationToken cancellationToken)
        {
            int[] dateOffsets = fileDate.HasValue ? new[] { 0, 1, -1 } : new[] { 0 };
            foreach (int offset in dateOffsets)
            {
                DateTime? dateParam = fileDate.HasValue ? fileDate.Value.AddDays(offset) : (DateTime?)null;
                RootObject? eventsResult = null;
                if (leagueId != null && dateParam.HasValue)
                {
                    eventsResult = await _client.GetEventsByDayAsync(dateParam.Value, leagueId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    eventsResult = await _client.SearchEventsAsync(name, cancellationToken).ConfigureAwait(false);
                }

                var evList = eventsResult?.events ?? eventsResult?.@event;
                if (evList != null)
                {
                    return evList.FirstOrDefault(ev => IsEventMatch(ev.strEvent, name));
                }
            }
            return null;
        }

        private static bool IsEventMatch(string? a, string? b)
        {
            static string Canon(string? s) => Regex.Replace(s ?? "", @"[^A-Za-z0-9]", "").ToLowerInvariant();
            return Canon(a) == Canon(b);
        }

        private static string BuildOverview(string? cardType, string? desc)
        {
            if (!string.IsNullOrWhiteSpace(cardType) &&
                (cardType.Contains("Prelims", StringComparison.OrdinalIgnoreCase) || cardType.Contains("Early", StringComparison.OrdinalIgnoreCase)))
            {
                return "";
            }
            if (string.IsNullOrEmpty(desc) || desc.Length <= 500) return desc ?? "";
            int idx = desc.LastIndexOf('.', 500);
            if (idx >= 0) return desc.Substring(0, idx + 1).Trim() + "...";
            return desc.Substring(0, 500).Trim() + "...";
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Banner };
        public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<RemoteImageInfo>());

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }
    }
}