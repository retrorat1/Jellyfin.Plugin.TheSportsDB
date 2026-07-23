using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
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
    /// Season posters + season.nfo: match league+season from path first, scrape season page once
    /// when Primary/local poster is missing, attach Primary, write/enrich season.nfo in the season folder.
    /// Skips scrape when poster already present (Jellyfin force Replace All Images still hits GetImages).
    /// Episode/game providers never scrape season pages.
    /// </summary>
    public class TheSportsDBSeasonProvider
        : IRemoteMetadataProvider<Season, SeasonInfo>, IRemoteImageProvider
    {
        private readonly TheSportsDbClient _client;
        private readonly ILogger<TheSportsDBSeasonProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SportsResolverDb _sportsResolverDb;

        public string Name => "TheSportsDB";

        public bool Supports(BaseItem item) => item is Season;

        public TheSportsDBSeasonProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<TheSportsDBSeasonProvider> logger,
            ILogger<TheSportsDbClient> clientLogger,
            IApplicationPaths applicationPaths)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _client = new TheSportsDbClient(httpClientFactory, clientLogger);
            _sportsResolverDb = new SportsResolverDb(applicationPaths);
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeasonInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        /// <summary>
        /// First-scan path: resolve identity → scrape once if no poster yet → SetImage Primary →
        /// write season.nfo. Later refreshes skip scrape when local Primary/NFO art already exists.
        /// </summary>
        public async Task<MetadataResult<Season>> GetMetadata(
            SeasonInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>();

            var identity = ResolveSeasonIdentity(
                info.SeriesProviderIds,
                seriesName: null,
                info.IndexNumber,
                info.Name,
                info.Path);

            if (identity == null)
                return result;

            var seasonKey = identity.Season;
            var seasonFolder = SeasonNfoWriter.GetSeasonFolderPath(info.Path);

            // Prefer path folder identity (2022 → 2022; 2025-2026 → Name 2025-2026 / Index 20252026)
            // so episodes' ParentIndexNumber links to this season.
            int? indexNumber = info.IndexNumber;
            if (SeasonNfoWriter.TryGetSeasonFolderInfo(info.Path, out var pathKey, out var pathIndex))
            {
                seasonKey = pathKey;
                indexNumber = pathIndex;
            }
            else if (SeasonNfoWriter.TryParseSeasonFolderName(seasonKey, out var parsedKey, out var parsedIndex))
            {
                seasonKey = parsedKey;
                indexNumber = parsedIndex;
            }

            result.HasMetadata = true;
            result.Item = new Season
            {
                IndexNumber = indexNumber,
                Name = seasonKey
            };

            if (TryParseSeasonYear(seasonKey, out var year))
                result.Item.ProductionYear = year;

            result.Item.ProviderIds["TheSportsDB"] = seasonKey;
            result.Item.ProviderIds["TheSportsDBSeries"] = identity.LeagueId;

            // Scrape / disk writes must not fail the metadata refresh: Primary remote URL
            // is enough for Jellyfin's metadata library when media folder isn't writable.
            string? posterUrl = null;
            try
            {
                var hasLocalArt = SeasonNfoWriter.HasLocalPrimaryImage(seasonFolder)
                                  || SeasonNfoWriter.HasNfoPoster(seasonFolder);

                if (hasLocalArt)
                {
                    _logger.LogInformation(
                        "TheSportsDB: Season \"{Season}\" already has local poster/NFO art — skipping scrape",
                        seasonKey);
                }
                else
                {
                    _logger.LogInformation(
                        "TheSportsDB: Season \"{Season}\" has no Primary yet — scraping season page once",
                        seasonKey);

                    var art = await ScrapeWithIdentityAsync(identity, cancellationToken)
                        .ConfigureAwait(false);
                    posterUrl = FirstNonEmpty(art?.strPoster, art?.strBadge);

                    if (!string.IsNullOrEmpty(posterUrl))
                    {
                        // Set remote Primary first — works even when poster.jpg can't be written
                        result.Item.SetImage(
                            new ItemImageInfo { Type = ImageType.Primary, Path = posterUrl }, 0);
                        result.RemoteImages.Add((posterUrl, ImageType.Primary));

                        if (!string.IsNullOrEmpty(seasonFolder))
                        {
                            await TryDownloadPosterAsync(seasonFolder, posterUrl, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "TheSportsDB: No season art scraped for league {LeagueId} season \"{Season}\"",
                            identity.LeagueId,
                            seasonKey);
                    }
                }

                if (!string.IsNullOrEmpty(seasonFolder))
                {
                    SeasonNfoWriter.Write(
                        seasonFolder,
                        seasonKey,
                        identity.LeagueId,
                        indexNumber,
                        year > 0 ? year : null,
                        posterUrl,
                        _logger);
                }

                _logger.LogInformation(
                    "TheSportsDB: Season metadata matched league={LeagueId} season=\"{Season}\" " +
                    "(IndexNumber={Index}, scraped={Scraped})",
                    identity.LeagueId,
                    seasonKey,
                    indexNumber,
                    !hasLocalArt && !string.IsNullOrEmpty(posterUrl));
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(
                    ex,
                    "TheSportsDB: Permission denied during season art/NFO for \"{Season}\" in {Folder} — " +
                    "metadata/Primary URL still applied; grant jellyfin write access to media for on-disk files",
                    seasonKey,
                    seasonFolder ?? "(none)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "TheSportsDB: Season art/NFO step failed for \"{Season}\" — identity metadata still returned",
                    seasonKey);
            }

            return result;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
            => new[] { ImageType.Primary };

        /// <summary>
        /// Backup / force-replace path. Jellyfin skips this when Primary exists unless
        /// Replace All Images / remote image UI. Does not run for episode refreshes.
        /// </summary>
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();
            if (item is not Season season)
                return list;

            var hasPrimary = season.HasImage(ImageType.Primary);
            if (hasPrimary)
            {
                _logger.LogInformation(
                    "TheSportsDB: Season \"{Name}\" already has Primary; scraping because Jellyfin " +
                    "requested remote images (Replace All Images or remote image UI)",
                    season.Name ?? season.Path);
            }
            else
            {
                _logger.LogInformation(
                    "TheSportsDB: Season \"{Name}\" has no Primary — GetImages scraping season page",
                    season.Name ?? season.Path);
            }

            var seriesIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seriesId = season.Series?.GetProviderId("TheSportsDB")
                           ?? season.GetProviderId("TheSportsDBSeries");
            if (!string.IsNullOrEmpty(seriesId) && IsLikelyLeagueId(seriesId))
                seriesIds["TheSportsDB"] = seriesId;

            var match = await ScrapeSeasonArtAsync(
                seriesIds,
                season.Series?.Name,
                season.IndexNumber,
                season.Name,
                season.Path,
                cancellationToken).ConfigureAwait(false);

            if (match == null)
                return list;

            var seasonFolder = SeasonNfoWriter.GetSeasonFolderPath(season.Path);
            var posterUrl = FirstNonEmpty(match.strPoster, match.strBadge);

            if (!string.IsNullOrEmpty(posterUrl)
                && !string.IsNullOrEmpty(seasonFolder)
                && !SeasonNfoWriter.HasLocalPrimaryImage(seasonFolder))
            {
                await TryDownloadPosterAsync(seasonFolder, posterUrl, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(seasonFolder))
            {
                var seasonKey = season.GetProviderId("TheSportsDB")
                                ?? match.strSeason
                                ?? season.Name
                                ?? "unknown";
                var leagueId = seriesId
                               ?? season.GetProviderId("TheSportsDBSeries")
                               ?? "";

                // Prefer path folder identity (2025-2026 display / collapsed IndexNumber)
                int? indexNumber = season.IndexNumber;
                if (SeasonNfoWriter.TryGetSeasonFolderInfo(season.Path, out var pathKey, out var pathIndex))
                {
                    seasonKey = pathKey;
                    indexNumber = pathIndex;
                }
                else if (SeasonNfoWriter.TryParseSeasonFolderName(seasonKey, out var parsedKey, out var parsedIndex))
                {
                    seasonKey = parsedKey;
                    indexNumber = parsedIndex;
                }

                TryParseSeasonYear(seasonKey, out var year);
                SeasonNfoWriter.Write(
                    seasonFolder,
                    seasonKey,
                    leagueId,
                    indexNumber,
                    year > 0 ? year : null,
                    posterUrl,
                    _logger);
            }

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

        private SeasonIdentity? ResolveSeasonIdentity(
            Dictionary<string, string>? seriesProviderIds,
            string? seriesName,
            int? indexNumber,
            string? name,
            string? path)
        {
            var leagueId = ResolveLeagueId(seriesProviderIds, seriesName, path);
            if (string.IsNullOrEmpty(leagueId))
            {
                _logger.LogWarning(
                    "TheSportsDB: Season lookup skipped — series has no TheSportsDB provider ID " +
                    "(name={SeriesName}, path={Path}). Identify the series first.",
                    seriesName,
                    path);
                return null;
            }

            var candidates = BuildSeasonCandidates(indexNumber, name, path);
            if (candidates.Count == 0)
            {
                _logger.LogWarning(
                    "TheSportsDB: Season lookup skipped — could not derive season year " +
                    "(IndexNumber={Index}, Name={Name}, Path={Path}).",
                    indexNumber,
                    name,
                    path);
                return null;
            }

            var leagueSlug = _sportsResolverDb.GetLeagueSlug(leagueId);
            _logger.LogInformation(
                "TheSportsDB: Matched season identity league={LeagueId} season=\"{Season}\" " +
                "slug={Slug} (path/filename candidates=[{Candidates}])",
                leagueId,
                candidates[0],
                leagueSlug ?? "(none)",
                string.Join(", ", candidates));

            return new SeasonIdentity(leagueId, candidates, leagueSlug);
        }

        private async Task<SeasonArt?> ScrapeSeasonArtAsync(
            Dictionary<string, string>? seriesProviderIds,
            string? seriesName,
            int? indexNumber,
            string? name,
            string? path,
            CancellationToken cancellationToken)
        {
            var identity = ResolveSeasonIdentity(
                seriesProviderIds, seriesName, indexNumber, name, path);
            if (identity == null)
                return null;

            return await ScrapeWithIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        }

        private async Task<SeasonArt?> ScrapeWithIdentityAsync(
            SeasonIdentity identity, CancellationToken cancellationToken)
        {
            foreach (var candidate in identity.Candidates)
            {
                var art = await _client.GetSeasonArtBySeasonAsync(
                    identity.LeagueId, candidate, cancellationToken, identity.LeagueSlug)
                    .ConfigureAwait(false);
                if (art == null
                    || (string.IsNullOrEmpty(art.strPoster) && string.IsNullOrEmpty(art.strBadge)))
                {
                    continue;
                }

                var imageUrl = FirstNonEmpty(art.strPoster, art.strBadge);
                _logger.LogInformation(
                    "TheSportsDB: Matched league {LeagueId} season \"{Season}\" then scraped poster {ImageUrl}",
                    identity.LeagueId,
                    candidate,
                    imageUrl);
                // Prefer the candidate that worked as strSeason
                art.strSeason = candidate;
                return art;
            }

            _logger.LogWarning(
                "TheSportsDB: Matched league {LeagueId} but scrape found no season art for [{Candidates}]. " +
                "Jellyfin may inherit the series primary image (often the current league poster).",
                identity.LeagueId,
                string.Join(", ", identity.Candidates));
            return null;
        }

        private async Task TryDownloadPosterAsync(
            string seasonFolder, string imageUrl, CancellationToken cancellationToken)
        {
            var dest = Path.Combine(seasonFolder, "poster.jpg");
            if (File.Exists(dest))
                return;

            try
            {
                using var client = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await client.GetAsync(imageUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "TheSportsDB: Poster download HTTP {Status} for {Url}",
                        (int)response.StatusCode,
                        imageUrl);
                    return;
                }

                await using var fs = new FileStream(
                    dest, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("TheSportsDB: Saved season poster to {Path}", dest);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(
                    ex,
                    "TheSportsDB: Permission denied saving poster.jpg in {Folder} — " +
                    "Primary remote URL still applied via metadata library",
                    seasonFolder);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TheSportsDB: Failed saving poster.jpg in {Folder}", seasonFolder);
            }
        }

        private string? ResolveLeagueId(
            Dictionary<string, string>? seriesProviderIds,
            string? seriesName,
            string? path)
        {
            if (seriesProviderIds != null
                && seriesProviderIds.TryGetValue("TheSportsDB", out var fromSeries)
                && !string.IsNullOrWhiteSpace(fromSeries)
                && IsLikelyLeagueId(fromSeries))
            {
                return fromSeries.Trim();
            }

            var fromName = ResolveLeagueIdByName(seriesName);
            if (!string.IsNullOrEmpty(fromName))
                return fromName;

            // Parent of season folder is typically the league folder
            // /Sports/FIFA World Cup/2022 → "FIFA World Cup"
            if (!string.IsNullOrEmpty(path))
            {
                var seasonFolder = SeasonNfoWriter.GetSeasonFolderPath(path);
                if (!string.IsNullOrEmpty(seasonFolder))
                {
                    var leagueFolderPath = Path.GetDirectoryName(seasonFolder);
                    var leagueFolder = Path.GetFileName(leagueFolderPath);
                    var fromFolder = ResolveLeagueIdByName(leagueFolder);
                    if (!string.IsNullOrEmpty(fromFolder))
                        return fromFolder;
                }
            }

            return null;
        }

        private string? ResolveLeagueIdByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            name = name.Trim();
            var config = Plugin.Instance?.Configuration;
            if (config?.LeagueMappings != null)
            {
                var map = config.LeagueMappings.FirstOrDefault(x =>
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (map != null && !string.IsNullOrEmpty(map.LeagueId))
                {
                    _logger.LogInformation(
                        "TheSportsDB: Season league from config mapping \"{Name}\" → {Id}", name, map.LeagueId);
                    return map.LeagueId;
                }
            }

            var dbId = _sportsResolverDb.GetLeagueIdFromAlias(name);
            if (!string.IsNullOrEmpty(dbId))
            {
                _logger.LogInformation(
                    "TheSportsDB: Season league from DB alias \"{Name}\" → {Id}", name, dbId);
                return dbId;
            }

            return null;
        }

        private static bool IsLikelyLeagueId(string value)
            => Regex.IsMatch(value.Trim(), @"^\d{3,6}$");

        /// <summary>
        /// Ordered lookup keys: path folder first (2025-2026), then expanded collapsed IndexNumber
        /// (20252026 → 2025-2026), then name. Never prefer bare 20252026 over hyphenated form.
        /// </summary>
        private static List<string> BuildSeasonCandidates(int? indexNumber, string? name, string? path)
        {
            var list = new List<string>();

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                value = value.Trim();
                value = Regex.Replace(value, @"^(Season|Series)\s+", "", RegexOptions.IgnoreCase).Trim();
                if (value.Length == 0) return;

                // Prefer expanded 2025-2026; never scrape with collapsed 20252026
                var expanded = SeasonNfoWriter.TryExpandCollapsedSeason(value);
                if (!string.IsNullOrEmpty(expanded))
                {
                    if (!list.Contains(expanded, StringComparer.OrdinalIgnoreCase))
                        list.Add(expanded);
                }
                else if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(value);
                }

                // Bare years from "FIFA World Cup 2022" or "2025-2026"
                foreach (Match ym in Regex.Matches(value, @"(?<!\d)((?:19|20)\d{2})(?!\d)"))
                {
                    var y = ym.Groups[1].Value;
                    if (!list.Contains(y, StringComparer.OrdinalIgnoreCase))
                        list.Add(y);
                }
            }

            // 1) Path folder (…/EPL/2025-2026 → "2025-2026") — highest priority
            if (!string.IsNullOrEmpty(path))
            {
                var folder = Path.GetFileName(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                // Ignore NFO/image filenames if Path ever points at a file
                if (!string.IsNullOrEmpty(folder)
                    && !folder.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)
                    && !folder.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    && !folder.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    && !folder.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                    && !folder.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    Add(folder);
                }
                else
                {
                    var seasonFolder = SeasonNfoWriter.GetSeasonFolderPath(path);
                    if (!string.IsNullOrEmpty(seasonFolder))
                        Add(Path.GetFileName(seasonFolder));
                }
            }

            // 2) IndexNumber: calendar year OR collapsed split season (20252026)
            if (indexNumber is >= 1900 and <= 2100)
            {
                Add(indexNumber.Value.ToString(CultureInfo.InvariantCulture));
            }
            else if (indexNumber is >= 19001900 and <= 21002100)
            {
                Add(indexNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            // 3) Display name (may be "Season 20252026" from Jellyfin)
            Add(name);

            // Last resort
            if (list.Count == 0 && indexNumber.HasValue)
                Add(indexNumber.Value.ToString(CultureInfo.InvariantCulture));

            return list;
        }

        private static string? FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

        private static string NormalizeSeasonKey(string season)
            => season.Trim().Replace('/', '-');

        private static bool TryParseSeasonYear(string season, out int year)
        {
            year = 0;
            var m = Regex.Match(NormalizeSeasonKey(season), @"(\d{4})$");
            return m.Success && int.TryParse(m.Groups[1].Value, out year);
        }

        private sealed class SeasonIdentity
        {
            public SeasonIdentity(string leagueId, List<string> candidates, string? leagueSlug)
            {
                LeagueId = leagueId;
                Candidates = candidates;
                LeagueSlug = leagueSlug;
            }

            public string LeagueId { get; }
            public List<string> Candidates { get; }
            public string Season => Candidates[0];
            public string? LeagueSlug { get; }
        }
    }
}
