using System.Net;
using System.Text.Json;
using EpicPrefill.Api;
using EpicPrefill.Handlers;
using EpicPrefill.Models.ApiResponses;

namespace EpicPrefill.Test;

public sealed class AccountRefreshTests
{
    [Fact]
    public async Task ClearingTheAccountPreventsAnInFlightRefreshFromRepublishingItsToken()
    {
        using var handler = new TokenHandler();
        var saved = 0;
        var account = new UserAccountManager(ConcurrentPrefillTests.CreateConsole(), new NoLogin(), handler,
            _ => Interlocked.Increment(ref saved))
        {
            OauthToken = new OauthToken
            {
                AccessToken = "expired",
                ExpiresAt = DateTime.UtcNow.AddHours(-1),
                RefreshToken = "fixture",
                RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(1)
            }
        };
        var refresh = account.LoginAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        account.OauthToken = null;
        handler.Release.TrySetResult();
        await Assert.ThrowsAsync<EpicPrefill.Models.Exceptions.EpicLoginException>(() => refresh);
        Assert.Null(account.OauthToken);
        Assert.Equal(0, saved);
    }

    [Fact]
    public async Task ThreeClientsShareOneTokenRefreshAndCaptureItsResult()
    {
        using var handler = new TokenHandler();
        var saved = 0;
        var account = new UserAccountManager(ConcurrentPrefillTests.CreateConsole(), new NoLogin(), handler,
            _ => Interlocked.Increment(ref saved))
        {
            OauthToken = new OauthToken
            {
                AccessToken = "expired",
                ExpiresAt = DateTime.UtcNow.AddHours(-1),
                RefreshToken = "fixture",
                RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(1)
            }
        };
        var clients = new HttpClientFactory(ConcurrentPrefillTests.CreateConsole(), account, handler);
        var requests = Enumerable.Range(0, 3).Select(_ => clients.GetHttpClientAsync()).ToArray();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
        handler.Release.TrySetResult();
        var completed = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(1, handler.Calls);
            Assert.Equal(1, saved);
            Assert.All(completed, client => Assert.Equal("fresh", client.DefaultRequestHeaders.Authorization!.Parameter));
            account.OauthToken = new OauthToken { AccessToken = "replacement" };
            Assert.All(completed, client => Assert.Equal("fresh", client.DefaultRequestHeaders.Authorization!.Parameter));
        }
        finally { foreach (var client in completed) { client.Dispose(); } }
    }

    private sealed class NoLogin : IEpicAuthProvider
    {
        public Task<string> GetAuthorizationCodeAsync(string authUrl, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Interactive login must not run.");
        public void CancelPendingRequest() { }
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var token = new OauthToken
            {
                AccessToken = "fresh",
                ExpiresAt = DateTime.UtcNow.AddHours(2),
                RefreshToken = "refreshed",
                RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(1)
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(token))
            };
        }
    }
}
