namespace EpicPrefill.Handlers
{
    /// <summary>
    /// This class is responsible for interacting with the Epic Games Online Services API.
    /// The methods here are intended to query the API and return the data as is with no transformations,
    /// however fields that are not needed by EpicPrefill will be omitted for the sake of simplicity.
    /// </summary>
    public sealed class EpicGamesApi : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly HttpClientFactory _httpClientFactory;
        private readonly SemaphoreSlim _catalogLock = new(1, 1);

        public void Dispose() => _catalogLock.Dispose();

        private const string LauncherHost = "https://launcher-public-service-prod06.ol.epicgames.com";
        private const string CatalogHost = "https://catalog-public-service-prod06.ol.epicgames.com";
        private const string LibraryHost = "https://library-service.live.use1a.on.epicgames.com";

        private string MetadataCachePath => Path.Combine(AppConfig.TempDir, "metadataCache.json");

        /// <summary>
        /// How many freshly fetched apps to accumulate before the cache is written again. Small enough
        /// that a killed process loses little, large enough that a big library is not rewriting the whole
        /// file once per app.
        /// </summary>
        private const int MetadataCacheSaveInterval = 50;

        public EpicGamesApi(IAnsiConsole ansiConsole, HttpClientFactory httpClientFactory)
        {
            _ansiConsole = ansiConsole;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Reads a response body under its own time limit.  These requests are sent with
        /// ResponseHeadersRead, which returns as soon as the headers arrive and leaves every read of the
        /// body outside HttpClient.Timeout, so a service that answers with headers and then goes quiet
        /// would stall the prefill indefinitely without this bound.
        /// </summary>
        private static async Task<string> ReadBodyAsync(HttpResponseMessage response, string host, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AppConfig.DefaultRequestTimeout);
            try
            {
                return await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{host} sent a reply header and then stopped sending the body for {AppConfig.DefaultRequestTimeout.TotalSeconds} seconds.  " +
                    "The Epic service may be having an outage, so wait and try again.");
            }
        }

        /// <summary>
        /// Gets a list of all owned apps for the currently logged in account.
        /// </summary>
        /// <returns></returns>
        internal async Task<List<Asset>> GetOwnedAppsAsync(CancellationToken cancellationToken = default)
        {
            //TODO this should probably be inside a status spinner
            _ansiConsole.LogMarkupLine("Retrieving owned apps");
            var timer = Stopwatch.StartNew();

            // Build request
            var requestUri = new Uri($"{LauncherHost}/launcher/api/public/assets/Windows?label=Live");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

            // Send request
            using var httpClient = await _httpClientFactory.GetHttpClientAsync(cancellationToken);
            using var permit = PrefillRun.Current == null ? null : await PrefillRun.Current.AcquireAsync(cancellationToken);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new EpicLoginException("auth-lost");
            }
            response.EnsureSuccessStatusCode();

            // Read and deserialize
            var responseContent = await ReadBodyAsync(response, LauncherHost, cancellationToken);
            var ownedApps = JsonSerializer.Deserialize(responseContent, SerializationContext.Default.ListAsset);

            // Dumping out the raw json if -debug is enabled
            if (AppConfig.DebugLogs)
            {
                await File.WriteAllTextAsync(
                    $@"{AppConfig.DebugOutputDir}\assetsResponse.json",
                    responseContent,
                    cancellationToken);
            }

            // Removing anything related to unreal engine.  We're only interested in actual games
            var filteredApps = ownedApps.Where(e => e.Namespace != "ue")
                                                     // This namespace is related to unreal assets
                                                     .Where(e => e.Namespace != "89efe5924d3d467c839449ab6ab52e7f")
                                                     .ToList();

            _ansiConsole.LogMarkupLine($"Retrieved {Magenta(filteredApps.Count)} owned apps", timer);
            return filteredApps;
        }

        /// <summary>
        /// Retrieves cumulative playtime per owned artifact for the given account from Epic's library-service.
        /// Uses the same launcher OAuth token the daemon already holds (the endpoint requires the
        /// 'library:public:{accountId}:playtime:all READ' scope, which that token carries).
        ///
        /// Epic returns only a total-seconds figure per title, with no last-played timestamp, so this data
        /// backs a "most played" ordering (the Top preset) and cannot back a genuine "recently played" one.
        /// </summary>
        internal async Task<List<PlaytimeEntry>> GetPlaytimeAsync(string accountId, CancellationToken cancellationToken = default)
        {
            _ansiConsole.LogMarkupLine("Retrieving Epic playtime data");
            var timer = Stopwatch.StartNew();

            var requestUri = new Uri($"{LibraryHost}/library/api/public/playtime/account/{accountId}/all");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

            using var httpClient = await _httpClientFactory.GetHttpClientAsync(cancellationToken);
            using var permit = PrefillRun.Current == null ? null : await PrefillRun.Current.AcquireAsync(cancellationToken);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new EpicLoginException("auth-lost");
            }
            response.EnsureSuccessStatusCode();

            var responseContent = await ReadBodyAsync(response, LibraryHost, cancellationToken);
            var playtimes = JsonSerializer.Deserialize(responseContent, SerializationContext.Default.ListPlaytimeEntry);

            if (AppConfig.DebugLogs)
            {
                await File.WriteAllTextAsync($@"{AppConfig.DebugOutputDir}\playtimeResponse.json", responseContent, cancellationToken);
            }

            _ansiConsole.LogMarkupLine($"Retrieved playtime for {Magenta(playtimes?.Count ?? 0)} titles", timer);
            return playtimes ?? new List<PlaytimeEntry>();
        }

        /// <summary>
        /// Gets additional metadata for the list of apps passed in.  The majority of this metadata isn't important to our use case, however we primarily need
        /// to get this metadata in order to know the apps titles.
        ///
        /// If the metadata has already been loaded before then a cached copy will be returned from disk.  If any of the apps are new and have not had their
        /// metadata loaded already then they will be requested and cached for future use.
        /// </summary>
        public async Task<Dictionary<string, AppMetadataResponse>> LoadAppMetadataAsync(
            List<Asset> apps,
            CancellationToken cancellationToken = default)
        {
            await _catalogLock.WaitAsync(cancellationToken);
            try
            {
                var metadataDictionary = new Dictionary<string, AppMetadataResponse>();

                // Load cache from disk, if it exists
                if (File.Exists(MetadataCachePath))
                {
                    var allText = await File.ReadAllTextAsync(MetadataCachePath, cancellationToken);
                    // A cache file holding literal 'null' - a truncated write, or one an older build left
                    // behind - deserializes to null. Starting from an empty dictionary rebuilds it on this
                    // run, instead of throwing on every call forever because the file is only ever rewritten
                    // after a successful load. [16]
                    metadataDictionary = JsonSerializer.Deserialize(allText, SerializationContext.Default.DictionaryStringAppMetadataResponse)
                                         ?? new Dictionary<string, AppMetadataResponse>();
                }

                // Determine which apps don't already have their metadata loaded. A cache entry written
                // before artwork support existed deserializes with KeyImages == null, so it is treated
                // the same as a missing entry and re-requested - otherwise every already-cached app on
                // an existing install would keep its art-less cache forever.
                List<Asset> appsMissingMetadata = GetAppsMissingMetadata(apps, metadataDictionary)
                                                          .OrderBy(e => e.AppId)
                                                          .ToList();

                // If everything is cached, return
                if (!appsMissingMetadata.Any())
                {
                    return metadataDictionary;
                }

                await _ansiConsole.CreateSpectreProgress().StartAsync(async context =>
                {
                    var progressTask = context.AddTask("Loading app metadata...", maxValue: appsMissingMetadata.Count);
                    var unsavedApps = 0;

                    try
                    {
                        foreach (var app in appsMissingMetadata)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            AppMetadataResponse metadata;
                            try
                            {
                                metadata = await GetSingleAppMetadataAsync(app, cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (EpicLoginException) { throw; }
                            catch (Exception e)
                            {
                                // One delisted or region-locked catalog item must not end the run for every
                                // other app in the library. [15]
                                FileLogger.LogExceptionNoStackTrace($"Metadata request for app {app.AppId}", e);
                                progressTask.Increment(1);
                                continue;
                            }

                            // Epic answers with no usable entry for some catalog items, so there is nothing
                            // to record for this app. [15]
                            if (metadata == null)
                            {
                                progressTask.Increment(1);
                                continue;
                            }

                            // Epic omits keyImages entirely for some catalog items. Storing an empty list rather
                            // than null records that the app was checked and has no artwork, so it stops matching
                            // the cache-miss rule below instead of being re-requested on every future run.
                            metadata.KeyImages ??= new List<KeyImage>();
                            // Indexer, not .Add: an app can already be a key here with a stale (no-artwork)
                            // cached value, and this re-fetch is meant to replace that entry, not collide with it.
                            metadataDictionary[app.AppId] = metadata;
                            progressTask.Increment(1);

                            unsavedApps++;
                            if (unsavedApps >= MetadataCacheSaveInterval)
                            {
                                await SaveMetadataCacheAsync(metadataDictionary);
                                unsavedApps = 0;
                            }
                        }
                    }
                    finally
                    {
                        // A cancel or a failure part way through has to keep the apps already fetched.
                        // Without this a library too big to finish inside one attempt refetches from zero
                        // every retry and never converges. [13]
                        if (unsavedApps > 0)
                        {
                            try
                            {
                                await SaveMetadataCacheAsync(metadataDictionary);
                            }
                            catch (Exception e)
                            {
                                // Best effort while unwinding: a write failure here must not replace the
                                // exception that is already on its way out.
                                FileLogger.LogExceptionNoStackTrace("Writing the app metadata cache", e);
                            }
                        }
                    }
                });

                _ansiConsole.LogMarkupLine($"Loaded new app metadata for {LightYellow(appsMissingMetadata.Count)} apps");

                return metadataDictionary;
            }
            finally
            {
                _catalogLock.Release();
            }
        }

        /// <summary>
        /// Writes the app metadata cache to disk. Deliberately ignores the run's cancellation token:
        /// this also runs while unwinding a cancelled run, and a cancelled token would throw away the
        /// very progress the write exists to keep.
        /// </summary>
        private async Task SaveMetadataCacheAsync(Dictionary<string, AppMetadataResponse> metadataDictionary)
        {
            var serialized = JsonSerializer.Serialize(metadataDictionary, SerializationContext.Default.DictionaryStringAppMetadataResponse);
            var temporary = MetadataCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, serialized, CancellationToken.None);
                File.Move(temporary, MetadataCachePath, true);
            }
            finally
            {
                if (File.Exists(temporary)) { File.Delete(temporary); }
            }
        }

        /// <summary>
        /// An app needs its metadata (re-)requested when it isn't cached at all, or when the cached
        /// entry has no artwork - which is what every entry written before artwork support existed
        /// looks like once deserialized. Pulled out of <see cref="LoadAppMetadataAsync"/> so the
        /// cache-miss rule can be tested without a disk or a network call.
        /// </summary>
        internal static IEnumerable<Asset> GetAppsMissingMetadata(
            List<Asset> apps,
            Dictionary<string, AppMetadataResponse> cachedMetadata)
        {
            // A null cached entry counts as a miss too - that is what an older build wrote whenever Epic
            // answered with no usable metadata for an app. [16]
            return apps.Where(app => !cachedMetadata.TryGetValue(app.AppId, out var cached) || cached?.KeyImages == null);
        }

        /// <summary>
        /// Gets additional metadata about a single app from Epic's API.  The only real thing that we are really interested in here is the app's title.
        /// </summary>
        private async Task<AppMetadataResponse> GetSingleAppMetadataAsync(
            Asset app,
            CancellationToken cancellationToken = default)
        {
            // Building request
            var baseUrl = $"{CatalogHost}/catalog/api/shared/namespace/{app.Namespace}/bulk/items";
            var requestParams = new Dictionary<string, string>
            {
                { "id", app.CatalogItemId },
                { "includeDLCDetails", "true" },
                { "includeMainGameDetails", "True" },
                { "country", "US" },
                { "locale", "en" }
            };
            var urlWithParams = QueryHelpers.AddQueryString(baseUrl, requestParams);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(urlWithParams));

            // Send request
            using var httpClient = await _httpClientFactory.GetHttpClientAsync(cancellationToken);
            using var permit = PrefillRun.Current == null ? null : await PrefillRun.Current.AcquireAsync(cancellationToken);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new EpicLoginException("auth-lost");
            }
            response.EnsureSuccessStatusCode();

            var responseContent = await ReadBodyAsync(response, CatalogHost, cancellationToken);
            var appMetadata = JsonSerializer.Deserialize(responseContent, SerializationContext.Default.DictionaryStringAppMetadataResponse);

            // Dumping out the raw json if -debug is enabled
            if (AppConfig.DebugLogs)
            {
                await File.WriteAllTextAsync(
                    $@"{AppConfig.MetadataOutputDir}\{app.AppId}.json",
                    responseContent,
                    cancellationToken);
            }

            // Epic returns an empty body, or a null entry, for catalog items that are delisted or not
            // sold in this region. Handing back null lets the caller skip that one app instead of the
            // whole batch dying on it. [15]
            return appMetadata?.Values.FirstOrDefault();
        }

        //TODO comment
        public async Task<ManifestUrl> GetManifestDownloadUrlAsync(
            AppInfo app,
            CancellationToken cancellationToken = default)
        {
            var url = $"{LauncherHost}/launcher/api/public/assets/v2/platform/Windows/namespace/{app.Namespace}/" +
                            $"catalogItem/{app.CatalogItemId}/app/{app.AppId}/label/Live";

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            using var httpClient = await _httpClientFactory.GetHttpClientAsync(cancellationToken);
            using var permit = PrefillRun.Current == null ? null : await PrefillRun.Current.AcquireAsync(cancellationToken);
            using var response = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new EpicLoginException("auth-lost");
            }
            response.EnsureSuccessStatusCode();

            var responseContent = await ReadBodyAsync(response, LauncherHost, cancellationToken);
            ManifestResponse deserialized = JsonSerializer.Deserialize(responseContent, SerializationContext.Default.ManifestResponse);

            // Dumping out the raw json if -debug is enabled
            if (AppConfig.DebugLogs)
            {
                await File.WriteAllTextAsync(
                    $@"{AppConfig.DownloadUrlPath}\{app.AppId}.json",
                    responseContent,
                    cancellationToken);
            }

            var allManifests = deserialized.elements.First().manifests.ToList();
            //TODO document why this is.  I don't remember exactly why it needs to be the one that has this query param.
            return allManifests.First(e => e.queryParams.Any(e2 => e2.Name == "f_token"));
        }
    }
}
