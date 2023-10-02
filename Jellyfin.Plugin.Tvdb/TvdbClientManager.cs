using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Caching.Memory;
using TvDbSharper;

namespace Jellyfin.Plugin.Tvdb
{
    /// <summary>
    /// Tvdb client manager.
    /// </summary>
    public class TvdbClientManager
    {
        private const string DefaultLanguage = "en";

        private readonly IMemoryCache _cache;
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// TvDbClients per language.
        /// </summary>
        private readonly ConcurrentDictionary<string, TvDbClientInfo> _tvDbClients = new ConcurrentDictionary<string, TvDbClientInfo>();

        /// <summary>
        /// Initializes a new instance of the <see cref="TvdbClientManager"/> class.
        /// </summary>
        /// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        public TvdbClientManager(IMemoryCache memoryCache, IHttpClientFactory httpClientFactory)
        {
            _cache = memoryCache;
            _httpClientFactory = httpClientFactory;
        }

        private static string? ApiKey => TvdbPlugin.Instance?.Configuration.ApiKey;

        private async Task<TvDbClient> GetTvDbClient(string language)
        {
            var normalizedLanguage = TvdbUtils.NormalizeLanguage(language) ?? DefaultLanguage;

            var tvDbClientInfo = _tvDbClients.GetOrAdd(normalizedLanguage, key => new TvDbClientInfo(_httpClientFactory, key));

            var tvDbClient = tvDbClientInfo.Client;

            // First time authenticating if the token was never updated or if it's empty in the client
            if (tvDbClientInfo.TokenUpdatedAt == DateTime.MinValue || string.IsNullOrEmpty(tvDbClient.AuthToken))
            {
                await tvDbClientInfo.TokenUpdateLock.WaitAsync().ConfigureAwait(false);

                try
                {
                    if (string.IsNullOrEmpty(tvDbClient.AuthToken))
                    {
                        await tvDbClient.Login(ApiKey, "string").ConfigureAwait(false);
                        tvDbClientInfo.TokenUpdatedAt = DateTime.UtcNow;
                    }
                }
                finally
                {
                    tvDbClientInfo.TokenUpdateLock.Release();
                }
            }

            // Refresh if necessary
            if (tvDbClientInfo.TokenUpdatedAt < DateTime.UtcNow.Subtract(TimeSpan.FromHours(20)))
            {
                await tvDbClientInfo.TokenUpdateLock.WaitAsync().ConfigureAwait(false);

                try
                {
                    if (tvDbClientInfo.TokenUpdatedAt < DateTime.UtcNow.Subtract(TimeSpan.FromHours(20)))
                    {
                        await tvDbClient.Login(ApiKey, "string").ConfigureAwait(false);

                        tvDbClientInfo.TokenUpdatedAt = DateTime.UtcNow;
                    }
                }
                finally
                {
                    tvDbClientInfo.TokenUpdateLock.Release();
                }
            }

            return tvDbClient;
        }

        /// <summary>
        /// Get series by name.
        /// </summary>
        /// <param name="name">Series name.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The series search result.</returns>
        public Task<TvDbApiResponse<SearchResultDto[]>> GetSeriesByNameAsync(
            string name,
            string language,
            CancellationToken cancellationToken)
        {
            var cacheKey = GenerateKey("series", name, language);
            var searchParams = new SearchOptionalParams() { Language = language, Type = "series", Query = name };
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.Search(searchParams, cancellationToken));
        }

        /// <summary>
        /// Get series by id.
        /// </summary>
        /// <param name="tvdbId">The series tvdb id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The series response.</returns>
        public Task<TvDbApiResponse<SeriesExtendedRecordDto>> GetSeriesByIdAsync(
            int tvdbId,
            string language,
            CancellationToken cancellationToken)
        {
            var cacheKey = GenerateKey("series", tvdbId, language);
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.SeriesExtended(tvdbId, cancellationToken));
        }

        /// <summary>
        /// Get episode record.
        /// </summary>
        /// <param name="episodeTvdbId">The episode tvdb id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The episode record.</returns>
        public Task<TvDbApiResponse<EpisodeExtendedRecordDto>> GetEpisodesAsync(
            int episodeTvdbId,
            string language,
            CancellationToken cancellationToken)
        {
            var cacheKey = GenerateKey("episode", episodeTvdbId, language);
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.EpisodeExtended(episodeTvdbId, cancellationToken));
        }

        /// <summary>
        /// Get series by imdb.
        /// </summary>
        /// <param name="imdbId">The imdb id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The series search result.</returns>
        public Task<TvDbApiResponse<SearchResultDto[]>> GetSeriesByImdbIdAsync(
            string imdbId,
            string language,
            CancellationToken cancellationToken)
        {
            var cacheKey = GenerateKey("series", imdbId, language);
            var searchParams = new SearchOptionalParams() { Language = language, Remote_id = imdbId, Type = "series" };
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.Search(searchParams, cancellationToken));
        }

        /// <summary>
        /// Get series by zap2it id.
        /// </summary>
        /// <param name="zap2ItId">Zap2it id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The series search result.</returns>
        public Task<TvDbApiResponse<SearchResultDto[]>> GetSeriesByZap2ItIdAsync(
            string zap2ItId,
            string language,
            CancellationToken cancellationToken)
        {
            var cacheKey = GenerateKey("series", zap2ItId, language);
            var searchParams = new SearchOptionalParams() { Language = language, Remote_id = zap2ItId, Type = "series" };
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.Search(searchParams, cancellationToken));
        }

        /// <summary>
        /// Get actors by tvdb id.
        /// </summary>
        /// <param name="tvdbId">Tvdb id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The actors attached to the id.</returns>
        public async Task<IEnumerable<CharacterDto>> GetActorsAsync(
            int tvdbId,
            string language,
            CancellationToken cancellationToken)
        {
            var seriesResult = await GetSeriesByIdAsync(tvdbId, language, cancellationToken).ConfigureAwait(false);
            var actors = seriesResult.Data.Characters.Where(x => x.PeopleType.Equals("Actor", StringComparison.Ordinal));
            return actors;
        }

        /// <summary>
        /// Get images by tvdb id.
        /// </summary>
        /// <param name="tvdbId">Tvdb id.</param>
        /// <param name="seriesArtworksOptionalParams">Input artwork query params.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The images attached to the id.</returns>
        public IEnumerable<ArtworkExtendedRecordDto> GetImagesAsync(
            int tvdbId,
            SeriesArtworksOptionalParams seriesArtworksOptionalParams,
            string language,
            CancellationToken cancellationToken)
        {
            var cacheKey = GenerateKey("series-images", tvdbId, language);
            var seriesResult = TryGetValue(cacheKey, language, tvDbClient => tvDbClient.SeriesArtworks(tvdbId, seriesArtworksOptionalParams, cancellationToken));
            var artwork = seriesResult.Result.Data.Artworks;
            return artwork;
        }

        /// <summary>
        /// Get all tvdb languages.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>All tvdb languages.</returns>
        public Task<TvDbApiResponse<LanguageDto[]>> GetLanguagesAsync(CancellationToken cancellationToken)
        {
            var cacheKey = GenerateKey("languages");
            return TryGetValue("languages", string.Empty, tvDbClient => tvDbClient.Languages(cancellationToken));
        }

        /// <summary>
        /// Get series episode summary.
        /// </summary>
        /// <param name="tvdbId">Tvdb id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The episode summary.</returns>
        public IEnumerable<string> GetSeriesEpisodeSummaryAsync(
            int tvdbId,
            string language,
            CancellationToken cancellationToken)
        {
            var seriesResult = GetSeriesByIdAsync(tvdbId, language, cancellationToken).Result.Data.Episodes.Select(x => x.Overview);
            return seriesResult;
        }

        /// <summary>
        /// Gets a page of episodes.
        /// </summary>
        /// <param name="tvdbId">Tvdb series id.</param>
        /// <param name="seriesEpisodesOptionalParams">Episode query.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The page of episodes.</returns>
        public Task<TvDbApiResponse<GetSeriesEpisodesResponseData>> GetEpisodesPageAsync(
            int tvdbId,
            SeriesEpisodesOptionalParams seriesEpisodesOptionalParams,
            string language,
            CancellationToken cancellationToken)
        {
            if (seriesEpisodesOptionalParams != null)
            {
                seriesEpisodesOptionalParams.Page ??= 0;
            }
            else
            {
                seriesEpisodesOptionalParams = new SeriesEpisodesOptionalParams() { Page = 0 };
            }

            var cacheKey = GenerateKey(language, tvdbId, seriesEpisodesOptionalParams);
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.SeriesEpisodes(tvdbId, "default", seriesEpisodesOptionalParams, cancellationToken));
        }

        /// <summary>
        /// Gets a page of episodes.
        /// </summary>
        /// <param name="tvdbId">Tvdb series id.</param>
        /// <param name="seasonType">Episode seasonType.</param>
        /// <param name="seriesEpisodesOptionalParams">Episode query.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The page of episodes.</returns>
        public Task<TvDbApiResponse<GetSeriesEpisodesResponseData>> GetEpisodesPageAsync(
            int tvdbId,
            string seasonType,
            SeriesEpisodesOptionalParams seriesEpisodesOptionalParams,
            string language,
            CancellationToken cancellationToken)
        {
            if (seriesEpisodesOptionalParams != null)
            {
                seriesEpisodesOptionalParams.Page ??= 0;
            }
            else
            {
                seriesEpisodesOptionalParams = new SeriesEpisodesOptionalParams() { Page = 0 };
            }

            var cacheKey = GenerateKey(language, tvdbId, seriesEpisodesOptionalParams, seasonType);
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.SeriesEpisodes(tvdbId, seasonType, seriesEpisodesOptionalParams, cancellationToken));
        }

        /// <summary>
        /// Get an episode's tvdb id.
        /// </summary>
        /// <param name="searchInfo">Episode search info.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The tvdb id.</returns>
        public Task<string?> GetEpisodeTvdbId(
            EpisodeInfo searchInfo,
            string language,
            CancellationToken cancellationToken)
        {
            searchInfo.SeriesProviderIds.TryGetValue(TvdbPlugin.ProviderId, out var seriesTvdbId);
            var seriesEpisodesOptionalParams = new SeriesEpisodesOptionalParams();
            var seasonType = "default";

            // Prefer SxE over premiere date as it is more robust
            if (searchInfo.IndexNumber.HasValue && searchInfo.ParentIndexNumber.HasValue)
            {
                seriesEpisodesOptionalParams.EpisodeNumber = searchInfo.IndexNumber.Value;
                seriesEpisodesOptionalParams.Season = searchInfo.ParentIndexNumber.Value;
                switch (searchInfo.SeriesDisplayOrder)
                {
                    case "dvd":
                        seasonType = "dvd";
                        break;
                    case "absolute":
                        seasonType = "absolute";
                        break;
                    default:
                        break;
                }
            }
            else if (searchInfo.PremiereDate.HasValue)
            {
                // tvdb expects yyyy-mm-dd format
                seriesEpisodesOptionalParams.AirDate = searchInfo.PremiereDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return GetEpisodeTvdbId(Convert.ToInt32(seriesTvdbId, CultureInfo.InvariantCulture), seasonType, seriesEpisodesOptionalParams, language, cancellationToken);
        }

        /// <summary>
        /// Get an episode's tvdb id.
        /// </summary>
        /// <param name="seriesTvdbId">The series tvdb id.</param>
        /// <param name="seasonType">The series season type.</param>
        /// <param name="seriesEpisodesOptionalParams">Episode query.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The tvdb id.</returns>
        public async Task<string?> GetEpisodeTvdbId(
            int seriesTvdbId,
            string seasonType,
            SeriesEpisodesOptionalParams seriesEpisodesOptionalParams,
            string language,
            CancellationToken cancellationToken)
        {
            var episodePage =
                await GetEpisodesPageAsync(Convert.ToInt32(seriesTvdbId), seasonType, seriesEpisodesOptionalParams, language, cancellationToken)
                    .ConfigureAwait(false);
            return episodePage.Data.Series.Id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Gets a page of episodes.
        /// </summary>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The page of episodes.</returns>
        public Task<TvDbApiResponse<ArtworkTypeDto[]>> GetArtworkTypes(
            string language,
            CancellationToken cancellationToken)
        {
            var cacheKey = GenerateKey("artwork-types");
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.ArtworkTypes(cancellationToken));
        }

        /// <summary>
        /// Get image key types for series.
        /// </summary>
        /// <param name="tvdbId">Tvdb series id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The image key types.</returns>
        public async IAsyncEnumerable<ArtworkTypeDto> GetArtworkKeyTypesForSeriesAsync(int tvdbId, string language, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Images summary is language agnostic
            var artworkTypes = await GetArtworkTypes(language, cancellationToken).ConfigureAwait(false);
            if (artworkTypes == null)
            {
                throw new TvDbServerException("artworkTypes query returned null", 0);
            }

            var cacheKey = GenerateKey(nameof(TvDbClient.SeriesArtworks), tvdbId);
            var imagesSummary = await TryGetValue(cacheKey, language, tvDbClient => tvDbClient.SeriesArtworks(tvdbId, cancellationToken)).ConfigureAwait(false);
            foreach (var artworkTypeName in artworkTypes.Data.Where(x => x.RecordType != null && x.RecordType.Equals("series", StringComparison.Ordinal)).Select(x => x.Name))
            {
                if (imagesSummary.Data.Artworks.Any(x => x.Type == artworkTypes.Data?.FirstOrDefault(x => x.Name.Equals(artworkTypeName, StringComparison.Ordinal))?.Id))
                {
                    var artType = artworkTypes.Data.FirstOrDefault(x => x.Name.Equals(artworkTypeName, StringComparison.Ordinal));
                    if (artType != null)
                    {
                        yield return artType;
                    }
                }
            }
        }

        /// <summary>
        /// Get image key types for season.
        /// </summary>
        /// <param name="tvdbId">Tvdb series id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The image key types.</returns>
        public async IAsyncEnumerable<ArtworkTypeDto> GetArtworkKeyTypesForSeasonAsync(int tvdbId, string language, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Images summary is language agnostic
            var artworkTypes = await GetArtworkTypes(language, cancellationToken).ConfigureAwait(false);
            if (artworkTypes == null)
            {
                throw new TvDbServerException("artworkTypes query returned null", 0);
            }

            var cacheKey = GenerateKey(nameof(TvDbClient.SeriesArtworks), tvdbId);
            var imagesSummary = await TryGetValue(cacheKey, language, tvDbClient => tvDbClient.SeriesArtworks(tvdbId, cancellationToken)).ConfigureAwait(false);
            foreach (var artworkTypeName in artworkTypes.Data.Where(x => x.RecordType != null && x.RecordType.Equals("season", StringComparison.Ordinal)).Select(x => x.Name))
            {
                if (imagesSummary.Data.Artworks.Any(x => x.Type == artworkTypes.Data?.FirstOrDefault(x => x.Name.Equals(artworkTypeName, StringComparison.Ordinal))?.Id))
                {
                    var artType = artworkTypes.Data.FirstOrDefault(x => x.Name.Equals(artworkTypeName, StringComparison.Ordinal));
                    if (artType != null)
                    {
                        yield return artType;
                    }
                }
            }
        }

        /// <summary>
        /// Gets a page of episodes.
        /// </summary>
        /// <param name="tvdbEpisodeId">Tvdb episode id.</param>
        /// <param name="language">Metadata language.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The page of episodes.</returns>
        public Task<TvDbApiResponse<TranslationDto>> GetEpisodeNameTranslationForLanguage(
            int tvdbEpisodeId,
            string language,
            CancellationToken cancellationToken)
        {
            if (language == "en")
            {
                language = "eng";
            }

            var cacheKey = GenerateKey(language, tvdbEpisodeId);
            return TryGetValue(cacheKey, language, tvDbClient => tvDbClient.EpisodeTranslation(tvdbEpisodeId, language, cancellationToken));
        }

        private static string GenerateKey(params object[] objects)
        {
            var key = string.Empty;

            foreach (var obj in objects)
            {
                var objType = obj.GetType();
                if (objType.IsPrimitive || objType == typeof(string))
                {
                    key += obj + ";";
                }
                else
                {
                    foreach (PropertyInfo propertyInfo in objType.GetProperties())
                    {
                        var currentValue = propertyInfo.GetValue(obj, null);
                        if (currentValue == null)
                        {
                            continue;
                        }

                        key += propertyInfo.Name + "=" + currentValue + ";";
                    }
                }
            }

            return key;
        }

        private Task<T> TryGetValue<T>(string key, string language, Func<TvDbClient, Task<T>> resultFactory)
        {
            return _cache.GetOrCreateAsync(key, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);

                var tvDbClient = await GetTvDbClient(language).ConfigureAwait(false);

                var result = await resultFactory.Invoke(tvDbClient).ConfigureAwait(false);

                return result;
            });
        }

        private class TvDbClientInfo
        {
            public TvDbClientInfo(IHttpClientFactory httpClientFactory, string language)
            {
                Client = new TvDbClient(httpClientFactory.CreateClient(NamedClient.Default));

                TokenUpdateLock = new SemaphoreSlim(1, 1);
                TokenUpdatedAt = DateTime.MinValue;
            }

            public TvDbClient Client { get; }

            public SemaphoreSlim TokenUpdateLock { get; }

            public DateTime TokenUpdatedAt { get; set; }
        }
    }
}
