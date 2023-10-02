using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using TvDbSharper;

namespace Jellyfin.Plugin.Tvdb
{
    /// <summary>
    /// Tvdb utils.
    /// </summary>
    public static class TvdbUtils
    {
        /// <summary>
        /// Base url for all requests.
        /// </summary>
        public const string TvdbBaseUrl = "https://www.thetvdb.com/";

        /// <summary>
        /// Base url for banners.
        /// </summary>
        public const string BannerUrl = TvdbBaseUrl + "banners/";

        /// <summary>
        /// Get image type from key type.
        /// </summary>
        /// <param name="keyType">Key type.</param>
        /// <param name="artworkTypes">Input list of artwork types.</param>
        /// <returns>Image type.</returns>
        /// <exception cref="ArgumentException">Unknown key type.</exception>
        public static ImageType GetArtworkTypeFromKeyType(long keyType, ArtworkTypeDto[] artworkTypes)
        {
            switch (artworkTypes?.FirstOrDefault(x => x.Id == keyType)?.Name.ToLowerInvariant())
            {
                case "Background":
                    return ImageType.Backdrop;
                case "Icon":
                    return ImageType.Thumb;
                case "Poster":
                    return ImageType.Primary;
                case "Banner":
                    return ImageType.Banner;
                case "ClearLogo":
                    return ImageType.Logo;
                default: throw new ArgumentException($"Invalid or unknown keytype: {keyType}", nameof(keyType));
            }
        }

        /// <summary>
        /// Normalize language to tvdb format.
        /// </summary>
        /// <param name="language">Language.</param>
        /// <returns>Normalized language.</returns>
        public static string? NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            // pt-br is just pt to tvdb
            return language.Split('-')[0].ToLowerInvariant();
        }
    }
}
