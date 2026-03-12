namespace EpicPrefill.Models.ApiResponses
{
    public sealed class AppMetadataResponse
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("keyImages")]
        public List<KeyImage>? KeyImages { get; set; }

        /// <summary>
        /// Gets the best available image URL from keyImages.
        /// Prefers DieselStoreFrontWide > OfferImageWide > DieselStoreFrontTall > OfferImageTall > Thumbnail.
        /// </summary>
        public string? GetBestImageUrl()
        {
            if (KeyImages == null || KeyImages.Count == 0)
                return null;

            // Priority order for game art (wide images work best as game cards)
            string[] preferredTypes = { "DieselStoreFrontWide", "OfferImageWide", "DieselStoreFrontTall", "OfferImageTall", "Thumbnail", "CodeRedemption_340x440" };

            foreach (var type in preferredTypes)
            {
                var match = KeyImages.FirstOrDefault(k => k.Type == type && !string.IsNullOrEmpty(k.Url));
                if (match != null)
                    return match.Url;
            }

            // Fallback: return the first image with a URL
            return KeyImages.FirstOrDefault(k => !string.IsNullOrEmpty(k.Url))?.Url;
        }

        public override string ToString()
        {
            return Title;
        }
    }

    public sealed class KeyImage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }
    }
}
