namespace EpicPrefill.Models.ApiResponses
{
    /// <summary>
    /// A single entry from Epic's library-service playtime endpoint
    /// (GET library-service.../library/api/public/playtime/account/{accountId}/all).
    /// Epic only exposes a cumulative <see cref="TotalTime"/> (seconds) per artifact; the response
    /// carries no per-title last-played timestamp. This can therefore back a "most played" ordering
    /// but cannot back a genuine "recently played" ordering.
    /// </summary>
    public sealed class PlaytimeEntry
    {
        [JsonPropertyName("accountId")]
        public string AccountId { get; set; }

        // Epic's artifactId is the owned-asset appName, so it matches Asset.AppId / AppInfo.AppId 1:1.
        [JsonPropertyName("artifactId")]
        public string ArtifactId { get; set; }

        [JsonPropertyName("totalTime")]
        public long TotalTime { get; set; }
    }
}
