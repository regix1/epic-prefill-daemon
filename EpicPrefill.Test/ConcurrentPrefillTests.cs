using System.Net;
using EpicPrefill.Api;
using EpicPrefill.Handlers;
using EpicPrefill.Models;
using EpicPrefill.Models.ApiResponses;
using LancachePrefill.Common;
using Spectre.Console;

namespace EpicPrefill.Test;

public sealed class ConcurrentPrefillTests
{
    [Fact]
    public async Task ShortBodiesFailAndCountActualBytesAcrossRetries()
    {
        using var handler = new ShortBodyHandler();
        using var downloader = new DownloadHandler(CreateConsole(), handler, "127.0.0.1");
        var success = await downloader.DownloadQueuedChunksAsync(
            new List<QueuedRequest> { new() { DownloadUrl = "/chunk", DownloadSizeBytes = 9 } },
            new ManifestUrl { ManifestDownloadUrl = "http://content.test/manifest" });
        Assert.False(success);
        Assert.Equal(16, downloader.BytesTransferred);
    }

    [Fact]
    public async Task ExclusiveRegistrationPreventsTheSecondBodyFromStarting()
    {
        using var handler = new BodyHandler();
        await using var owner = new OwnedOperationCoordinator();
        using var downloader = new DownloadHandler(CreateConsole(), handler, "127.0.0.1");
        await owner.StartAsync(async token =>
        {
            await downloader.DownloadQueuedChunksAsync(
                new List<QueuedRequest> { new() { DownloadUrl = "/chunk", DownloadSizeBytes = 8 } },
                new ManifestUrl { ManifestDownloadUrl = "http://content.test/manifest" }, cancellationToken: token);
        });
        try
        {
            await handler.Body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync(_ =>
                throw new InvalidOperationException("Second body cannot be dispatched by an exclusive owner.")));
        }
        finally
        {
            handler.Body.Release.TrySetResult();
            await owner.WaitAsync();
        }
    }

    [Fact]
    public async Task ThreeRunsReadBodiesTogetherAndTargetedCancelLeavesSiblingsRunning()
    {
        var protocol = new PrefillProtocol(3, maxRuns: "3");
        using var budget = new RequestBudget(3);
        var claims = new ItemClaims();
        await using var owner = new OwnedOperationCoordinator(protocol.MaxConcurrentRuns);
        var handlers = Enumerable.Range(0, 3).Select(_ => new BodyHandler()).ToArray();
        var runs = Enumerable.Range(0, 3).Select(i => new PrefillRun("run-" + i, protocol,
            new RunOptions { AppIds = new[] { "APP-" + i }, Force = i == 1, MaxConcurrency = 2 },
            budget, claims, NullProgress.Instance)).ToArray();
        try
        {
            for (var i = 0; i < runs.Length; i++)
            {
                var run = runs[i];
                var handler = handlers[i];
                var app = run.Options.AppIds![0];
                var admission = await owner.StartAsync(run.Progress.Snapshot.OperationId,
                    PrefillProtocol.Fingerprint(run.Options), run.Progress,
                    token => run.ExecuteAsync(async runToken =>
                    {
                        Assert.NotNull(run.TryClaim(app));
                        run.OnAppStarted(new AppDownloadInfo { AppId = app, Name = app, TotalBytes = 8 });
                        using var downloader = new DownloadHandler(CreateConsole(), handler, "127.0.0.1", run);
                        Assert.True(await downloader.DownloadQueuedChunksAsync(
                            new List<QueuedRequest> { new() { DownloadUrl = "/chunk", DownloadSizeBytes = 8 } },
                            new ManifestUrl { ManifestDownloadUrl = "http://content.test/manifest" }, app, app, runToken));
                        run.OnAppCompleted(new AppDownloadInfo { AppId = app, Name = app, TotalBytes = 8 }, AppDownloadResult.Success);
                    }, token));
                Assert.True(admission.Accepted);
            }

            await Task.WhenAll(handlers.Select(handler => handler.Body.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, owner.GetActiveOperations().Count);
            var fourth = new PrefillRun("run-3", protocol,
                new RunOptions { AppIds = new[] { "APP-3" }, MaxConcurrency = 1 }, budget, claims, NullProgress.Instance);
            var rejected = await owner.StartAsync("run-3", PrefillProtocol.Fingerprint(fourth.Options), fourth.Progress,
                _ => throw new InvalidOperationException("Rejected work must not execute."));
            Assert.False(rejected.Accepted);
            Assert.Equal("run-limit", rejected.Error);
            Assert.Equal("cancelling", owner.Cancel("run-0", protocol.DaemonInstanceId)!.State);
            await owner.WaitAsync("run-0").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("cancelled", owner.GetOperation("run-0")!.State);
            Assert.Equal(3, owner.GetOperation("run-0")!.BytesTransferred);
            Assert.Equal(2, owner.GetActiveOperations().Count);
            Assert.False(handlers[1].Body.Disposed);
            Assert.False(handlers[2].Body.Disposed);
            handlers[1].Body.Release.TrySetResult();
            handlers[2].Body.Release.TrySetResult();
            await Task.WhenAll(owner.WaitAsync("run-1"), owner.WaitAsync("run-2")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, owner.GetOperation("run-1")!.BytesTransferred);
            Assert.Equal(8, owner.GetOperation("run-2")!.BytesTransferred);
            Assert.Equal("completed", owner.GetOperation("run-2")!.State);
            Assert.All(handlers, handler => Assert.True(handler.Body.Disposed));
        }
        finally
        {
            foreach (var handler in handlers) { handler.Body.Release.TrySetResult(); handler.Dispose(); }
        }
    }

    internal static IAnsiConsole CreateConsole() => AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.No,
        ColorSystem = ColorSystemSupport.NoColors,
        Interactive = InteractionSupport.No,
        Out = new AnsiConsoleOutput(TextWriter.Synchronized(TextWriter.Null))
    });

    private sealed class BodyHandler : HttpMessageHandler
    {
        public BodyStream Body { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body) });
    }

    private sealed class ShortBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8]) });
    }

    private sealed class BodyStream : Stream
    {
        private int _reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 8;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref _reads);
            if (read == 1) { buffer.Span[..3].Clear(); return 3; }
            if (read == 2)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                buffer.Span[..5].Clear();
                return 5;
            }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
