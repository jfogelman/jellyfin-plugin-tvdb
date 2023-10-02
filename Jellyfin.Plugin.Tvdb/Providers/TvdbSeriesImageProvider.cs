using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using TvDbSharper;
using RatingType = MediaBrowser.Model.Dto.RatingType;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.Tvdb.Providers
{
    /// <summary>
    /// Tvdb series image provider.
    /// </summary>
    public class TvdbSeriesImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TvdbSeriesImageProvider> _logger;
        private readonly TvdbClientManager _tvdbClientManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvdbSeriesImageProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{TvdbSeriesImageProvider}"/> interface.</param>
        /// <param name="tvdbClientManager">Instance of <see cref="TvdbClientManager"/>.</param>
        public TvdbSeriesImageProvider(IHttpClientFactory httpClientFactory, ILogger<TvdbSeriesImageProvider> logger, TvdbClientManager tvdbClientManager)
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
            return item is Series;
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
            if (!TvdbSeriesProvider.IsValidSeries(item.ProviderIds))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var language = item.GetPreferredMetadataLanguage();
            var remoteImages = new List<RemoteImageInfo>();
            var tvdbId = Convert.ToInt32(item.GetProviderId(TvdbPlugin.ProviderId), CultureInfo.InvariantCulture);
            var allowedKeyTypes = _tvdbClientManager.GetArtworkKeyTypesForSeriesAsync(tvdbId, language, cancellationToken)
                .ConfigureAwait(false);
            await foreach (var keyType in allowedKeyTypes)
            {
                var imageQuery = new SeriesArtworksOptionalParams
                {
                    Type = (int)keyType.Id,
                    Lang = language
                };
                try
                {
                    var imageResults = _tvdbClientManager.GetImagesAsync(tvdbId, imageQuery, language, cancellationToken);
                    var imagesToAdd = await GetImages(item, cancellationToken).ConfigureAwait(false);

                    remoteImages.AddRange(imagesToAdd);
                }
                catch (TvDbServerException)
                {
                    _logger.LogDebug(
                        "No images of type {KeyType} exist for series {TvDbId}:{Name}",
                        keyType,
                        tvdbId,
                        item.Name);
                }
            }

            return remoteImages;
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetImages(ArtworkExtendedRecordDto[] images, string preferredLanguage, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();
            var languages = _tvdbClientManager.GetLanguagesAsync(CancellationToken.None).Result.Data;
            var artworkTypes = await _tvdbClientManager.GetArtworkTypes(preferredLanguage, cancellationToken).ConfigureAwait(false);

            foreach (var image in images)
            {
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

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);
        }
    }
}
