#nullable enable

namespace EpicPrefill.Api;

/// <summary>
/// Interface for receiving progress updates during prefill operations.
/// </summary>
public interface IPrefillProgress
{
    void OnLog(LogLevel level, string message);
    void OnOperationStarted(string operationName);
    void OnOperationCompleted(string operationName, TimeSpan elapsed);
    void OnAppStarted(AppDownloadInfo app);
    void OnDownloadProgress(DownloadProgressInfo progress);
    void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result);
    void OnPrefillCompleted(PrefillSummary summary);
    void OnError(string message, Exception? exception = null);
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

public class NullProgress : IPrefillProgress
{
    public static readonly NullProgress Instance = new();

    public void OnLog(LogLevel level, string message) { }
    public void OnOperationStarted(string operationName) { }
    public void OnOperationCompleted(string operationName, TimeSpan elapsed) { }
    public void OnAppStarted(AppDownloadInfo app) { }
    public void OnDownloadProgress(DownloadProgressInfo progress) { }
    public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result) { }
    public void OnPrefillCompleted(PrefillSummary summary) { }
    public void OnError(string message, Exception? exception = null) { }
}

public class CallbackProgress : IPrefillProgress
{
    public event Action<LogLevel, string>? LogReceived;
    public event Action<string>? OperationStarted;
    public event Action<string, TimeSpan>? OperationCompleted;
    public event Action<AppDownloadInfo>? AppStarted;
    public event Action<DownloadProgressInfo>? DownloadProgressUpdated;
    public event Action<AppDownloadInfo, AppDownloadResult>? AppCompleted;
    public event Action<PrefillSummary>? PrefillCompleted;
    public event Action<string, Exception?>? ErrorOccurred;

    public void OnLog(LogLevel level, string message) => LogReceived?.Invoke(level, message);
    public void OnOperationStarted(string operationName) => OperationStarted?.Invoke(operationName);
    public void OnOperationCompleted(string operationName, TimeSpan elapsed) => OperationCompleted?.Invoke(operationName, elapsed);
    public void OnAppStarted(AppDownloadInfo app) => AppStarted?.Invoke(app);
    public void OnDownloadProgress(DownloadProgressInfo progress) => DownloadProgressUpdated?.Invoke(progress);
    public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result) => AppCompleted?.Invoke(app, result);
    public void OnPrefillCompleted(PrefillSummary summary) => PrefillCompleted?.Invoke(summary);
    public void OnError(string message, Exception? exception = null) => ErrorOccurred?.Invoke(message, exception);
}
