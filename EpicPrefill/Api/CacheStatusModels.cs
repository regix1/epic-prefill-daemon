using System.Text.Json.Serialization;

#nullable enable

namespace EpicPrefill.Api;

[JsonConverter(typeof(JsonStringEnumConverter<CacheOutcome>))]
public enum CacheOutcome
{
    Current,
    Outdated,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<CacheReason>))]
public enum CacheReason
{
    MissingApp,
    ManifestUnavailable,
    NoCacheEvidence,
    InspectionFailed,
    DeadlineReached
}

public class CacheStatusResult
{
    public List<AppCacheStatus> Apps { get; init; } = new();
    public string? Message { get; init; }
    public int? Version { get; init; }
}

public class AppCacheStatus
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsUpToDate { get; init; }
    public CacheOutcome? Outcome { get; init; }
    public CacheReason? Reason { get; init; }
}
