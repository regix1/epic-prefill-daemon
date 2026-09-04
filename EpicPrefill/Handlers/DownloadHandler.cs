using System.Threading;

namespace EpicPrefill.Handlers
{
    public sealed class DownloadHandler : IDisposable
    {
        // Every retry goes back to the same cache address, so a second one buys nothing when that address
        // has stopped answering and only adds another pass over every failed chunk.
        private const int MaxDownloadRetries = 1;

        /// <summary>
        /// How many chunks may fail with nothing at all transferred before the rest of the queue is
        /// abandoned.  Two full waves, so that a queue whose first wave fails and then recovers is still
        /// downloaded; a single wave would give up on a download that was about to work.
        /// </summary>
        private static int FailuresBeforeCacheIsDown => AppConfig.MaxConcurrentRequests * 2;

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
            // Bounds the wait for the reply headers, which is all HttpClient.Timeout covers under
            // ResponseHeadersRead.  Left at the 100 second default this was the largest part of the time
            // spent failing against a cache that has gone quiet.
            _client.Timeout = AppConfig.DefaultRequestTimeout;
            _client.DefaultRequestHeaders.Add("User-Agent", AppConfig.DefaultUserAgent);
        }

        internal DownloadHandler(IAnsiConsole ansiConsole, HttpMessageHandler handler, string lancacheAddress, IPrefillProgress? progress = null)
        {
            _ansiConsole = ansiConsole;
            _progress = progress ?? NullProgress.Instance;
            _lancacheAddress = lancacheAddress;

            _client = new HttpClient(handler, disposeHandler: false);
            _client.Timeout = AppConfig.DefaultRequestTimeout;
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
            if (AppConfig.SkipDownloads)
            {
                return true;
            }
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
                while (failedRequests.Any() && retryCount < MaxDownloadRetries)
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
            // Counted separately from bytesDownloaded, which is incremented for failed chunks too because it
            // drives the progress bar, and from failedRequests, whose Count walks the whole bag.
            var succeededCount = 0;
            var failedCount = 0;
            var cacheIsDown = 0;
            long bytesDownloaded = 0;
            var startTime = DateTime.UtcNow;
            long lastProgressReportTicks = 0;
            var progressThrottle = TimeSpan.FromMilliseconds(250);

            var progressAppId = appId ?? upstreamCdn.Host;
            var progressAppName = appName ?? upstreamCdn.Host;

            await Parallel.ForEachAsync(requestsToDownload, new ParallelOptions { MaxDegreeOfParallelism = AppConfig.MaxConcurrentRequests, CancellationToken = cancellationToken }, async (chunk, ct) =>
            {
                if (Volatile.Read(ref cacheIsDown) != 0)
                {
                    failedRequests.Add(chunk);
                    return;
                }

                try
                {
                    var url = Path.Join($"http://{_lancacheAddress}", chunk.DownloadUrl);
                    if (forceRecache)
                    {
                        url += "?nocache=1";
                    }

                    using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                    requestMessage.Headers.Host = upstreamCdn.Host;

                    // ResponseHeadersRead returns once the headers arrive, which leaves every read below
                    // outside HttpClient.Timeout.  The clock is restarted after each read, so a slow but
                    // living connection is left alone and only one that stops sending entirely is cut off.
                    using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    using var response = await _client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, stallCts.Token);
                    using Stream responseStream = await response.Content.ReadAsStreamAsync(stallCts.Token);
                    response.EnsureSuccessStatusCode();

                    // Don't save the data anywhere, so we don't have to waste time writing it to disk.
                    var buffer = new byte[4096];
                    try
                    {
                        stallCts.CancelAfter(AppConfig.DefaultRequestTimeout);
                        while (await responseStream.ReadAsync(buffer, stallCts.Token) != 0)
                        {
                            stallCts.CancelAfter(AppConfig.DefaultRequestTimeout);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"{upstreamCdn.Host} stopped sending data for {AppConfig.DefaultRequestTimeout.TotalSeconds} seconds partway through a chunk.  " +
                            "The cache or its upstream CDN may be unreachable.");
                    }

                    Interlocked.Increment(ref succeededCount);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Propagate cancellation - let Parallel.ForEachAsync stop
                    throw;
                }
                catch (Exception e)
                {
                    failedRequests.Add(chunk);
                    Interlocked.Increment(ref failedCount);
                    FileLogger.LogExceptionNoStackTrace($"Request {chunk.DownloadUrl}", e);
                }

                // A source that has handed back nothing at all after this many failures is not going to
                // start working further down the queue, and walking the rest of it costs one timeout per
                // wave.  Any single success means the source works and turns this off for good.
                if (Volatile.Read(ref succeededCount) == 0 && Volatile.Read(ref failedCount) >= FailuresBeforeCacheIsDown)
                {
                    Volatile.Write(ref cacheIsDown, 1);
                }

                progressTask.Increment(chunk.DownloadSizeBytes);

                // Report progress via IPrefillProgress (throttled)
                var downloaded = Interlocked.Add(ref bytesDownloaded, (long)chunk.DownloadSizeBytes);
                var now = DateTime.UtcNow;
                var nowTicks = now.Ticks;
                long prevTicks = Volatile.Read(ref lastProgressReportTicks);
                if (prevTicks == 0 || (nowTicks - prevTicks) >= progressThrottle.Ticks)
                {
                    if (Interlocked.CompareExchange(ref lastProgressReportTicks, nowTicks, prevTicks) == prevTicks)
                    {
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
                }
            });

            // Thrown out here rather than from inside the loop body: the body's own catch would swallow it,
            // and concurrent throws arrive wrapped in an AggregateException with the message buried.
            if (Volatile.Read(ref cacheIsDown) != 0)
            {
                throw new TimeoutException(
                    $"Gave up downloading from {_lancacheAddress}.  The first {FailuresBeforeCacheIsDown} requests for {upstreamCdn.Host} all failed and not one byte arrived, " +
                    "so the rest of the queue was abandoned rather than waiting on every remaining chunk.  " +
                    "Check that the cache is running and that it can reach the internet.");
            }

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
