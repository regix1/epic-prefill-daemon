#nullable enable

using EpicPrefill.Handlers;
using EpicPrefill.Models;
using EpicPrefill.Settings;

namespace EpicPrefill.Api;

/// <summary>
/// High-level programmatic API for Epic Prefill operations.
/// </summary>
public sealed class EpicPrefillApi : IDisposable
{
    private readonly IEpicAuthProvider _authProvider;
    private readonly IPrefillProgress _progress;

    private EpicGamesManager? _epicManager;

    private List<string>? _selectedAppsCache;
    private bool _isInitialized;
    private bool _isDisposed;

    public EpicPrefillApi(
        IEpicAuthProvider authProvider,
        IPrefillProgress? progress = null)
    {
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _progress = progress ?? NullProgress.Instance;
    }

    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Initializes the API and logs into Epic Games.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isInitialized)
            return;

        _progress.OnOperationStarted("Initializing Epic Games connection");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var consoleAdapter = new ApiConsoleAdapter(_authProvider, _progress);

            var downloadArgs = new DownloadArguments
            {
                Force = false,
                TransferSpeedUnit = LancachePrefill.Common.Enums.TransferSpeedUnit.Bits
            };

            _epicManager = new EpicGamesManager(consoleAdapter, downloadArgs, _authProvider, _progress);

            await _epicManager.InitializeAsync();
            _isInitialized = true;

            _progress.OnOperationCompleted("Initializing Epic Games connection", timer.Elapsed);
            _progress.OnLog(LogLevel.Info, "Successfully logged into Epic Games");
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to initialize Epic Games connection", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets all games owned by the logged-in user
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        _progress.OnOperationStarted("Fetching owned games");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var apps = await _epicManager!.GetAvailableGamesAsync();
            var result = apps.Select(a => new OwnedGame
            {
                AppId = a.AppId,
                Name = a.Title
            }).ToList();

            _progress.OnOperationCompleted("Fetching owned games", timer.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to fetch owned games", ex);
            throw;
        }
    }

    public List<string> GetSelectedApps()
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (_selectedAppsCache != null && _selectedAppsCache.Count > 0)
        {
            _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Returning {_selectedAppsCache.Count} cached apps");
            return _selectedAppsCache;
        }

        var fileApps = _epicManager!.LoadPreviouslySelectedApps();
        _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Loaded {fileApps.Count} apps from file");
        return fileApps;
    }

    public void SetSelectedApps(IEnumerable<string> appIds)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var appIdList = appIds.ToList();

        _selectedAppsCache = appIdList;

        var tuiApps = appIdList.Select(id => new LancachePrefill.Common.SelectAppsTui.TuiAppInfo(id, "")
        {
            IsSelected = true
        }).ToList();

        _epicManager!.SetAppsAsSelected(tuiApps);
        _progress.OnLog(LogLevel.Info, $"Set {tuiApps.Count} apps for prefill (cached in memory)");
    }

    /// <summary>
    /// Runs the prefill operation
    /// </summary>
    public async Task<PrefillResult> PrefillAsync(
        PrefillOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        options ??= new PrefillOptions();

        _progress.OnOperationStarted("Prefill operation");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await _epicManager!.DownloadMultipleAppsAsync(
                downloadAllOwnedGames: options.DownloadAllOwnedGames);

            _progress.OnOperationCompleted("Prefill operation", timer.Elapsed);

            return new PrefillResult
            {
                Success = true,
                TotalTime = timer.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            _progress.OnLog(LogLevel.Info, "Prefill operation cancelled");
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = "Prefill cancelled",
                TotalTime = timer.Elapsed
            };
        }
        catch (Exception ex)
        {
            _progress.OnError("Prefill operation failed", ex);
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                TotalTime = timer.Elapsed
            };
        }
    }

    public static ClearCacheResult ClearCache()
    {
        var tempDir = new DirectoryInfo(AppConfig.TempDir);

        if (!tempDir.Exists)
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is already empty" };
        }

        var tempFiles = tempDir.EnumerateFiles("*.*", SearchOption.AllDirectories).ToList();
        var totalBytes = tempFiles.Sum(e => e.Length);
        var fileCount = tempFiles.Count;

        if (fileCount == 0)
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is already empty" };
        }

        try
        {
            Directory.Delete(tempDir.FullName, true);
            Directory.CreateDirectory(AppConfig.TempDir);
            var clearedSize = ByteSize.FromBytes(totalBytes);
            return new ClearCacheResult
            {
                Success = true,
                FileCount = fileCount,
                BytesCleared = totalBytes,
                Message = $"Cleared {fileCount} files ({clearedSize.ToDecimalString()})"
            };
        }
        catch (Exception ex)
        {
            return new ClearCacheResult { Success = false, FileCount = 0, BytesCleared = 0, Message = $"Failed to clear cache: {ex.Message}" };
        }
    }

    public static ClearCacheResult GetCacheInfo()
    {
        var tempDir = new DirectoryInfo(AppConfig.TempDir);

        if (!tempDir.Exists)
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is empty" };
        }

        var tempFiles = tempDir.EnumerateFiles("*.*", SearchOption.AllDirectories).ToList();
        var totalBytes = tempFiles.Sum(e => e.Length);
        var cacheSize = ByteSize.FromBytes(totalBytes);

        return new ClearCacheResult
        {
            Success = true,
            FileCount = tempFiles.Count,
            BytesCleared = totalBytes,
            Message = $"Cache contains {tempFiles.Count} files ({cacheSize.ToDecimalString()})"
        };
    }

    public void Shutdown()
    {
        _isInitialized = false;
        _progress.OnLog(LogLevel.Info, "Disconnected from Epic Games");
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        Shutdown();
        _isDisposed = true;
    }

    private void ThrowIfNotInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("EpicPrefillApi not initialized. Call InitializeAsync first.");
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(EpicPrefillApi));
    }
}

public class PrefillOptions
{
    public bool DownloadAllOwnedGames { get; set; }
    public bool Force { get; set; }
}

public class PrefillResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int AppsUpdated { get; init; }
    public int AppsAlreadyUpToDate { get; init; }
    public int AppsFailed { get; init; }
    public long TotalBytesTransferred { get; init; }
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
}
