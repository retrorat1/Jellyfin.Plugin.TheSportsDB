using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
    public class TheSportsDBMetadataProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IRemoteImageProvider
    {
        private readonly TheSportsDbClient _client;
        private readonly ILogger<TheSportsDBMetadataProvider> _logger;
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

        public string Name => "TheSportsDB";

        public bool Supports(BaseItem item)
        {
            return item is Series;
        }

        public TheSportsDBMetadataProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<TheSportsDBMetadataProvider> logger,
            ILogger<TheSportsDbClient> clientLogger
        )
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _client = new TheSportsDbClient(httpClientFactory, clientLogger);
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            _logger.LogInformation("TheSportsDB: Searching for {Name}", searchInfo.Name);

            var result = await _client.SearchLeagueAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            var list = new List<RemoteSearchResult>();

            if (result == null)
            {
                _logger.LogWarning("TheSportsDB: Search result for {Name} was null", searchInfo.Name);
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
                    // De-duplicate
                    if (!list.Any(x => x.ProviderIds.ContainsKey("TheSportsDB") && x.ProviderIds["TheSportsDB"] == league.idLeague))
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

            _logger.LogInformation("TheSportsDB: Found {Count} results for {Name}", list.Count, searchInfo.Name);

            // Optionally add mappings or fallback logic here

            return list;
        }

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            _logger.LogInformation("TheSportsDB: Getting metadata for {Name}", info.Name);

            var id = info.GetProviderId("TheSportsDB");
            if (string.IsNullOrEmpty(id))
            {
                KnownLeagueIds.TryGetValue(info.Name?.Trim() ?? "", out id);
            }

            if (string.IsNullOrEmpty(id))
            {
                var config = Plugin.Instance?.Configuration;
                if (config?.LeagueMappings != null)
                {
                    var map = config.LeagueMappings.FirstOrDefault(x =>
                        string.Equals(x.Name, info.Name, StringComparison.OrdinalIgnoreCase));
                    if (map != null)
                        id = map.LeagueId;
                }
            }

            var result = new MetadataResult<Series>();
            if (!string.IsNullOrEmpty(id))
            {
                var leagueInfo = await _client.GetLeagueAsync(id, cancellationToken).ConfigureAwait(false);
                var league = leagueInfo?.leagues?.FirstOrDefault();
                if (league != null)
                {
                    result.Item = new Series
                    {
                        Name = league.strLeague,
                        Overview = league.strDescriptionEN,
                        ProductionYear = int.TryParse(league.intFormedYear, out var y) ? y : (int?)null,
                        PremiereDate = DateTime.TryParse(league.dateFirstEvent, out var d) ? d : (DateTime?)null
                    };
                    result.HasMetadata = true;

                    result.ProviderIds["TheSportsDB"] = league.idLeague;
                    if (!string.IsNullOrEmpty(league.strPoster))
                        result.Item.SetImage(ImageType.Primary, league.strPoster);
                    else if (!string.IsNullOrEmpty(league.strBadge))
                        result.Item.SetImage(ImageType.Primary, league.strBadge);
                    else if (!string.IsNullOrEmpty(league.strLogo))
                        result.Item.SetImage(ImageType.Primary, league.strLogo);

                    if (!string.IsNullOrEmpty(league.strFanart1))
                        result.Item.SetImage(ImageType.Backdrop, league.strFanart1);
                    if (!string.IsNullOrEmpty(league.strFanart2))
                        result.Item.AddImage(ImageType.Backdrop, league.strFanart2);
                    if (!string.IsNullOrEmpty(league.strFanart3))
                        result.Item.AddImage(ImageType.Backdrop, league.strFanart3);
                    if (!string.IsNullOrEmpty(league.strFanart4))
                        result.Item.AddImage(ImageType.Backdrop, league.strFanart4);

                    if (!string.IsNullOrEmpty(league.strBanner))
                        result.Item.SetImage(ImageType.Banner, league.strBanner);
                }
            }
            return result;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Banner };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();
            var id = item.GetProviderId("TheSportsDB");

            if (string.IsNullOrEmpty(id))
            {
                return list;
            }

            var result = await _client.GetLeagueAsync(id, cancellationToken).ConfigureAwait(false);
            var league = result?.leagues?.FirstOrDefault();

            if (league != null)
            {
                if (!string.IsNullOrEmpty(league.strPoster))
                    list.Add(new RemoteImageInfo { Url = league.strPoster, Type = ImageType.Primary, ProviderName = "TheSportsDB" });
                else if (!string.IsNullOrEmpty(league.strBadge))
                    list.Add(new RemoteImageInfo { Url = league.strBadge, Type = ImageType.Primary, ProviderName = "TheSportsDB" });
                else if (!string.IsNullOrEmpty(league.strLogo))
                    list.Add(new RemoteImageInfo { Url = league.strLogo, Type = ImageType.Primary, ProviderName = "TheSportsDB" });

                if (!string.IsNullOrEmpty(league.strFanart1))
                    list.Add(new RemoteImageInfo { Url = league.strFanart1, Type = ImageType.Backdrop, ProviderName = "TheSportsDB" });
                if (!string.IsNullOrEmpty(league.strFanart2))
                    list.Add(new RemoteImageInfo { Url = league.strFanart2, Type = ImageType.Backdrop, ProviderName = "TheSportsDB" });
                if (!string.IsNullOrEmpty(league.strFanart3))
                    list.Add(new RemoteImageInfo { Url = league.strFanart3, Type = ImageType.Backdrop, ProviderName = "TheSportsDB" });
                if (!string.IsNullOrEmpty(league.strFanart4))
                    list.Add(new RemoteImageInfo { Url = league.strFanart4, Type = ImageType.Backdrop, ProviderName = "TheSportsDB" });

                if (!string.IsNullOrEmpty(league.strBanner))
                    list.Add(new RemoteImageInfo { Url = league.strBanner, Type = ImageType.Banner, ProviderName = "TheSportsDB" });
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