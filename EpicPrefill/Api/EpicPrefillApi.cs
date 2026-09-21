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
    private readonly TimeProvider _clock;

    private EpicGamesManager? _epicManager;

    private List<string>? _selectedAppsCache;
    private bool _isInitialized;
    private bool _isDisposed;

    public EpicPrefillApi(
        IEpicAuthProvider authProvider,
        IPrefillProgress? progress = null,
        TimeProvider? clock = null)
    {
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _progress = progress ?? NullProgress.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsInitialized => _isInitialized;

    public string? DisplayName => _epicManager?.DisplayName;

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

            await _epicManager.InitializeAsync(cancellationToken);
            _isInitialized = true;

            _progress.OnOperationCompleted("Initializing Epic Games connection", timer.Elapsed);
            _progress.OnLog(LogLevel.Info, "Successfully logged into Epic Games");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            var apps = await _epicManager!.GetAvailableGamesAsync(cancellationToken);
            var result = apps.Select(a => new OwnedGame
            {
                AppId = a.AppId,
                Name = a.Title,
                KeyImages = a.KeyImages
            }).ToList();

            _progress.OnOperationCompleted("Fetching owned games", timer.Elapsed);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to fetch owned games", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets CDN URL patterns for owned games.
    /// For each game, resolves the manifest URL to extract the CDN host and chunk base URL.
    /// These patterns can be used to identify which game a cached download belongs to.
    /// </summary>
    public async Task<CdnInfoResult> GetCdnInfoAsync(List<string>? appIds = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        _progress.OnOperationStarted("Fetching CDN info");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var allGames = await _epicManager!.GetAvailableGamesAsync(cancellationToken);

            // Filter to requested appIds if provided
            if (appIds != null && appIds.Count > 0)
            {
                var requestedSet = new HashSet<string>(appIds, StringComparer.OrdinalIgnoreCase);
                allGames = allGames.Where(g => requestedSet.Contains(g.AppId)).ToList();
            }

            var results = new List<CdnInfo>();
            foreach (var app in allGames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var manifestUrl = await _epicManager.GetManifestDownloadUrlAsync(app, cancellationToken);
                    results.Add(new CdnInfo
                    {
                        AppId = app.AppId,
                        Name = app.Title,
                        CdnHost = manifestUrl.ManifestDownloadUri.Host,
                        ChunkBaseUrl = manifestUrl.ChunkBaseUrl
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _progress.OnLog(LogLevel.Warning, $"Failed to get CDN info for {app.Title} ({app.AppId}): {ex.Message}");
                    // Continue with other games
                }
            }

            _progress.OnOperationCompleted("Fetching CDN info", timer.Elapsed);
            return new CdnInfoResult
            {
                Apps = results,
                Message = $"Retrieved CDN info for {results.Count} of {allGames.Count} games"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to fetch CDN info", ex);
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
            var allGames = await _epicManager!.GetAvailableGamesAsync(cancellationToken);
            var gamesByAppId = allGames.ToDictionary(g => g.AppId, g => g);

            var apps = new List<AppStatus>();
            long totalDownloadSize = 0;

            foreach (var appId in selectedAppIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!gamesByAppId.TryGetValue(appId, out var game))
                {
                    apps.Add(new AppStatus { AppId = appId, Name = appId, DownloadSize = 0, IsUpToDate = false });
                    continue;
                }

                var isUpToDate = _epicManager.IsAppUpToDate(game);
                long downloadSize = 0;

                if (isUpToDate != true)
                {
                    try
                    {
                        downloadSize = await _epicManager.GetAppDownloadSizeAsync(game, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
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
                    IsUpToDate = isUpToDate == true
                });
            }

            return new SelectedAppsStatus
            {
                Apps = apps,
                TotalDownloadSize = totalDownloadSize
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1068:CancellationToken parameters must come last",
        Justification = "The versioned fields follow the existing cancellation token to preserve source compatibility.")]
    public async Task<CacheStatusResult> CheckCacheStatusAsync(
        List<CachedAppInput> cachedApps,
        CancellationToken cancellationToken = default,
        DateTimeOffset? expiresAtUtc = null,
        int? cacheStatusVersion = null)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        try
        {
            return await CheckCacheStatusAsync(
                cachedApps,
                (includeDetails, token) => _epicManager!.GetAvailableGamesAsync(token, includeDetails),
                (game, _) => ValueTask.FromResult(_epicManager!.IsAppUpToDate(game)),
                _clock,
                cancellationToken,
                expiresAtUtc,
                cacheStatusVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EpicLoginException)
        {
            throw;
        }
        catch (ArgumentException) when (cacheStatusVersion == 2)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to check cache status", ex);
            if (cacheStatusVersion == 2)
            {
                return CreateUnknownResult(cachedApps, CacheReason.InspectionFailed, cacheStatusVersion);
            }
            return new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = $"Error: {ex.Message}"
            };
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1068:CancellationToken parameters must come last",
        Justification = "The production core mirrors the public compatibility-preserving parameter order.")]
    internal static async Task<CacheStatusResult> CheckCacheStatusAsync(
        IReadOnlyList<CachedAppInput> cachedApps,
        Func<bool, CancellationToken, Task<List<AppInfo>>> getAvailableGames,
        Func<AppInfo, CancellationToken, ValueTask<bool?>> inspectCache,
        TimeProvider clock,
        CancellationToken cancellationToken = default,
        DateTimeOffset? expiresAtUtc = null,
        int? cacheStatusVersion = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(cachedApps);
        ArgumentNullException.ThrowIfNull(getAvailableGames);
        ArgumentNullException.ThrowIfNull(inspectCache);
        ArgumentNullException.ThrowIfNull(clock);

        if (cacheStatusVersion is not null and not 2)
        {
            throw new ArgumentException("cacheStatusVersion must be 2", nameof(cacheStatusVersion));
        }
        if (cacheStatusVersion == 2 && expiresAtUtc is null)
        {
            throw new ArgumentException("expiresAtUtc is required for cacheStatusVersion 2", nameof(expiresAtUtc));
        }

        var version2 = cacheStatusVersion == 2;
        var requestedApps = version2
            ? cachedApps.ToList()
            : cachedApps.DistinctBy(app => app.AppId, StringComparer.OrdinalIgnoreCase).ToList();
        if (version2)
        {
            ValidateV2Apps(requestedApps);
        }

        if (requestedApps.Count == 0)
        {
            return new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = version2 ? null : "No app IDs provided",
                Version = cacheStatusVersion
            };
        }

        var inspectionEndsAtUtc = expiresAtUtc?.ToUniversalTime().Subtract(TimeSpan.FromSeconds(2));
        if (InspectionEnded(clock, inspectionEndsAtUtc))
        {
            return version2
                ? CreateUnknownResult(requestedApps, CacheReason.DeadlineReached, cacheStatusVersion)
                : new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "Checked 0 apps" };
        }

        using var deadlineCancellation = new CancellationTokenSource();
#pragma warning disable AsyncFixer02 // TimeProvider timer callbacks must signal cancellation synchronously.
        using var deadlineTimer = inspectionEndsAtUtc is { } inspectionEnd
            ? clock.CreateTimer(
                CancelDeadline,
                deadlineCancellation,
                inspectionEnd - clock.GetUtcNow(),
                Timeout.InfiniteTimeSpan)
            : null;
#pragma warning restore AsyncFixer02
        using var inspectionCancellation = inspectionEndsAtUtc is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCancellation.Token);
        var inspectionToken = inspectionCancellation.Token;
        List<AppInfo> allGames;
        try
        {
            allGames = await getAvailableGames(!version2, inspectionToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (InspectionEnded(clock, inspectionEndsAtUtc))
            {
                return version2
                    ? CreateUnknownResult(requestedApps, CacheReason.DeadlineReached, cacheStatusVersion)
                    : new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "Checked 0 apps" };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested || InspectionEnded(clock, inspectionEndsAtUtc))
        {
            return version2
                ? CreateUnknownResult(requestedApps, CacheReason.DeadlineReached, cacheStatusVersion)
                : new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "Checked 0 apps" };
        }
        catch (EpicLoginException)
        {
            throw;
        }
        catch
        {
            return version2
                ? CreateUnknownResult(requestedApps, CacheReason.InspectionFailed, cacheStatusVersion)
                : new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "Checked 0 apps" };
        }

        var gamesByAppId = allGames.ToDictionary(game => game.AppId, game => game, StringComparer.OrdinalIgnoreCase);
        var results = new List<AppCacheStatus>();
        for (var index = 0; index < requestedApps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (InspectionEnded(clock, inspectionEndsAtUtc))
            {
                AddDeadlineRows(results, requestedApps, index, version2);
                break;
            }

            var cachedApp = requestedApps[index];
            if (!gamesByAppId.TryGetValue(cachedApp.AppId, out var game))
            {
                if (version2)
                {
                    results.Add(CreateUnknown(cachedApp.AppId, CacheReason.MissingApp));
                }
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.BuildVersion))
            {
                if (version2)
                {
                    results.Add(CreateUnknown(cachedApp.AppId, CacheReason.ManifestUnavailable));
                }
                continue;
            }

            bool? isUpToDate;
            try
            {
                isUpToDate = string.IsNullOrWhiteSpace(cachedApp.Revision)
                    ? await inspectCache(game, inspectionToken)
                    : StringComparer.Ordinal.Equals(cachedApp.Revision, game.BuildVersion);
                cancellationToken.ThrowIfCancellationRequested();
                if (InspectionEnded(clock, inspectionEndsAtUtc))
                {
                    AddDeadlineRows(results, requestedApps, index, version2);
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested || InspectionEnded(clock, inspectionEndsAtUtc))
            {
                AddDeadlineRows(results, requestedApps, index, version2);
                break;
            }
            catch (EpicLoginException)
            {
                throw;
            }
            catch
            {
                if (version2)
                {
                    results.Add(CreateUnknown(cachedApp.AppId, CacheReason.InspectionFailed));
                }
                continue;
            }

            if (!isUpToDate.HasValue)
            {
                if (version2)
                {
                    results.Add(CreateUnknown(cachedApp.AppId, CacheReason.NoCacheEvidence));
                }
                continue;
            }

            var outcome = isUpToDate.Value ? CacheOutcome.Current : CacheOutcome.Outdated;
            results.Add(new AppCacheStatus
            {
                AppId = cachedApp.AppId,
                Name = version2 ? cachedApp.AppId : game.Title,
                IsUpToDate = isUpToDate.Value,
                Outcome = version2 ? outcome : null
            });
        }

        return new CacheStatusResult
        {
            Apps = results,
            Message = version2 ? null : $"Checked {results.Count} apps",
            Version = cacheStatusVersion
        };
    }

    private static void ValidateV2Apps(IEnumerable<CachedAppInput> cachedApps)
    {
        var appIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cachedApp in cachedApps)
        {
            if (cachedApp is null || string.IsNullOrWhiteSpace(cachedApp.AppId))
            {
                throw new ArgumentException("cachedApps must contain nonempty appId values", nameof(cachedApps));
            }
            if (!appIds.Add(cachedApp.AppId))
            {
                throw new ArgumentException("cachedApps must contain unique appId values", nameof(cachedApps));
            }
        }
    }

    private static bool InspectionEnded(TimeProvider clock, DateTimeOffset? inspectionEndsAtUtc)
        => inspectionEndsAtUtc is { } inspectionEnd && clock.GetUtcNow() >= inspectionEnd;

#pragma warning disable AsyncFixer02 // TimeProvider invokes timer callbacks synchronously.
    private static void CancelDeadline(object? state)
    {
        try
        {
            ((CancellationTokenSource)state!).Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal won a race with a timer callback that had already been selected to run.
        }
    }
#pragma warning restore AsyncFixer02

    private static void AddDeadlineRows(
        List<AppCacheStatus> results,
        IReadOnlyList<CachedAppInput> requestedApps,
        int startIndex,
        bool version2)
    {
        if (!version2)
        {
            return;
        }
        for (var index = startIndex; index < requestedApps.Count; index++)
        {
            results.Add(CreateUnknown(requestedApps[index].AppId, CacheReason.DeadlineReached));
        }
    }

    private static CacheStatusResult CreateUnknownResult(
        IEnumerable<CachedAppInput> cachedApps,
        CacheReason reason,
        int? cacheStatusVersion)
    {
        var apps = cachedApps.Select(app => CreateUnknown(app.AppId, reason)).ToList();
        return new CacheStatusResult
        {
            Apps = apps,
            Message = cacheStatusVersion == 2 ? null : $"Checked {apps.Count} apps",
            Version = cacheStatusVersion
        };
    }

    private static AppCacheStatus CreateUnknown(string appId, CacheReason reason) => new()
    {
        AppId = appId,
        Name = appId,
        IsUpToDate = false,
        Outcome = CacheOutcome.Unknown,
        Reason = reason
    };

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
    public Task<PrefillResult> PrefillAsync(PrefillOptions? options = null, CancellationToken cancellationToken = default)
        => PrefillAsync(options, null, cancellationToken);

    internal async Task<PrefillResult> PrefillAsync(
        PrefillOptions? options = null,
        PrefillRun? run = null,
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
                order: ResolvePrefillOrder(options),
                force: options.Force,
                cancellationToken: cancellationToken,
                manualIds: run?.Options.AppIds?.ToList(),
                run: run);

            _progress.OnOperationCompleted("Prefill operation", timer.Elapsed);

            return new PrefillResult
            {
                Success = true,
                TotalTime = timer.Elapsed
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _progress.OnLog(LogLevel.Info, "Prefill operation cancelled");
            throw;
        }
        catch (EpicLoginException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (run == null) { _progress.OnError("Prefill operation failed", ex); }
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                TotalTime = timer.Elapsed
            };
        }
    }

    // Maps the wire-level preset booleans onto a single strongly-typed ordering. Presets are mutually
    // exclusive in the UI; if more than one arrives, the most specific one wins.
    private static PrefillAppOrder ResolvePrefillOrder(PrefillOptions options)
    {
        if (options.Top)
            return PrefillAppOrder.Top;
        if (options.Recent)
            return PrefillAppOrder.Recent;
        if (options.DownloadAllOwnedGames)
            return PrefillAppOrder.AllOwned;
        return PrefillAppOrder.Selected;
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
        // Unconditional (not gated on _isInitialized): _epicManager is constructed synchronously
        // before the OAuth exchange completes, so a logout racing a mid-login task must still be
        // able to drop the in-memory OAuth token even though _isInitialized never flipped true.
        _epicManager?.ClearOAuthToken();
        _isInitialized = false;
        _progress.OnLog(LogLevel.Info, "Disconnected from Epic Games");
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        Shutdown();
        _epicManager?.Dispose();
        _isDisposed = true;
    }

    private void ThrowIfNotInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("EpicPrefillApi not initialized. Call InitializeAsync first.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
