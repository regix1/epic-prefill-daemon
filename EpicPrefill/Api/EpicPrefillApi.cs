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

    /// <summary>
    /// Gets status of selected apps including names and download sizes.
    /// Downloads manifests to calculate actual sizes (may take a moment for many apps).
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var selectedAppIds = GetSelectedApps();
        if (selectedAppIds.Count == 0)
        {
            return new SelectedAppsStatus
            {
                Apps = new List<AppStatus>(),
                TotalDownloadSize = 0,
                Message = "No apps selected"
            };
        }

        try
        {
            var allGames = await _epicManager!.GetAvailableGamesAsync();
            var gamesByAppId = allGames.ToDictionary(g => g.AppId, g => g);

            var apps = new List<AppStatus>();
            long totalDownloadSize = 0;

            foreach (var appId in selectedAppIds)
            {
                if (!gamesByAppId.TryGetValue(appId, out var game))
                {
                    apps.Add(new AppStatus { AppId = appId, Name = appId, DownloadSize = 0, IsUpToDate = false });
                    continue;
                }

                var isUpToDate = _epicManager.IsAppUpToDate(game);
                long downloadSize = 0;

                if (!isUpToDate)
                {
                    try
                    {
                        downloadSize = await _epicManager.GetAppDownloadSizeAsync(game);
                    }
                    catch (Exception ex)
                    {
                        _progress.OnLog(LogLevel.Warning, $"Failed to get size for {game.Title}: {ex.Message}");
                    }
                }

                totalDownloadSize += downloadSize;
                apps.Add(new AppStatus
                {
                    AppId = appId,
                    Name = game.Title,
                    DownloadSize = downloadSize,
                    IsUpToDate = isUpToDate
                });
            }

            return new SelectedAppsStatus
            {
                Apps = apps,
                TotalDownloadSize = totalDownloadSize
            };
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to get selected apps status", ex);
            return new SelectedAppsStatus
            {
                Apps = new List<AppStatus>(),
                TotalDownloadSize = 0,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Checks cache status by comparing app build versions against previously downloaded versions.
    /// Returns which apps are up-to-date and which need updating.
    /// </summary>
    public async Task<CacheStatusResult> CheckCacheStatusAsync(List<string> appIds, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (appIds.Count == 0)
        {
            return new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = "No app IDs provided"
            };
        }

        try
        {
            var allGames = await _epicManager!.GetAvailableGamesAsync();
            var gamesByAppId = allGames.ToDictionary(g => g.AppId, g => g);

            var apps = new List<AppCacheStatus>();
            foreach (var appId in appIds.Distinct())
            {
                if (gamesByAppId.TryGetValue(appId, out var game))
                {
                    apps.Add(new AppCacheStatus
                    {
                        AppId = appId,
                        Name = game.Title,
                        IsUpToDate = _epicManager.IsAppUpToDate(game)
                    });
                }
            }

            return new CacheStatusResult
            {
                Apps = apps,
                Message = $"Checked {apps.Count} apps"
            };
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to check cache status", ex);
            return new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = $"Error: {ex.Message}"
            };
        }
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
                downloadAllOwnedGames: options.DownloadAllOwnedGames,
                force: options.Force,
                cancellationToken: cancellationToken);

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

    private static (int FileCount, long TotalBytes)? GetCacheStats()
    {
        var tempDir = new DirectoryInfo(AppConfig.TempDir);
        if (!tempDir.Exists)
            return null;

        var tempFiles = tempDir.EnumerateFiles("*.*", SearchOption.AllDirectories).ToList();
        return (tempFiles.Count, tempFiles.Sum(e => e.Length));
    }

    public static ClearCacheResult ClearCache()
    {
        var stats = GetCacheStats();
        if (stats is not { FileCount: > 0 })
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is already empty" };
        }

        var (fileCount, totalBytes) = stats.Value;

        try
        {
            Directory.Delete(AppConfig.TempDir, true);
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
        var stats = GetCacheStats();
        if (stats == null)
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is empty" };
        }

        var (fileCount, totalBytes) = stats.Value;
        var cacheSize = ByteSize.FromBytes(totalBytes);

        return new ClearCacheResult
        {
            Success = true,
            FileCount = fileCount,
            BytesCleared = totalBytes,
            Message = $"Cache contains {fileCount} files ({cacheSize.ToDecimalString()})"
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
