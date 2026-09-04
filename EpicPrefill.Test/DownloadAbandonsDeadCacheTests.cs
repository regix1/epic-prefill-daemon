using System.Net;
using Spectre.Console;
using EpicPrefill.Handlers;
using EpicPrefill.Models;
using EpicPrefill.Models.ApiResponses;

namespace EpicPrefill.Test;

public sealed class DownloadAbandonsDeadCacheTests
{
    private const string CacheAddress = "10.0.0.42";
    private const string CdnHost = "epicgames-download1.akamaized.net";
    private const int QueueLength = 200;

    [Fact]
    public async Task DownloadQueuedChunksAsync_CacheReturnsNothing_AbandonsQueue()
    {
        using var handler = new StubCacheHandler(failFirst: int.MaxValue);
        var downloadHandler = new DownloadHandler(CreateConsole(), handler, CacheAddress);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => downloadHandler.DownloadQueuedChunksAsync(CreateQueue(), CreateManifestUrl()));

        Assert.Contains(CacheAddress, exception.Message, StringComparison.Ordinal);
        Assert.Contains(CdnHost, exception.Message, StringComparison.Ordinal);

        // The point of the rule: the queue stops being walked, so the cost no longer grows with its length.
        Assert.True(
            handler.AttemptCount < QueueLength,
            $"Expected the walk to stop early, but all {handler.AttemptCount} requests were attempted.");
    }

    [Fact]
    public async Task DownloadQueuedChunksAsync_FirstWaveFailsThenRecovers_StillCompletes()
    {
        using var handler = new StubCacheHandler(failFirst: 30);
        var downloadHandler = new DownloadHandler(CreateConsole(), handler, CacheAddress);

        var succeeded = await downloadHandler.DownloadQueuedChunksAsync(CreateQueue(), CreateManifestUrl());

        Assert.True(succeeded);
        Assert.True(
            handler.AttemptCount > QueueLength,
            "Every chunk should have been attempted, plus a retry for the ones that failed.");
    }

    /// <summary>
    /// Spectre renders the progress bar from many threads at once, and a console backed by a plain
    /// StringBuilder corrupts under that.  Nothing here asserts on console output, so the writes are
    /// discarded through a synchronized writer instead.
    /// </summary>
    private static IAnsiConsole CreateConsole()
    {
        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(TextWriter.Synchronized(TextWriter.Null))
        });
    }

    private static List<QueuedRequest> CreateQueue()
    {
        return Enumerable.Range(0, QueueLength)
            .Select(i => new QueuedRequest { DownloadUrl = $"/chunks/{i}.chunk", DownloadSizeBytes = 4096 })
            .ToList();
    }

    private static ManifestUrl CreateManifestUrl()
    {
        return new ManifestUrl { ManifestDownloadUrl = $"http://{CdnHost}/Builds/test.manifest" };
    }

    /// <summary>
    /// Stands in for the cache.  Fails the first <c>failFirst</c> requests and serves the rest.
    /// </summary>
    private sealed class StubCacheHandler : HttpMessageHandler
    {
        private readonly int _failFirst;
        private int _attemptCount;

        public StubCacheHandler(int failFirst)
        {
            _failFirst = failFirst;
        }

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attemptCount) <= _failFirst)
            {
                throw new HttpRequestException("Connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[4096])
            });
        }
    }
}
