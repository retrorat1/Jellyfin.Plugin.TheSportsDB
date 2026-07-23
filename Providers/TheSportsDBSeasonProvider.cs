using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// Fetches per-season posters/badges from TheSportsDB (search_all_seasons.php).
    /// Matches Jellyfin season folders like "2022" or "2025-2026" to strSeason.
    /// </summary>
    public class TheSportsDBSeasonProvider
        : IRemoteMetadataProvider<Season, SeasonInfo>, IRemoteImageProvider
    {
        private readonly TheSportsDbClient _client;
        private readonly ILogger<TheSportsDBSeasonProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public string Name => "TheSportsDB";

        public bool Supports(BaseItem item) => item is Season;

        public TheSportsDBSeasonProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<TheSportsDBSeasonProvider> logger,
            ILogger<TheSportsDbClient> clientLogger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _client = new TheSportsDbClient(httpClientFactory, clientLogger);
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeasonInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public async Task<MetadataResult<Season>> GetMetadata(
            SeasonInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>();

            var match = await FindSeasonArtAsync(
                info.SeriesProviderIds,
                info.IndexNumber,
                info.Name,
                info.Path,
                cancellationToken).ConfigureAwait(false);

            if (match == null)
                return result;

            result.HasMetadata = true;
            result.Item = new Season
            {
                IndexNumber = info.IndexNumber,
                Name = match.strSeason
            };

            // Prefer season poster, fall back to season badge (same priority as series)
            var imageUrl = match.strPoster ?? match.strBadge;
            if (!string.IsNullOrEmpty(imageUrl))
            {
                result.Item.SetImage(
                    new ItemImageInfo { Type = ImageType.Primary, Path = imageUrl }, 0);
            }

            if (TryParseSeasonYear(match.strSeason, out var year))
                result.Item.ProductionYear = year;

            result.Item.ProviderIds["TheSportsDB"] = match.strSeason;
            _logger.LogInformation(
                "TheSportsDB: Season metadata matched \"{Season}\" (poster={HasPoster}, badge={HasBadge})",
                match.strSeason,
                !string.IsNullOrEmpty(match.strPoster),
                !string.IsNullOrEmpty(match.strBadge));

            return result;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
            => new[] { ImageType.Primary };

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();
            if (item is not Season season)
                return list;

            var seriesIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seriesId = season.Series?.GetProviderId("TheSportsDB");
            if (!string.IsNullOrEmpty(seriesId))
                seriesIds["TheSportsDB"] = seriesId;

            var match = await FindSeasonArtAsync(
                seriesIds,
                season.IndexNumber,
                season.Name,
                season.Path,
                cancellationToken).ConfigureAwait(false);

            if (match == null)
                return list;

            if (!string.IsNullOrEmpty(match.strPoster))
            {
                list.Add(new RemoteImageInfo
                {
                    Url = match.strPoster,
                    Type = ImageType.Primary,
                    ProviderName = Name
                });
            }

            if (!string.IsNullOrEmpty(match.strBadge))
            {
                list.Add(new RemoteImageInfo
                {
                    Url = match.strBadge,
                    Type = ImageType.Primary,
                    ProviderName = Name
                });
            }

            return list;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);

        private async Task<SeasonArt?> FindSeasonArtAsync(
            Dictionary<string, string>? seriesProviderIds,
            int? indexNumber,
            string? name,
            string? path,
            CancellationToken cancellationToken)
        {
            string? leagueId = null;
            seriesProviderIds?.TryGetValue("TheSportsDB", out leagueId);

            if (string.IsNullOrEmpty(leagueId))
            {
                _logger.LogWarning(
                    "TheSportsDB: Season image lookup skipped — series has no TheSportsDB provider ID.");
                return null;
            }

            var seasons = await _client.GetSeasonsAsync(leagueId, cancellationToken).ConfigureAwait(false);
            if (seasons.Count == 0)
            {
                _logger.LogWarning(
                    "TheSportsDB: No seasons returned for league {LeagueId}.", leagueId);
                return null;
            }

            var candidates = BuildSeasonCandidates(indexNumber, name, path);
            _logger.LogInformation(
                "TheSportsDB: Matching season art for league {LeagueId}; candidates=[{Candidates}], apiSeasons={Count}",
                leagueId,
                string.Join(", ", candidates),
                seasons.Count);

            foreach (var candidate in candidates)
            {
                var exact = seasons.FirstOrDefault(s =>
                    string.Equals(NormalizeSeasonKey(s.strSeason), NormalizeSeasonKey(candidate),
                        StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                    return exact;
            }

            // Year-only folder (e.g. 2022) against split seasons (e.g. 2021-2022): prefer ending year
            foreach (var candidate in candidates)
            {
                if (!Regex.IsMatch(candidate, @"^\d{4}$"))
                    continue;

                var byEndYear = seasons.FirstOrDefault(s =>
                    NormalizeSeasonKey(s.strSeason).EndsWith("-" + candidate, StringComparison.OrdinalIgnoreCase)
                    || NormalizeSeasonKey(s.strSeason).EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase));
                if (byEndYear != null)
                    return byEndYear;
            }

            _logger.LogWarning(
                "TheSportsDB: No season art match for candidates [{Candidates}] in league {LeagueId}.",
                string.Join(", ", candidates),
                leagueId);
            return null;
        }

        /// <summary>
        /// Build ordered lookup keys from folder name / IndexNumber / path.
        /// </summary>
        private static List<string> BuildSeasonCandidates(int? indexNumber, string? name, string? path)
        {
            var list = new List<string>();

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                value = value.Trim();
                // Strip common "Season " prefix from Jellyfin display names
                value = Regex.Replace(value, @"^Season\s+", "", RegexOptions.IgnoreCase).Trim();
                if (value.Length == 0) return;
                if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
                    list.Add(value);
            }

            Add(name);

            if (!string.IsNullOrEmpty(path))
            {
                var folder = System.IO.Path.GetFileName(
                    path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                Add(folder);
            }

            if (indexNumber.HasValue)
                Add(indexNumber.Value.ToString(CultureInfo.InvariantCulture));

            return list;
        }

        private static string NormalizeSeasonKey(string season)
            => season.Trim().Replace('/', '-');

        private static bool TryParseSeasonYear(string season, out int year)
        {
            year = 0;
            var m = Regex.Match(NormalizeSeasonKey(season), @"(\d{4})$");
            return m.Success && int.TryParse(m.Groups[1].Value, out year);
        }
    }
}
