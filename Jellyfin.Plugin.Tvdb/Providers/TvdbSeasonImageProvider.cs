using System;
using System.Collections.Generic;
using System.Globalization;
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
using TvDbSharper;
using RatingType = MediaBrowser.Model.Dto.RatingType;

namespace Jellyfin.Plugin.Tvdb.Providers
{
    /// <summary>
    /// Tvdb season image provider.
    /// </summary>
    public class TvdbSeasonImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TvdbSeasonImageProvider> _logger;
        private readonly TvdbClientManager _tvdbClientManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvdbSeasonImageProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{TvdbSeasonImageProvider}"/> interface.</param>
        /// <param name="tvdbClientManager">Instance of <see cref="TvdbClientManager"/>.</param>
        public TvdbSeasonImageProvider(IHttpClientFactory httpClientFactory, ILogger<TvdbSeasonImageProvider> logger, TvdbClientManager tvdbClientManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _tvdbClientManager = tvdbClientManager;
        }

        /// <inheritdoc />
        public string Name => TvdbPlugin.ProviderName;

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            return item is Season;
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
            yield return ImageType.Banner;
            yield return ImageType.Backdrop;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var season = (Season)item;
            var series = season.Series;

            if (series == null || !season.IndexNumber.HasValue || !TvdbSeriesProvider.IsValidSeries(series.ProviderIds))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var tvdbId = Convert.ToInt32(series.GetProviderId(TvdbPlugin.ProviderId), CultureInfo.InvariantCulture);
            var seasonNumber = season.IndexNumber.Value;
            var language = item.GetPreferredMetadataLanguage();
            var remoteImages = new List<RemoteImageInfo>();

            var keyTypes = _tvdbClientManager.GetArtworkKeyTypesForSeriesAsync(tvdbId, language, cancellationToken).ConfigureAwait(false);
            await foreach (var keyType in keyTypes)
            {
                var imageQuery = new SeriesArtworksOptionalParams
                {
                    Type = (int)keyType.Id,
                    Lang = language
                };
                try
                {
                    var imageResults = _tvdbClientManager
                        .GetImagesAsync(tvdbId, imageQuery, language, cancellationToken);
                    var imagesToAdd = await GetImages(imageResults.ToArray(), seasonNumber.ToString(CultureInfo.CurrentCulture), language, cancellationToken).ConfigureAwait(false);
                    remoteImages.AddRange(imagesToAdd);
                }
                catch (TvDbServerException)
                {
                    _logger.LogDebug("No images of type {KeyType} found for series {TvdbId}:{Name}", keyType, tvdbId, item.Name);
                }
            }

            return remoteImages;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetImages(ArtworkExtendedRecordDto[] images, string seasonNumber, string preferredLanguage, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();
            // any languages with null ids are ignored
            var languages = _tvdbClientManager.GetLanguagesAsync(CancellationToken.None).Result.Data.Where(x => !string.IsNullOrEmpty(x.Id)).ToArray();
            var artworkTypes = await _tvdbClientManager.GetArtworkTypes(preferredLanguage, cancellationToken).ConfigureAwait(false);

            foreach (var image in images)
            {
                // The API returns everything that contains the subkey eg. 2 matches 20, 21, 22, 23 etc.
                if (!string.Equals(image.SeasonId.ToString(), seasonNumber, StringComparison.Ordinal))
                {
                    continue;
                }

                var imageInfo = new RemoteImageInfo
                {
                    RatingType = RatingType.Score,
                    VoteCount = image.Score,
                    Url = image.Image,
                    ProviderName = Name,
                    Language = languages.FirstOrDefault(lang => lang.Id == image.Language)?.Id,
                    ThumbnailUrl = TvdbUtils.BannerUrl + image.Thumbnail
                };

                imageInfo.Width = Convert.ToInt32(image.Width, CultureInfo.InvariantCulture);
                imageInfo.Height = Convert.ToInt32(image.Height, CultureInfo.InvariantCulture);

                imageInfo.Type = TvdbUtils.GetArtworkTypeFromKeyType(image.Type, artworkTypes.Data);
                list.Add(imageInfo);
            }

            var isLanguageEn = string.Equals(preferredLanguage, "en", StringComparison.OrdinalIgnoreCase);
            return list.OrderByDescending(i =>
                {
                    if (string.Equals(preferredLanguage, i.Language, StringComparison.OrdinalIgnoreCase))
                    {
                        return 3;
                    }

                    if (!isLanguageEn)
                    {
                        if (string.Equals("en", i.Language, StringComparison.OrdinalIgnoreCase))
                        {
                            return 2;
                        }
                    }

                    if (string.IsNullOrEmpty(i.Language))
                    {
                        return isLanguageEn ? 3 : 2;
                    }

                    return 0;
                })
                .ThenByDescending(i => i.CommunityRating ?? 0)
                .ThenByDescending(i => i.VoteCount ?? 0);
        }
    }
}
