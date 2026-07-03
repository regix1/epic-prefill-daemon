using System.Text.Json;
using EpicPrefill.Api;
using EpicPrefill.Models.ApiResponses;
using EpicPrefill.Settings;
using Xunit;

namespace EpicPrefill.Test
{
    /// <summary>
    /// SocketCommandInterface itself has no test seam (it owns a live SocketServer and only reaches
    /// _isLoggedIn=true via a real Epic OAuth exchange), so this covers the underlying invariant its
    /// complete-forget contract depends on: <see cref="EpicPrefillApi.Shutdown"/> must drop the
    /// in-memory OAuth token unconditionally - not gated on IsInitialized - so a logout racing a
    /// mid-login task can't leave a live token behind (diagnostic: EpicPrefillApi.cs Shutdown,
    /// EpicGamesManager.ClearOAuthToken). Pre-seeds an unexpired token on disk so InitializeAsync
    /// takes the "reuse existing session" path and never makes a real network call.
    /// </summary>
    [Collection("EpicAccountFile")]
    public sealed class LogoutClearsOAuthTokenTests : IDisposable
    {
        private readonly string _accountPath = AppConfig.AccountSettingsStorePath;
        private readonly bool _accountFileExisted;
        private readonly string? _originalAccountContent;

        public LogoutClearsOAuthTokenTests()
        {
            _accountFileExisted = File.Exists(_accountPath);
            _originalAccountContent = _accountFileExisted ? File.ReadAllText(_accountPath) : null;
        }

        public void Dispose()
        {
            if (_accountFileExisted && _originalAccountContent != null)
            {
                File.WriteAllText(_accountPath, _originalAccountContent);
            }
            else
            {
                File.Delete(_accountPath);
            }
        }

        private sealed class NeverCalledAuthProvider : IEpicAuthProvider
        {
            public Task<string> GetAuthorizationCodeAsync(string authUrl, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Should not be called: token on disk is not expired.");

            public void CancelPendingRequest() { }
        }

        [Fact]
        public async Task Shutdown_AfterSuccessfulInitialize_ClearsOAuthToken_AndDisplayNameGoesNull()
        {
            var token = new OauthToken
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token",
                ExpiresAt = DateTime.UtcNow.AddHours(4),
                RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(30),
                DisplayName = "test-display-name",
                AccountId = "test-account-id"
            };
            // Plaintext (unencrypted) - UserAccountManager.LoadFromFile treats this as the legacy
            // migration path: loads it directly, no auth provider call needed either way.
            File.WriteAllText(_accountPath, JsonSerializer.Serialize(token, SerializationContext.Default.OauthToken));

            var api = new EpicPrefillApi(new NeverCalledAuthProvider());
            await api.InitializeAsync();

            Assert.True(api.IsInitialized);
            Assert.Equal("test-display-name", api.DisplayName);

            api.Shutdown();

            Assert.False(api.IsInitialized);
            Assert.Null(api.DisplayName);
        }
    }
}
