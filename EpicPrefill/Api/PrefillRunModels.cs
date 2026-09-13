using EpicPrefill.Handlers;
using EpicPrefill.Models;
using EpicPrefill.Settings;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace EpicPrefill.Api;

public sealed record PrefillStart(bool Success, string RunId, string DaemonInstanceId, string State);

public class PrefillOptions
{
    public bool DownloadAllOwnedGames { get; set; }
    public bool Force { get; set; }

    /// <summary>The "Recent" preset. Epic has no recently-played API, so this gracefully falls back to all owned games.</summary>
    public bool Recent { get; set; }

    /// <summary>The "Top" preset: owned games ordered by cumulative Epic playtime, most-played first.</summary>
    public bool Top { get; set; }
}

public class CredentialChallengeEvent : SocketEvent<CredentialChallenge>
{
    public CredentialChallengeEvent(CredentialChallenge challenge)
    {
        Type = "credential-challenge";
        Data = challenge;
    }
}

public class ProgressEvent : SocketEvent<PrefillProgressUpdate>
{
    public ProgressEvent(PrefillProgressUpdate progress)
    {
        Type = "progress";
        Data = progress;
    }
}

public class AuthStateEvent : SocketEvent<AuthStateData>
{
    public AuthStateEvent(string state, string? message = null, string? displayName = null)
    {
        Type = "auth-state";
        Data = new AuthStateData { State = state, Message = message, DisplayName = displayName };
    }
}

public class DownloadProgressInfo
{
    public string AppId { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public double PercentComplete => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
    public double BytesPerSecond { get; init; }
    public TimeSpan Elapsed { get; init; }
}


public class PrefillProgressUpdate
{
    public string? OperationId { get; set; }
    public string? DaemonInstanceId { get; set; }
    public long Sequence { get; set; }
    public string? Reason { get; set; }
    public int SkippedApps { get; set; }
    public int CancelledApps { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppId")]
    public string? CurrentAppId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppName")]
    public string? CurrentAppName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesPerSecond")]
    public double BytesPerSecond { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsed")]
    public TimeSpan Elapsed { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds => Elapsed.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public string? Result { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalApps")]
    public int TotalApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("updatedApps")]
    public int UpdatedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("alreadyUpToDate")]
    public int AlreadyUpToDate { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("failedApps")]
    public int FailedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytesTransferred")]
    public long TotalBytesTransferred { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTimeSeconds")]
    public double TotalTimeSeconds => TotalTime.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
public class PrefillResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan TotalTime { get; init; }
}

public class ClearCacheResult
{
    public bool Success { get; init; }
    public int FileCount { get; init; }
    public long BytesCleared { get; init; }
    public string? Message { get; init; }
}

public class AppStatus
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public long DownloadSize { get; init; }
    public bool IsUpToDate { get; init; }
}

public class SelectedAppsStatus
{
    public List<AppStatus> Apps { get; init; } = new();
    public long TotalDownloadSize { get; init; }
    public string? Message { get; init; }
}

public class OwnedGame
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public List<KeyImage>? KeyImages { get; init; }
}

public class CacheStatusResult
{
    public List<AppCacheStatus> Apps { get; init; } = new();
    public string? Message { get; init; }
}

public class AppCacheStatus
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsUpToDate { get; init; }
}

public class CdnInfo
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string CdnHost { get; init; } = string.Empty;
    public string ChunkBaseUrl { get; init; } = string.Empty;
}

public class CdnInfoResult
{
    public List<CdnInfo> Apps { get; init; } = new();
    public string? Message { get; init; }
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public class AppDownloadInfo
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public int ChunkCount { get; init; }
}

public enum AppDownloadResult
{
    Success,
    AlreadyUpToDate,
    Failed,
    Skipped,
    NoDepotsToDownload
}

public class PrefillSummary
{
    public int TotalApps { get; init; }
    public int UpdatedApps { get; init; }
    public int AlreadyUpToDate { get; init; }
    public int FailedApps { get; init; }
    public long TotalBytesTransferred { get; init; }
    public TimeSpan TotalTime { get; init; }
}

public class StatusData
{
    public int ProtocolVersion { get; init; }
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();
    public string? DaemonInstanceId { get; init; }
    public int MaxConcurrentRuns { get; init; }
    public int MaxConcurrentRequests { get; init; }
    public int RetentionHours { get; init; }
    public int RetentionOperations { get; init; }
    public int RetentionItems { get; init; }
    public IReadOnlyList<RunSnapshot> ActiveOperations { get; init; } = Array.Empty<RunSnapshot>();
    public IReadOnlyList<RunSnapshot> RecentOperations { get; init; } = Array.Empty<RunSnapshot>();
    public bool IsLoggedIn { get; init; }
    public bool IsInitialized { get; init; }
    public bool IsPrefilling { get; init; }

    /// <summary>
    /// UTC ISO-8601 expiry of the refresh token (refresh_expires_at). Null when not logged in / no token on disk.
    /// </summary>
    public string? AuthExpiryUtc { get; init; }

    /// <summary>
    /// UTC ISO-8601 expiry of the access token (expires_at). Null when not logged in / no token on disk.
    /// </summary>
    public string? AccessExpiryUtc { get; init; }

    /// <summary>
    /// Display name of the logged-in account, when available. Null when not logged in / no token on disk.
    /// </summary>
    public string? AccountDisplayName { get; init; }
}

public class CommandRequest
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Framed command deserialization supplies the parameter collection.")]
    public Dictionary<string, string>? Parameters { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CommandResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public object? Data { get; set; }
    public bool RequiresLogin { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public enum SocketServerMode
{
    UnixSocket,
    Tcp
}

public class SocketEvent<T>
{
    public string Type { get; init; } = string.Empty;
    public T? Data { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class AuthStateData
{
    public string State { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string? DisplayName { get; init; }
}
