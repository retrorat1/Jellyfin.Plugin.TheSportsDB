namespace Jellyfin.Plugin.TheSportsDB.Providers;

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

public class TheSportsDBMetadataProvider : IRemoteMetadataProvider<Series, SeriesInfo>
{
    private readonly TheSportsDbClient _client;
    private readonly ILogger<TheSportsDBMetadataProvider> _logger;

    public string Name => "TheSportsDB";

    public TheSportsDBMetadataProvider(IHttpClientFactory httpClientFactory, ILogger<TheSportsDBMetadataProvider> logger, ILogger<TheSportsDbClient> clientLogger)
    {
        _client = new TheSportsDbClient(httpClientFactory, clientLogger);
        _logger = logger;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        _logger.LogInformation("TheSportsDB: Searching for {Name}", searchInfo.Name);

        var result = await _client.SearchLeagueAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
        var list = new List<RemoteSearchResult>();

        if (result?.countrys != null)
        {
            foreach (var league in result.countrys)
            {
                list.Add(new RemoteSearchResult
                {
                    Name = league.strLeague,
                    ProviderIds = { { "TheSportsDB", league.idLeague } },
                    ProductionYear = int.TryParse(league.intFormedYear, out var year) ? year : null,
                    ImageUrl = league.strBadge ?? league.strLogo
                });
            }
        }
        
        // Sometimes results are in 'leagues' instead of 'countrys' depending on the endpoint used (search vs lookup)
        // But SearchLeagueAsync uses search_all_leagues.php which returns 'countrys' or 'leagues'
        if (result?.leagues != null)
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
                        ImageUrl = league.strBadge ?? league.strLogo
                    });
                }
            }
        }

        return list;
    }

    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        _logger.LogInformation("TheSportsDB: Getting metadata for {Name}", info.Name);

        var id = info.GetProviderId("TheSportsDB");
        if (string.IsNullOrEmpty(id))
        {
             // If we don't have an ID, try to search first? 
             // Ideally Jellyfin calls GetSearchResults first, so we should have an ID if the user selected it.
             // Usually implicit search happens if ID is missing.
             var searchResults = await GetSearchResults(info, cancellationToken).ConfigureAwait(false);
             var first = searchResults.FirstOrDefault();
             if (first != null)
             {
                 id = first.ProviderIds["TheSportsDB"];
             }
        }

        if (string.IsNullOrEmpty(id))
        {
            return new MetadataResult<Series>();
        }

        var result = await _client.GetLeagueAsync(id, cancellationToken).ConfigureAwait(false);
        var league = result?.leagues?.FirstOrDefault();

        if (league == null)
        {
            return new MetadataResult<Series>();
        }

        var series = new Series
        {
            Name = league.strLeague,
            Overview = league.strDescriptionEN,
            ExternalId = league.idLeague,
            ProductionYear = int.TryParse(league.intFormedYear, out var year) ? year : null,
            HomePageUrl = string.IsNullOrEmpty(league.strWebsite) ? null : (!league.strWebsite.StartsWith("http") ? "http://" + league.strWebsite : league.strWebsite)
        };

        series.SetProviderId("TheSportsDB", league.idLeague);

        return new MetadataResult<Series>
        {
            Item = series,
            HasMetadata = true
        };
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        // Not used as we return ImageUrl in RemoteSearchResult
        // But if needed for specific image fetching (like FanArt), this would be used.
        // For IRemoteMetadataProvider, this method is often just a proxy or standard Get.
        // However, IRemoteMetadataProvider inherits from IRemoteImageProvider? No, it doesn't always.
        // Wait, IRemoteMetadataProvider interface definition:
        // public interface IRemoteMetadataProvider<TItemType, in TLookupInfoType> : IMetadataProvider<TItemType, TLookupInfoType>, IRemoteMetadataProvider, IMetadataProvider, IRemoteSearchProvider<TItemType, TLookupInfoType>, IRemoteSearchProvider
        throw new NotImplementedException();
    }
}
