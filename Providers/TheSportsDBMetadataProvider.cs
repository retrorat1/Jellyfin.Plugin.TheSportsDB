using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
    public class TheSportsDBMetadataProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IRemoteImageProvider
    {
        private readonly TheSportsDbClient _client;
        private readonly ILogger<TheSportsDBMetadataProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SportsResolverDb _sportsResolverDb;

        public string Name => "TheSportsDB";

        public bool Supports(BaseItem item) => item is Series;

        public TheSportsDBMetadataProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<TheSportsDBMetadataProvider> logger,
            ILogger<TheSportsDbClient> clientLogger,
            IApplicationPaths applicationPaths)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _client = new TheSportsDbClient(httpClientFactory, clientLogger);
            _sportsResolverDb = new SportsResolverDb(applicationPaths);
        }

        /// <summary>
        /// Resolve a league ID from: stored provider ID → config mappings → DB alias.
        /// Same priority order as TheSportsDBEpisodeProvider.
        /// </summary>
        private string? ResolveLeagueId(string seriesName, string? storedId)
        {
            // 1. Stored provider ID (set on a previous scan)
            if (!string.IsNullOrEmpty(storedId))
            {
                _logger.LogInformation("TheSportsDB: Stored provider ID: \"{N}\" → \"{Id}\"", seriesName, storedId);
                return storedId;
            }

            // 2. Config mappings (Dashboard > Plugins > TheSportsDB)
            var config = Plugin.Instance?.Configuration;
            if (config?.LeagueMappings != null)
            {
                var map = config.LeagueMappings.FirstOrDefault(x =>
                    string.Equals(x.Name, seriesName, StringComparison.OrdinalIgnoreCase));
                if (map != null)
                {
                    _logger.LogInformation("TheSportsDB: Config mapping: \"{N}\" → \"{Id}\"", seriesName, map.LeagueId);
                    return map.LeagueId;
                }
            }

            // 3. DB alias lookup (sports_resolver.db — covers all leagues in the bundled database)
            var dbId = _sportsResolverDb.GetLeagueIdFromAlias(seriesName);
            if (!string.IsNullOrEmpty(dbId))
            {
                _logger.LogInformation("TheSportsDB: DB alias: \"{N}\" → \"{Id}\"", seriesName, dbId);
                return dbId;
            }

            _logger.LogWarning(
                "TheSportsDB: No league mapping for \"{N}\". Add a mapping in Dashboard > Plugins > TheSportsDB.",
                seriesName);
            return null;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            _logger.LogInformation("TheSportsDB: Searching for \"{Name}\"", searchInfo.Name);

            var result = await _client.SearchLeagueAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            var list = new List<RemoteSearchResult>();

            if (result == null)
            {
                _logger.LogWarning("TheSportsDB: Search result for \"{Name}\" was null", searchInfo.Name);
                return list;
            }

            if (result.countrys != null)
            {
                foreach (var league in result.countrys)
                {
                    list.Add(new RemoteSearchResult
                    {
                        Name = league.strLeague,
                        ProviderIds = { { "TheSportsDB", league.idLeague } },
                        ProductionYear = int.TryParse(league.intFormedYear, out var year) ? year : null,
                        ImageUrl = league.strPoster ?? league.strBadge ?? league.strLogo
                    });
                }
            }

            if (result.leagues != null)
            {
                foreach (var league in result.leagues)
                {
                    if (!list.Any(x => x.ProviderIds.ContainsKey("TheSportsDB")
                                    && x.ProviderIds["TheSportsDB"] == league.idLeague))
                    {
                        list.Add(new RemoteSearchResult
                        {
                            Name = league.strLeague,
                            ProviderIds = { { "TheSportsDB", league.idLeague } },
                            ProductionYear = int.TryParse(league.intFormedYear, out var year) ? year : null,
                            ImageUrl = league.strPoster ?? league.strBadge ?? league.strLogo
                        });
                    }
                }
            }

            _logger.LogInformation("TheSportsDB: Found {Count} results for \"{Name}\"", list.Count, searchInfo.Name);
            return list;
        }

        public async Task<MetadataResult<Series>> GetMetadata(
            SeriesInfo info, CancellationToken cancellationToken)
        {
            _logger.LogInformation("TheSportsDB: Getting metadata for \"{Name}\"", info.Name);

            var id = ResolveLeagueId(info.Name?.Trim() ?? "", info.GetProviderId("TheSportsDB"));

            var result = new MetadataResult<Series>();
            if (string.IsNullOrEmpty(id))
                return result;

            var leagueInfo = await _client.GetLeagueAsync(id, cancellationToken).ConfigureAwait(false);
            var league = leagueInfo?.leagues?.FirstOrDefault();
            if (league == null)
                return result;

            result.Item = new Series
            {
                Name = league.strLeague,
                Overview = league.strDescriptionEN,
                ProductionYear = int.TryParse(league.intFormedYear, out var y) ? y : (int?)null,
                PremiereDate = DateTime.TryParse(league.dateFirstEvent, out var d) ? d : (DateTime?)null
            };
            result.HasMetadata = true;
            result.Item.ProviderIds["TheSportsDB"] = league.idLeague;

            // Primary image: poster → badge → logo
            if (!string.IsNullOrEmpty(league.strPoster))
                result.Item.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = league.strPoster }, 0);
            else if (!string.IsNullOrEmpty(league.strBadge))
                result.Item.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = league.strBadge }, 0);
            else if (!string.IsNullOrEmpty(league.strLogo))
                result.Item.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = league.strLogo }, 0);

            // Backdrops
            if (!string.IsNullOrEmpty(league.strFanart1))
                result.Item.SetImage(new ItemImageInfo { Type = ImageType.Backdrop, Path = league.strFanart1 }, 0);
            if (!string.IsNullOrEmpty(league.strFanart2))
                result.Item.AddImage(new ItemImageInfo { Type = ImageType.Backdrop, Path = league.strFanart2 });
            if (!string.IsNullOrEmpty(league.strFanart3))
                result.Item.AddImage(new ItemImageInfo { Type = ImageType.Backdrop, Path = league.strFanart3 });
            if (!string.IsNullOrEmpty(league.strFanart4))
                result.Item.AddImage(new ItemImageInfo { Type = ImageType.Backdrop, Path = league.strFanart4 });

            // Banner
            if (!string.IsNullOrEmpty(league.strBanner))
                result.Item.SetImage(new ItemImageInfo { Type = ImageType.Banner, Path = league.strBanner }, 0);

            return result;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
            => new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Banner };

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();

            // Use stored provider ID first, fall back to resolving by name
            var id = item.GetProviderId("TheSportsDB");
            if (string.IsNullOrEmpty(id))
            {
                id = ResolveLeagueId(item.Name?.Trim() ?? "", null);
                if (string.IsNullOrEmpty(id))
                    return list;
            }

            var result = await _client.GetLeagueAsync(id, cancellationToken).ConfigureAwait(false);
            var league = result?.leagues?.FirstOrDefault();
            if (league == null) return list;

            if (!string.IsNullOrEmpty(league.strPoster))
                list.Add(new RemoteImageInfo { Url = league.strPoster, Type = ImageType.Primary, ProviderName = Name });
            else if (!string.IsNullOrEmpty(league.strBadge))
                list.Add(new RemoteImageInfo { Url = league.strBadge, Type = ImageType.Primary, ProviderName = Name });
            else if (!string.IsNullOrEmpty(league.strLogo))
                list.Add(new RemoteImageInfo { Url = league.strLogo, Type = ImageType.Primary, ProviderName = Name });

            if (!string.IsNullOrEmpty(league.strFanart1))
                list.Add(new RemoteImageInfo { Url = league.strFanart1, Type = ImageType.Backdrop, ProviderName = Name });
            if (!string.IsNullOrEmpty(league.strFanart2))
                list.Add(new RemoteImageInfo { Url = league.strFanart2, Type = ImageType.Backdrop, ProviderName = Name });
            if (!string.IsNullOrEmpty(league.strFanart3))
                list.Add(new RemoteImageInfo { Url = league.strFanart3, Type = ImageType.Backdrop, ProviderName = Name });
            if (!string.IsNullOrEmpty(league.strFanart4))
                list.Add(new RemoteImageInfo { Url = league.strFanart4, Type = ImageType.Backdrop, ProviderName = Name });

            if (!string.IsNullOrEmpty(league.strBanner))
                list.Add(new RemoteImageInfo { Url = league.strBanner, Type = ImageType.Banner, ProviderName = Name });

            return list;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => _httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
    }
}