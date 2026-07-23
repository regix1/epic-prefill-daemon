using System.Net;
using System.Reflection;
using Spectre.Console;
using EpicPrefill.Api;
using EpicPrefill.Handlers;
using EpicPrefill.Models;
using EpicPrefill.Models.ApiResponses;

namespace EpicPrefill.Test;

public sealed class EpicManifestCancellationTests
{
    [Fact]
    public async Task GetManifestDownloadUrlAsync_CancelsActiveSend()
    {
        using var handler = new CancellationProbeHandler(blockSend: true);
        var factory = new HttpClientFactory(AnsiConsole.Console, CreateAccountManager(), handler);
        var api = new EpicGamesApi(AnsiConsole.Console, factory);
        using var cancellation = new CancellationTokenSource();

        var request = api.GetManifestDownloadUrlAsync(CreateApp(), cancellation.Token);
        var sendToken = await handler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sendToken.CanBeCanceled);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task GetManifestDownloadUrlAsync_CancelsActiveResponseRead()
    {
        using var handler = new CancellationProbeHandler();
        var factory = new HttpClientFactory(AnsiConsole.Console, CreateAccountManager(), handler);
        var api = new EpicGamesApi(AnsiConsole.Console, factory);
        using var cancellation = new CancellationTokenSource();

        var request = api.GetManifestDownloadUrlAsync(CreateApp(), cancellation.Token);
        var sendToken = await handler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var readToken = await handler.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sendToken.CanBeCanceled);
        Assert.True(readToken.CanBeCanceled);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task DownloadManifestAsync_CancelsActiveSend()
    {
        using var handler = new CancellationProbeHandler(blockSend: true);
        var factory = new HttpClientFactory(AnsiConsole.Console, CreateAccountManager(), handler);
        var manifestHandler = new ManifestHandler(AnsiConsole.Console, factory);
        using var cancellation = new CancellationTokenSource();

        var request = manifestHandler.DownloadManifestAsync(
            CreateApp(),
            CreateManifestUrl(),
            cancellation.Token);
        var sendToken = await handler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sendToken.CanBeCanceled);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task DownloadManifestAsync_CancelsActiveResponseRead()
    {
        using var handler = new CancellationProbeHandler();
        var factory = new HttpClientFactory(AnsiConsole.Console, CreateAccountManager(), handler);
        var manifestHandler = new ManifestHandler(AnsiConsole.Console, factory);
        using var cancellation = new CancellationTokenSource();

        var request = manifestHandler.DownloadManifestAsync(
            CreateApp(),
            CreateManifestUrl(),
            cancellation.Token);
        var sendToken = await handler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var readToken = await handler.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sendToken.CanBeCanceled);
        Assert.True(readToken.CanBeCanceled);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    private static UserAccountManager CreateAccountManager()
    {
        var manager = (UserAccountManager)Activator.CreateInstance(
            typeof(UserAccountManager),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { AnsiConsole.Console, new NullEpicAuthProvider() },
            culture: null)!;

        manager.OauthToken = new OauthToken
        {
            AccessToken = "access-token",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            RefreshToken = "refresh-token",
            RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        return manager;
    }

    private static AppInfo CreateApp()
    {
        var id = Guid.NewGuid().ToString("N");
        return new AppInfo
        {
            AppId = id,
            BuildVersion = id,
            CatalogItemId = "catalog-id",
            Namespace = "namespace-id",
            Title = "Test app"
        };
    }

    private static ManifestUrl CreateManifestUrl()
        => new()
        {
            ManifestDownloadUrl = "http://example.test/manifest",
            queryParams = new[]
            {
                new QueryParam { Name = "f_token", Value = "token" }
            }
        };

    private sealed class NullEpicAuthProvider : IEpicAuthProvider
    {
        public Task<string> GetAuthorizationCodeAsync(
            string authUrl,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Authorization should not be requested with a valid token.");

        public void CancelPendingRequest()
        {
        }
    }

    private sealed class CancellationProbeHandler : HttpMessageHandler
    {
        private readonly bool _blockSend;
        private readonly BlockingReadStream _responseStream = new();
        private readonly TaskCompletionSource<CancellationToken> _sendStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CancellationToken> SendStarted => _sendStarted.Task;
        public Task<CancellationToken> ReadStarted => _responseStream.ReadStarted;

        public CancellationProbeHandler(bool blockSend = false)
        {
            _blockSend = blockSend;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _sendStarted.TrySetResult(cancellationToken);

            if (_blockSend)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(_responseStream)
            };
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<CancellationToken> _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CancellationToken> ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
