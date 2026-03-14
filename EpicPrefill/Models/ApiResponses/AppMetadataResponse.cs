namespace EpicPrefill.Models.ApiResponses
{
    public sealed class AppMetadataResponse
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        public override string ToString()
        {
            return Title;
        }
    }
}
