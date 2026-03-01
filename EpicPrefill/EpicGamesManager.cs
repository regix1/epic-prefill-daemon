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

        public async Task DownloadMultipleAppsAsync(bool downloadAllOwnedGames, bool force = false, List<string> manualIds = null, CancellationToken cancellationToken = default)
        {
            var allOwnedGames = await GetAvailableGamesAsync();

            var appIdsToDownload = LoadPreviouslySelectedApps();
            if (manualIds != null)
            {
                appIdsToDownload.AddRange(manualIds);
            }
            if (downloadAllOwnedGames)
            {
                appIdsToDownload = allOwnedGames.Select(e => e.AppId).ToList();
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
                var app = new AppInfo
                {
                    AppId = asset.AppId,
                    BuildVersion = asset.BuildVersion,
                    CatalogItemId = asset.CatalogItemId,
                    Namespace = asset.Namespace,
                    Title = appMetadata[asset.AppId].Title
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