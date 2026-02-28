using System.Threading;

namespace EpicPrefill.Handlers
{
    public sealed class DownloadHandler : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly HttpClient _client;
        private readonly IPrefillProgress _progress;

        /// <summary>
        /// The URL/IP Address where the Lancache has been detected.
        /// </summary>
        private string _lancacheAddress;

        public DownloadHandler(IAnsiConsole ansiConsole, IPrefillProgress? progress = null)
        {
            _ansiConsole = ansiConsole;
            _progress = progress ?? NullProgress.Instance;

            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("User-Agent", AppConfig.DefaultUserAgent);
        }

        //TODO document allManifestUrls
        //TODO why does the manifest url need to be passed in?
        /// <summary>
        /// Attempts to download all queued requests.  If all downloads are successful, will return true.
        /// In the case of any failed downloads, the failed downloads will be retried up to 3 times.  If the downloads fail 3 times, then
        /// false will be returned
        /// </summary>
        /// <returns>True if all downloads succeeded.  False if downloads failed 3 times.</returns>
        public async Task<bool> DownloadQueuedChunksAsync(List<QueuedRequest> queuedRequests, ManifestUrl manifestUrl, string? appId = null, string? appName = null, CancellationToken cancellationToken = default)
        {
#if DEBUG
            if (AppConfig.SkipDownloads)
            {
                return true;
            }
#endif
            if (_lancacheAddress == null)
            {
                var cdnUrl = manifestUrl.ManifestDownloadUri.Host;
                _lancacheAddress = await LancacheIpResolver.ResolveLancacheIpAsync(_ansiConsole, cdnUrl);
            }

            int retryCount = 0;
            var failedRequests = new ConcurrentBag<QueuedRequest>();
            await _ansiConsole.CreateSpectreProgress(TransferSpeedUnit.Bits).StartAsync(async ctx =>
            {
                //TODO should probably implement cycling through available CDNs when one fails
                // Run the initial download
                failedRequests = await AttemptDownloadAsync(ctx, "Downloading..", queuedRequests, new Uri(manifestUrl.ManifestDownloadUrl), appId: appId, appName: appName, cancellationToken: cancellationToken);

                // Handle any failed requests
                while (failedRequests.Any() && retryCount < 2)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    retryCount++;
                    await Task.Delay(2000 * retryCount, cancellationToken);
                    var upstreamCdn = new Uri(manifestUrl.ManifestDownloadUrl);
                    failedRequests = await AttemptDownloadAsync(ctx, $"Retrying  {retryCount}..", failedRequests.ToList(), upstreamCdn, forceRecache: true, appId: appId, appName: appName, cancellationToken: cancellationToken);
                }
            });

            // Handling final failed requests
            if (!failedRequests.Any())
            {
                return true;
            }

            _ansiConsole.LogMarkupError($"Download failed with {LightYellow(failedRequests.Count)} failed requests");
            _ansiConsole.WriteLine();
            return false;
        }

        /// <summary>
        /// Attempts to download the specified requests.  Returns a list of any requests that have failed for any reason.
        /// </summary>
        /// <param name="forceRecache">When specified, will cause the cache to delete the existing cached data for a request, and redownload it again.</param>
        /// <returns>A list of failed requests</returns>
        private async Task<ConcurrentBag<QueuedRequest>> AttemptDownloadAsync(ProgressContext ctx, string taskTitle, List<QueuedRequest> requestsToDownload,
                                                                                Uri upstreamCdn, bool forceRecache = false, string? appId = null, string? appName = null, CancellationToken cancellationToken = default)
        {
            double requestTotalSize = requestsToDownload.Sum(e => (long)e.DownloadSizeBytes);
            var progressTask = ctx.AddTask(taskTitle, new ProgressTaskSettings { MaxValue = requestTotalSize });

            var failedRequests = new ConcurrentBag<QueuedRequest>();
            long bytesDownloaded = 0;
            var startTime = DateTime.UtcNow;
            var lastProgressReport = DateTime.MinValue;
            var progressThrottle = TimeSpan.FromMilliseconds(250);

            var progressAppId = appId ?? upstreamCdn.Host;
            var progressAppName = appName ?? upstreamCdn.Host;

            await Parallel.ForEachAsync(requestsToDownload, new ParallelOptions { MaxDegreeOfParallelism = AppConfig.MaxConcurrentRequests, CancellationToken = cancellationToken }, async (chunk, ct) =>
            {
                try
                {
                    var url = Path.Join($"http://{_lancacheAddress}", chunk.DownloadUrl);
                    if (forceRecache)
                    {
                        url += "?nocache=1";
                    }

                    using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                    requestMessage.Headers.Host = upstreamCdn.Host;

                    using var response = await _client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);
                    using Stream responseStream = await response.Content.ReadAsStreamAsync(ct);
                    response.EnsureSuccessStatusCode();

                    // Don't save the data anywhere, so we don't have to waste time writing it to disk.
                    var buffer = new byte[4096];
                    while (await responseStream.ReadAsync(buffer, ct) != 0)
                    {
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Propagate cancellation - let Parallel.ForEachAsync stop
                    throw;
                }
                catch (Exception e)
                {
                    failedRequests.Add(chunk);
                    FileLogger.LogExceptionNoStackTrace($"Request {chunk.DownloadUrl}", e);
                }
                progressTask.Increment(chunk.DownloadSizeBytes);

                // Report progress via IPrefillProgress (throttled)
                var downloaded = Interlocked.Add(ref bytesDownloaded, (long)chunk.DownloadSizeBytes);
                var now = DateTime.UtcNow;
                if (now - lastProgressReport >= progressThrottle)
                {
                    lastProgressReport = now;
                    var elapsed = now - startTime;
                    var bytesPerSecond = elapsed.TotalSeconds > 0 ? downloaded / elapsed.TotalSeconds : 0;

                    _progress.OnDownloadProgress(new DownloadProgressInfo
                    {
                        AppId = progressAppId,
                        AppName = progressAppName,
                        TotalBytes = (long)requestTotalSize,
                        BytesDownloaded = downloaded,
                        BytesPerSecond = bytesPerSecond,
                        Elapsed = elapsed
                    });
                }
            });

            // Making sure the progress bar is always set to its max value, in-case some unexpected error leaves the progress bar showing as unfinished
            progressTask.Increment(progressTask.MaxValue);

            // Send a final progress report to ensure the client sees 100% completion
            var finalElapsed = DateTime.UtcNow - startTime;
            var finalBytesPerSecond = finalElapsed.TotalSeconds > 0 ? bytesDownloaded / finalElapsed.TotalSeconds : 0;
            _progress.OnDownloadProgress(new DownloadProgressInfo
            {
                AppId = progressAppId,
                AppName = progressAppName,
                TotalBytes = (long)requestTotalSize,
                BytesDownloaded = (long)requestTotalSize,
                BytesPerSecond = finalBytesPerSecond,
                Elapsed = finalElapsed
            });

            return failedRequests;
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
