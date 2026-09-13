namespace EpicPrefill.Models.ApiResponses
{
    public sealed class AppMetadataResponse
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        // Raw artwork entries as Epic's catalog returns them. Left unpicked here on purpose: the
        // consumer of the socket reply chooses the banner (landscape only), so a portrait or
        // oversized entry never becomes the one that ships. Null whenever Epic returned no artwork
        // at all for the app, so every read has to allow for that.
        [JsonPropertyName("keyImages")]
#pragma warning disable CA2227 // System.Text.Json replaces this collection when deserializing Epic responses.
        public List<KeyImage> KeyImages { get; set; }
#pragma warning restore CA2227

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

        // Not always present on Epic's artwork entries.
        [JsonPropertyName("md5")]
        public string Md5 { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }
    }
}
