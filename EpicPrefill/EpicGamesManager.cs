namespace EpicPrefill
{
    public sealed class EpicGamesManager : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly DownloadArguments _downloadArgs;
        private readonly IEpicAuthProvider? _authProvider;
        private readonly IPrefillProgress _progress;

        private readonly DownloadHandler _downloadHandler;
        private readonly EpicGamesApi _epicApi;
        private readonly AppInfoHandler _appInfoHandler;
        private readonly ManifestHandler _manifestHandler;
        private readonly UserAccountManager _userAccountManager;
        private readonly HttpClientFactory _httpClientFactory;

        private readonly PrefillSummaryResult _prefillSummaryResult = new PrefillSummaryResult();

        public EpicGamesManager(IAnsiConsole ansiConsole, DownloadArguments downloadArgs, IEpicAuthProvider? authProvider = null, IPrefillProgress? progress = null)
        {
            _ansiConsole = ansiConsole;
            _downloadArgs = downloadArgs;
            _authProvider = authProvider;
            _progress = progress ?? NullProgress.Instance;

            // Setup required classes
            _downloadHandler = new DownloadHandler(_ansiConsole, _progress);
            _appInfoHandler = new AppInfoHandler(_ansiConsole);
            _userAccountManager = UserAccountManager.LoadFromFile(_ansiConsole, _authProvider!);

            _httpClientFactory = new HttpClientFactory(_ansiConsole, _userAccountManager);
            _epicApi = new EpicGamesApi(_ansiConsole, _httpClientFactory);
            _manifestHandler = new ManifestHandler(_ansiConsole, _httpClientFactory);
        }

        public string? DisplayName => _userAccountManager.OauthToken?.DisplayName;

        public async Task InitializeAsync()
        {
            await _userAccountManager.LoginAsync();
        }

        public async Task DownloadMultipleAppsAsync(PrefillAppOrder order, bool force = false, List<string> manualIds = null, CancellationToken cancellationToken = default)
        {
            var allOwnedGames = await GetAvailableGamesAsync();

            List<string> appIdsToDownload;
            if (manualIds != null && manualIds.Count > 0)
            {
                // An explicit id list always wins over the preset ordering.
                appIdsToDownload = new List<string>(manualIds);
            }
            else
            {
                appIdsToDownload = await ResolveAppIdsForOrderAsync(order, allOwnedGames, cancellationToken);
            }

            // Whitespace divider
            _ansiConsole.WriteLine();

            _progress.OnLog(LogLevel.Info, $"Starting prefill of {appIdsToDownload.Count} apps");

            foreach (var appId in appIdsToDownload)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AppInfo? app = null;
                try
                {
                    app = allOwnedGames.FirstOrDefault(e => e.AppId == appId);
                    if (app == null)
                    {
                        _progress.OnLog(LogLevel.Warning, $"App {appId} not found in owned games, skipping");
                        _prefillSummaryResult.FailedApps++;
                        _progress.OnAppCompleted(
                            new AppDownloadInfo { AppId = appId, Name = appId, TotalBytes = 0 },
                            AppDownloadResult.Failed);
                        continue;
                    }

                    await DownloadSingleAppAsync(app, force, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Propagate cancellation - don't treat it as a download error
                    throw;
                }
                catch (Exception e) when (e is LancacheNotFoundException)
                {
                    // We'll want to bomb out the entire process for these exceptions, as they mean we can't prefill any apps at all
                    throw;
                }
                catch (Exception e)
                {
                    // Need to catch any exceptions that might happen during a single download, so that the other apps won't be affected
                    var appName = app?.Title ?? appId;
                    _progress.OnLog(LogLevel.Error, $"Download error for {appName}: {e.Message}");
                    _prefillSummaryResult.FailedApps++;

                    _progress.OnAppCompleted(
                        new AppDownloadInfo { AppId = app?.AppId ?? appId, Name = appName, TotalBytes = 0 },
                        AppDownloadResult.Failed);
                }
            }

            _ansiConsole.LogMarkupLine("Prefill complete!");
            _prefillSummaryResult.RenderSummaryTable(_ansiConsole);

            // Notify completion via progress interface
            _progress.OnPrefillCompleted(new PrefillSummary
            {
                TotalApps = _prefillSummaryResult.AlreadyUpToDate + _prefillSummaryResult.Updated + _prefillSummaryResult.FailedApps,
                UpdatedApps = _prefillSummaryResult.Updated,
                AlreadyUpToDate = _prefillSummaryResult.AlreadyUpToDate,
                FailedApps = _prefillSummaryResult.FailedApps,
                TotalBytesTransferred = (long)_prefillSummaryResult.TotalBytesTransferred.Bytes,
                TotalTime = _prefillSummaryResult.PrefillElapsedTime.Elapsed
            });
        }

        /// <summary>
        /// Resolves the ordered list of app ids to prefill for the requested preset. Every branch is
        /// designed to degrade gracefully to a full owned-games prefill rather than prefilling nothing.
        /// </summary>
        private async Task<List<string>> ResolveAppIdsForOrderAsync(PrefillAppOrder order, List<AppInfo> allOwnedGames, CancellationToken cancellationToken)
        {
            switch (order)
            {
                case PrefillAppOrder.AllOwned:
                    return allOwnedGames.Select(e => e.AppId).ToList();

                case PrefillAppOrder.Top:
                    return await ResolveMostPlayedAppIdsAsync(allOwnedGames, cancellationToken);

                case PrefillAppOrder.Recent:
                    // Epic's public API returns only cumulative playtime, never a per-title last-played
                    // timestamp, so a real recently-played ordering cannot be produced. Fall back to a full
                    // prefill and say why, instead of shipping a misleading "recent" ordering.
                    _progress.OnLog(LogLevel.Warning,
                        "Epic does not expose a recently-played signal (its API returns only cumulative playtime), " +
                        "so the Recent preset prefills all owned games instead.");
                    return allOwnedGames.Select(e => e.AppId).ToList();

                case PrefillAppOrder.Selected:
                default:
                    return LoadPreviouslySelectedApps();
            }
        }

        /// <summary>
        /// Orders owned games by cumulative Epic playtime, most-played first. Falls back to a full
        /// owned-games prefill (with a clear log line) whenever the playtime data is unavailable,
        /// empty, or cannot be loaded, so the Top preset never silently prefills nothing.
        /// </summary>
        private async Task<List<string>> ResolveMostPlayedAppIdsAsync(List<AppInfo> allOwnedGames, CancellationToken cancellationToken)
        {
            try
            {
                var accountId = _userAccountManager.OauthToken?.AccountId;
                if (string.IsNullOrEmpty(accountId))
                {
                    _progress.OnLog(LogLevel.Warning, "Top preset: no Epic account id is available, prefilling all owned games instead.");
                    return allOwnedGames.Select(e => e.AppId).ToList();
                }

                var playtimes = await _epicApi.GetPlaytimeAsync(accountId, cancellationToken);
                var mostPlayed = PlaytimeOrdering.OrderOwnedGamesByMostPlayed(allOwnedGames, playtimes);

                if (mostPlayed.Count == 0)
                {
                    _progress.OnLog(LogLevel.Warning, "Top preset: Epic reported no playtime for any owned game, prefilling all owned games instead.");
                    return allOwnedGames.Select(e => e.AppId).ToList();
                }

                _progress.OnLog(LogLevel.Info, $"Top preset: prefilling {mostPlayed.Count} owned games ordered by total playtime (most played first).");
                return mostPlayed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Only a genuine user cancellation (token signalled) propagates. An HttpClient timeout also
                // throws OperationCanceledException but with the token unsignalled; that falls through to the
                // general handler below and degrades to a full owned-games prefill instead of aborting the run.
                throw;
            }
            catch (Exception e)
            {
                _progress.OnLog(LogLevel.Error, $"Top preset: could not load Epic playtime ({e.Message}); prefilling all owned games instead.");
                return allOwnedGames.Select(e => e.AppId).ToList();
            }
        }

        private async Task DownloadSingleAppAsync(AppInfo app, bool force = false, CancellationToken cancellationToken = default)
        {
            // Only download the app if it isn't up to date
            if (force == false && _downloadArgs.Force == false && _appInfoHandler.AppIsUpToDate(app))
            {
                _prefillSummaryResult.AlreadyUpToDate++;
                var cachedAppInfo = new AppDownloadInfo { AppId = app.AppId, Name = app.Title, TotalBytes = 0 };
                _progress.OnAppStarted(cachedAppInfo);
                _progress.OnAppCompleted(cachedAppInfo, AppDownloadResult.AlreadyUpToDate);
                return;
            }

            _progress.OnLog(LogLevel.Info, $"Starting download: {app.Title} ({app.AppId}), version: {app.BuildVersion}");

            // Download the latest manifest, and build the list of requests in order to download the app
            ManifestUrl manifestDownloadUrl;
            try
            {
                manifestDownloadUrl = await _epicApi.GetManifestDownloadUrlAsync(app);
                _progress.OnLog(LogLevel.Info, $"Manifest URL: {manifestDownloadUrl.ManifestDownloadUrlWithParams}");
                _progress.OnLog(LogLevel.Info, $"Manifest CDN host: {manifestDownloadUrl.ManifestDownloadUri.Host}");
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Failed to get manifest URL for {app.Title} ({app.AppId}): {ex.Message}");
                _progress.OnLog(LogLevel.Error, $"API URL was: launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/v2/platform/Windows/namespace/{app.Namespace}/catalogItem/{app.CatalogItemId}/app/{app.AppId}/label/Live");
                throw;
            }

            byte[] rawManifestBytes;
            try
            {
                rawManifestBytes = await _manifestHandler.DownloadManifestAsync(app, manifestDownloadUrl);
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Failed to download manifest for {app.Title}: {ex.Message}");
                _progress.OnLog(LogLevel.Error, $"Manifest download URL: {manifestDownloadUrl.ManifestDownloadUrlWithParams}");
                throw;
            }

            var chunkDownloadQueue = _manifestHandler.ParseManifest(rawManifestBytes, manifestDownloadUrl);

            // Logging some metadata about the downloads
            var downloadTimer = Stopwatch.StartNew();
            var totalBytes = ByteSize.FromBytes(chunkDownloadQueue.Sum(e => (long)e.DownloadSizeBytes));
            _prefillSummaryResult.TotalBytesTransferred += totalBytes;

            // Notify that app download is starting
            var appDownloadInfo = new AppDownloadInfo
            {
                AppId = app.AppId,
                Name = app.Title,
                TotalBytes = (long)totalBytes.Bytes,
                ChunkCount = chunkDownloadQueue.Count
            };
            _progress.OnAppStarted(appDownloadInfo);

            _ansiConsole.LogMarkupVerbose($"Downloading {Magenta(totalBytes.ToDecimalString())} from {LightYellow(chunkDownloadQueue.Count)} chunks");

            // Finally run the queued downloads
            var downloadSuccessful = await _downloadHandler.DownloadQueuedChunksAsync(chunkDownloadQueue, manifestDownloadUrl, appId: app.AppId, appName: app.Title, cancellationToken: cancellationToken);
            if (downloadSuccessful)
            {
                // Logging some metrics about the download
                _ansiConsole.LogMarkupLine($"Finished in {LightYellow(downloadTimer.FormatElapsedString())} - {Magenta(totalBytes.CalculateBitrate(downloadTimer))}");
                _ansiConsole.WriteLine();

                _appInfoHandler.MarkDownloadAsSuccessful(app);
                _prefillSummaryResult.Updated++;
                _progress.OnAppCompleted(appDownloadInfo, AppDownloadResult.Success);
            }
            else
            {
                _prefillSummaryResult.FailedApps++;
                _progress.OnAppCompleted(appDownloadInfo, AppDownloadResult.Failed);
            }
        }

        //TODO comment
        public async Task<List<AppInfo>> GetAvailableGamesAsync()
        {
            var ownedAssets = await _epicApi.GetOwnedAppsAsync();
            var appMetadata = await _epicApi.LoadAppMetadataAsync(ownedAssets);

            var ownedApps = new List<AppInfo>();
            foreach (Asset asset in ownedAssets)
            {
                var metadata = appMetadata[asset.AppId];
                var app = new AppInfo
                {
                    AppId = asset.AppId,
                    BuildVersion = asset.BuildVersion,
                    CatalogItemId = asset.CatalogItemId,
                    Namespace = asset.Namespace,
                    Title = metadata.Title
                };
                ownedApps.Add(app);
            }

            return ownedApps.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Checks if an app's current build version has been previously downloaded.
        /// </summary>
        public bool IsAppUpToDate(AppInfo app) => _appInfoHandler.AppIsUpToDate(app);

        /// <summary>
        /// Gets the download size for an app by downloading and parsing its manifest.
        /// Returns 0 if the size cannot be determined.
        /// </summary>
        /// <summary>
        /// Gets the manifest download URL for an app, which contains the CDN host and chunk base URL.
        /// </summary>
        public async Task<ManifestUrl> GetManifestDownloadUrlAsync(AppInfo app)
        {
            return await _epicApi.GetManifestDownloadUrlAsync(app);
        }

        public async Task<long> GetAppDownloadSizeAsync(AppInfo app)
        {
            var manifestDownloadUrl = await _epicApi.GetManifestDownloadUrlAsync(app);
            var rawManifestBytes = await _manifestHandler.DownloadManifestAsync(app, manifestDownloadUrl);
            var chunkDownloadQueue = _manifestHandler.ParseManifest(rawManifestBytes, manifestDownloadUrl);
            return chunkDownloadQueue.Sum(e => (long)e.DownloadSizeBytes);
        }

        public void Dispose()
        {
            _downloadHandler.Dispose();
        }

        #region Select Apps

        public void SetAppsAsSelected(List<TuiAppInfo> userSelected)
        {
            List<string> selectedAppIds = userSelected.Where(e => e.IsSelected)
                                                      .Select(e => e.AppId)
                                                      .ToList();
            File.WriteAllText(AppConfig.UserSelectedAppsPath, JsonSerializer.Serialize(selectedAppIds, SerializationContext.Default.ListString));

            _ansiConsole.LogMarkupLine($"Selected {Magenta(selectedAppIds.Count)} apps to prefill!  ");
        }

        public List<string> LoadPreviouslySelectedApps()
        {
            if (File.Exists(AppConfig.UserSelectedAppsPath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(AppConfig.UserSelectedAppsPath), SerializationContext.Default.ListString);
            }
            return new List<string>();
        }

        #endregion
    }
}