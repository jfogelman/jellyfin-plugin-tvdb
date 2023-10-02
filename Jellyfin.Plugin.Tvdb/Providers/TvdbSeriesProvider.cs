using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using TvDbSharper;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.Tvdb.Providers
{
    /// <summary>
    /// Tvdb series provider.
    /// </summary>
    public class TvdbSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>
    {
        private const int MaxSearchResults = 10;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TvdbSeriesProvider> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly TvdbClientManager _tvdbClientManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvdbSeriesProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{TvdbSeriesProvider}"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="tvdbClientManager">Instance of <see cref="TvdbClientManager"/>.</param>
        public TvdbSeriesProvider(IHttpClientFactory httpClientFactory, ILogger<TvdbSeriesProvider> logger, ILibraryManager libraryManager, TvdbClientManager tvdbClientManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _libraryManager = libraryManager;
            _tvdbClientManager = tvdbClientManager;
        }

        /// <inheritdoc />
        public string Name => TvdbPlugin.ProviderName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            if (IsValidSeries(searchInfo.ProviderIds))
            {
                return await FetchSeriesSearchResult(searchInfo, cancellationToken).ConfigureAwait(false);
            }

            return await FindSeries(searchInfo.Name, searchInfo.Year, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>
            {
                QueriedById = true,
            };

            if (!IsValidSeries(info.ProviderIds))
            {
                result.QueriedById = false;
                await Identify(info).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (IsValidSeries(info.ProviderIds))
            {
                result.Item = new Series();
                result.HasMetadata = true;

                await FetchSeriesMetadata(result, info, cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);
        }

        /// <summary>
        /// Check whether a dictionary of provider IDs includes an entry for a valid TV metadata provider.
        /// </summary>
        /// <param name="ids">The provider IDs to check.</param>
        /// <returns>True, if the series contains a valid TV provider ID, otherwise false.</returns>
        internal static bool IsValidSeries(Dictionary<string, string> ids)
        {
            return (ids.TryGetValue(MetadataProvider.Tvdb.ToString(), out var tvdbId) && !string.IsNullOrEmpty(tvdbId))
                   || (ids.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId) && !string.IsNullOrEmpty(imdbId))
                   || (ids.TryGetValue(MetadataProvider.Zap2It.ToString(), out var zap2ItId) && !string.IsNullOrEmpty(zap2ItId));
        }

        private async Task<IEnumerable<RemoteSearchResult>> FetchSeriesSearchResult(SeriesInfo seriesInfo, CancellationToken cancellationToken)
        {
            var tvdbId = seriesInfo.GetProviderId(MetadataProvider.Tvdb);
            if (string.IsNullOrEmpty(tvdbId))
            {
                var imdbId = seriesInfo.GetProviderId(MetadataProvider.Imdb);
                if (!string.IsNullOrEmpty(imdbId))
                {
                    tvdbId = await GetSeriesByRemoteId(
                        imdbId,
                        MetadataProvider.Imdb.ToString(),
                        seriesInfo.MetadataLanguage,
                        seriesInfo.Name,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrEmpty(tvdbId))
            {
                var zap2ItId = seriesInfo.GetProviderId(MetadataProvider.Zap2It);
                if (!string.IsNullOrEmpty(zap2ItId))
                {
                    tvdbId = await GetSeriesByRemoteId(
                        zap2ItId,
                        MetadataProvider.Zap2It.ToString(),
                        seriesInfo.MetadataLanguage,
                        seriesInfo.Name,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                var seriesResult =
                    await _tvdbClientManager
                        .GetSeriesByIdAsync(Convert.ToInt32(tvdbId, CultureInfo.InvariantCulture), seriesInfo.MetadataLanguage, cancellationToken)
                        .ConfigureAwait(false);
                var artworkTypes = _tvdbClientManager.GetArtworkTypes(seriesInfo.MetadataLanguage, cancellationToken);
                return new[] { MapSeriesToRemoteSearchResult(seriesResult.Data, artworkTypes.Result.Data) };
            }
            catch (TvDbServerException e)
            {
                _logger.LogError(e, "Failed to retrieve series with id {TvdbId}:{SeriesName}", tvdbId, seriesInfo.Name);
                return Array.Empty<RemoteSearchResult>();
            }
        }

        private RemoteSearchResult MapSeriesToRemoteSearchResult(SeriesExtendedRecordDto series, ArtworkTypeDto[] artworkTypes)
        {
            var posterTypes = artworkTypes.FirstOrDefault(x => x.Name != null && x.Name.Equals("Poster", StringComparison.Ordinal)
                && x.RecordType.Equals("series", StringComparison.Ordinal)) ?? throw new ArgumentException("artworkTypes is null");
            var poster = series.Artworks.FirstOrDefault(x => x.Id == posterTypes.Id);
            var remoteResult = new RemoteSearchResult
            {
                Name = series.Name,
                Overview = series.Overview?.Trim() ?? string.Empty,
                SearchProviderName = Name,
                ImageUrl = poster?.Image
            };

            if (DateTime.TryParse(series.FirstAired, out var date))
            {
                // Dates from tvdb are either EST or capital of primary airing country.
                remoteResult.PremiereDate = date;
                remoteResult.ProductionYear = date.Year;
            }

            var imdbId = series.RemoteIds.FirstOrDefault(x => x.SourceName.Equals("IMDB", StringComparison.Ordinal));
            if (imdbId != null)
            {
                if (!string.IsNullOrEmpty(imdbId.Id))
                {
                    remoteResult.SetProviderId(MetadataProvider.Imdb, imdbId.Id);
                }
            }

            remoteResult.SetProviderId(MetadataProvider.Tvdb, series.Id.ToString(CultureInfo.InvariantCulture));

            return remoteResult;
        }

        private async Task FetchSeriesMetadata(
            MetadataResult<Series> result,
            SeriesInfo info,
            CancellationToken cancellationToken)
        {
            string metadataLanguage = info.MetadataLanguage;
            Dictionary<string, string> seriesProviderIds = info.ProviderIds;
            var series = result.Item;

            if (seriesProviderIds.TryGetValue(TvdbPlugin.ProviderId, out var tvdbId) && !string.IsNullOrEmpty(tvdbId))
            {
                series.SetProviderId(TvdbPlugin.ProviderId, tvdbId);
            }

            if (seriesProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId) && !string.IsNullOrEmpty(imdbId))
            {
                series.SetProviderId(MetadataProvider.Imdb, imdbId);
                tvdbId = await GetSeriesByRemoteId(
                    imdbId,
                    MetadataProvider.Imdb.ToString(),
                    metadataLanguage,
                    info.Name,
                    cancellationToken).ConfigureAwait(false);
            }

            if (seriesProviderIds.TryGetValue(MetadataProvider.Zap2It.ToString(), out var zap2It) && !string.IsNullOrEmpty(zap2It))
            {
                series.SetProviderId(MetadataProvider.Zap2It, zap2It);
                tvdbId = await GetSeriesByRemoteId(
                    zap2It,
                    MetadataProvider.Zap2It.ToString(),
                    metadataLanguage,
                    info.Name,
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var seriesResult =
                    await _tvdbClientManager
                        .GetSeriesByIdAsync(Convert.ToInt32(tvdbId, CultureInfo.InvariantCulture), metadataLanguage, cancellationToken)
                        .ConfigureAwait(false);
                await MapSeriesToResult(result, seriesResult.Data, metadataLanguage).ConfigureAwait(false);
            }
            catch (TvDbServerException e)
            {
                _logger.LogError(e, "Failed to retrieve series with id {TvdbId}:{SeriesName}", tvdbId, info.Name);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            result.ResetPeople();

            try
            {
                var actorsResult = await _tvdbClientManager
                    .GetActorsAsync(Convert.ToInt32(tvdbId, CultureInfo.InvariantCulture), metadataLanguage, cancellationToken).ConfigureAwait(false);
                MapActorsToResult(result, actorsResult);
            }
            catch (TvDbServerException e)
            {
                _logger.LogError(e, "Failed to retrieve actors for series {TvdbId}:{SeriesName}", tvdbId, info.Name);
            }
        }

        private async Task<string?> GetSeriesByRemoteId(string id, string idType, string language, string seriesName, CancellationToken cancellationToken)
        {
            TvDbApiResponse<SearchResultDto[]>? result = null;

            try
            {
                if (string.Equals(idType, MetadataProvider.Zap2It.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    result = await _tvdbClientManager.GetSeriesByZap2ItIdAsync(id, language, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    result = await _tvdbClientManager.GetSeriesByImdbIdAsync(id, language, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (TvDbServerException e)
            {
                _logger.LogError(e, "Failed to retrieve series with remote id {RemoteId}:{SeriesName}", id, seriesName);
            }

            return result?.Data.FirstOrDefault()?.Id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Finds the series.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="year">The year.</param>
        /// <param name="language">The language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{System.String}.</returns>
        private async Task<IEnumerable<RemoteSearchResult>> FindSeries(string name, int? year, string language, CancellationToken cancellationToken)
        {
            _logger.LogDebug("TvdbSearch: Finding id for item: {0} ({1})", name, year);
            var results = await FindSeriesInternal(name, language, cancellationToken).ConfigureAwait(false);

            return results.Where(i =>
            {
                if (year.HasValue && i.ProductionYear.HasValue)
                {
                    // Allow one year tolerance
                    return Math.Abs(year.Value - i.ProductionYear.Value) <= 1;
                }

                return true;
            });
        }

        private async Task<List<RemoteSearchResult>> FindSeriesInternal(string name, string language, CancellationToken cancellationToken)
        {
            var parsedName = _libraryManager.ParseName(name);
            var comparableName = GetComparableName(parsedName.Name);

            var list = new List<Tuple<List<string>, RemoteSearchResult>>();
            TvDbApiResponse<SearchResultDto[]> result;
            try
            {
                result = await _tvdbClientManager.GetSeriesByNameAsync(comparableName, language, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TvDbServerException e)
            {
                _logger.LogError(e, "No series results found for {Name}", comparableName);
                return new List<RemoteSearchResult>();
            }

            foreach (var seriesSearchResult in result.Data)
            {
                var tvdbTitles = new List<string>
                {
                    seriesSearchResult.Name
                };
                tvdbTitles.AddRange(seriesSearchResult.Aliases);

                DateTime? firstAired = null;
                if (DateTime.TryParse(seriesSearchResult.FirstAirTime, out var parsedFirstAired))
                {
                    firstAired = parsedFirstAired;
                }

                var remoteSearchResult = new RemoteSearchResult
                {
                    Name = tvdbTitles.FirstOrDefault(),
                    ProductionYear = firstAired?.Year,
                    SearchProviderName = Name
                };

                if (!string.IsNullOrEmpty(seriesSearchResult.Poster))
                {
                    // Results from their Search endpoints already include the /banners/ part in the url, because reasons...
                    remoteSearchResult.ImageUrl = TvdbUtils.TvdbBaseUrl + seriesSearchResult.Poster.TrimStart('/');
                }

                try
                {
                    if (int.TryParse(seriesSearchResult.Id, out int seriesId))
                    {
                        var seriesResult =
                            await _tvdbClientManager.GetSeriesByIdAsync(seriesId, language, cancellationToken)
                                .ConfigureAwait(false);
                        var imdbResult = seriesResult.Data.RemoteIds.FirstOrDefault(x => x.SourceName.Equals("IMDB", StringComparison.Ordinal));
                        if (imdbResult != null)
                        {
                            remoteSearchResult.SetProviderId(MetadataProvider.Imdb, imdbResult.Id);
                        }

                        var zap2ToItResult = seriesResult.Data.RemoteIds.FirstOrDefault(x => x.SourceName.Equals("TMS (Zap2It)", StringComparison.Ordinal));
                        if (zap2ToItResult != null)
                        {
                            remoteSearchResult.SetProviderId(MetadataProvider.Zap2It, zap2ToItResult.Id);
                        }
                    }
                }
                catch (TvDbServerException e)
                {
                    _logger.LogError(e, "Unable to retrieve series with id {TvdbId}:{SeriesName}", seriesSearchResult.Id, seriesSearchResult.Name);
                }

                remoteSearchResult.SetProviderId(TvdbPlugin.ProviderId, seriesSearchResult.Id.ToString(CultureInfo.InvariantCulture));
                list.Add(new Tuple<List<string>, RemoteSearchResult>(tvdbTitles, remoteSearchResult));
            }

            return list
                .OrderBy(i => i.Item1.Contains(name, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(i => i.Item1.Any(title => title.Contains(parsedName.Name, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
                .ThenBy(i => i.Item2.ProductionYear.HasValue && i.Item2.ProductionYear.Equals(parsedName.Year) ? 0 : 1)
                .ThenBy(i => i.Item1.Any(title => title.Contains(comparableName, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
                .ThenBy(i => list.IndexOf(i))
                .Select(i => i.Item2)
                .Take(MaxSearchResults) // TVDB returns a lot of unrelated results
                .ToList();
        }

        /// <summary>
        /// Gets the name of the comparable.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>System.String.</returns>
        private static string GetComparableName(string name)
        {
            name = name.ToLowerInvariant();
            name = name.Normalize(NormalizationForm.FormC);
            name = name.Replace(", the", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("the ", " ", StringComparison.OrdinalIgnoreCase)
                .Replace(" the ", " ", StringComparison.OrdinalIgnoreCase);
            name = name.Replace("&", " and ", StringComparison.OrdinalIgnoreCase);
            name = Regex.Replace(name, @"[\p{Lm}\p{Mn}]", string.Empty); // Remove diacritics, etc
            name = Regex.Replace(name, @"[\W\p{Pc}]+", " "); // Replace sequences of non-word characters and _ with " "
            return name.Trim();
        }

        private static void MapActorsToResult(MetadataResult<Series> result, IEnumerable<CharacterDto> actors)
        {
            foreach (var actor in actors)
            {
                var personInfo = new PersonInfo
                {
                    Type = PersonType.Actor,
                    Name = (actor.Name ?? string.Empty).Trim(),
                    Role = actor.Name,
                    SortOrder = (int)actor.Sort
                };

                if (!string.IsNullOrEmpty(actor.Image))
                {
                    personInfo.ImageUrl = TvdbUtils.BannerUrl + actor.Image;
                }

                if (!string.IsNullOrWhiteSpace(personInfo.Name))
                {
                    result.AddPerson(personInfo);
                }
            }
        }

        private async Task Identify(SeriesInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.GetProviderId(TvdbPlugin.ProviderId)))
            {
                return;
            }

            var remoteSearchResults = await FindSeries(info.Name, info.Year, info.MetadataLanguage, CancellationToken.None)
                .ConfigureAwait(false);

            var entry = remoteSearchResults.FirstOrDefault();

            if (entry != null)
            {
                var id = entry.GetProviderId(TvdbPlugin.ProviderId);
                if (!string.IsNullOrEmpty(id))
                {
                    info.SetProviderId(TvdbPlugin.ProviderId, id);
                }
            }
        }

        private async Task MapSeriesToResult(MetadataResult<Series> result, SeriesExtendedRecordDto tvdbSeries, string metadataLanguage)
        {
            var imdbResult = tvdbSeries.RemoteIds.FirstOrDefault(x => x.SourceName.Equals("IMDB", StringComparison.Ordinal));
            var zap2ToItResult = tvdbSeries.RemoteIds.FirstOrDefault(x => x.SourceName.Equals("TMS (Zap2It)", StringComparison.Ordinal));

            Series series = result.Item;
            series.SetProviderId(TvdbPlugin.ProviderId, tvdbSeries.Id.ToString(CultureInfo.InvariantCulture));
            series.Name = tvdbSeries.Name;
            series.Overview = (tvdbSeries.Overview ?? string.Empty).Trim();
            result.ResultLanguage = metadataLanguage;
            var airDays = new List<DayOfWeek>();
            if (tvdbSeries.AirsDays.Monday)
            {
                airDays.Add(DayOfWeek.Monday);
            }

            if (tvdbSeries.AirsDays.Tuesday)
            {
                airDays.Add(DayOfWeek.Tuesday);
            }

            if (tvdbSeries.AirsDays.Wednesday)
            {
                airDays.Add(DayOfWeek.Wednesday);
            }

            if (tvdbSeries.AirsDays.Thursday)
            {
                airDays.Add(DayOfWeek.Thursday);
            }

            if (tvdbSeries.AirsDays.Friday)
            {
                airDays.Add(DayOfWeek.Friday);
            }

            if (tvdbSeries.AirsDays.Saturday)
            {
                airDays.Add(DayOfWeek.Saturday);
            }

            if (tvdbSeries.AirsDays.Sunday)
            {
                airDays.Add(DayOfWeek.Sunday);
            }

            series.AirDays = airDays.ToArray();
            series.AirTime = tvdbSeries.AirsTime;
            if (imdbResult != null)
            {
                series.SetProviderId(MetadataProvider.Imdb, imdbResult.Id);
            }

            if (zap2ToItResult != null)
            {
                series.SetProviderId(MetadataProvider.Zap2It, zap2ToItResult.Id);
            }

            if (Enum.TryParse(tvdbSeries.Status.Name, true, out SeriesStatus seriesStatus))
            {
                series.Status = seriesStatus;
            }

            if (DateTime.TryParse(tvdbSeries.FirstAired, out var date))
            {
                // dates from tvdb are UTC but without offset or Z
                series.PremiereDate = date;
                series.ProductionYear = date.Year;
            }

            series.RunTimeTicks = TimeSpan.FromMinutes(tvdbSeries.AverageRuntime).Ticks;

            foreach (var genre in tvdbSeries.Genres)
            {
                series.AddGenre(genre.Name);
            }

            if (!string.IsNullOrEmpty(tvdbSeries.LatestNetwork.Name))
            {
                series.AddStudio(tvdbSeries.LatestNetwork.Name);
            }

            if (result.Item.Status.HasValue && result.Item.Status.Value == SeriesStatus.Ended)
            {
                try
                {
                    var episodeSummary = _tvdbClientManager.GetSeriesEpisodeSummaryAsync((int)tvdbSeries.Id, metadataLanguage, CancellationToken.None);
                    var maxEpisodeSeasonNumber = tvdbSeries.Episodes.Where(x => x.Aired != null).Max(x => x.SeasonNumber);
                    if (maxEpisodeSeasonNumber != 0)
                    {
                        var maxSeasonNumber = maxEpisodeSeasonNumber;
                        var episodeQuery = new SeriesEpisodesOptionalParams
                        {
                            Season = maxSeasonNumber
                        };
                        var episodesPage = await _tvdbClientManager.GetEpisodesPageAsync((int)tvdbSeries.Id, episodeQuery, metadataLanguage, CancellationToken.None).ConfigureAwait(false);

                        result.Item.EndDate = episodesPage.Data.Episodes
                            .Select(e => DateTime.TryParse(e.Aired, out var firstAired) ? firstAired : (DateTime?)null)
                            .Max();
                    }
                }
                catch (TvDbServerException e)
                {
                    _logger.LogError(e, "Failed to find series end date for series {TvdbId}:{SeriesName}", tvdbSeries.Id, tvdbSeries?.Name ?? result.Item?.Name);
                }
            }
        }
    }
}
