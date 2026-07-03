using System.Security.Cryptography;
using System.Threading;
using EpicPrefill.Api;
using EpicPrefill.Handlers;
using EpicPrefill.Settings;
using Spectre.Console;

namespace EpicPrefill.Test
{
    /// <summary>
    /// Covers the key-file-backed token storage encryption: stable round-trip, key rotation
    /// self-heal, and a legacy-poisoned (undecryptable) store self-heal. Tests run against the
    /// real <see cref="AppConfig.ConfigDir"/> (a static readonly path under the test base dir),
    /// so each test saves/restores the "storage.key" and account file it touches.
    /// </summary>
    public sealed class TokenStorageEncryptionTests : IDisposable
    {
        private readonly string _keyPath = Path.Combine(AppConfig.ConfigDir, "storage.key");
        private readonly string _accountPath = AppConfig.AccountSettingsStorePath;
        private readonly byte[]? _originalKeyBytes;
        private readonly bool _accountFileExisted;
        private readonly string? _originalAccountContent;

        public TokenStorageEncryptionTests()
        {
            // Preserve any pre-existing key/account file state so parallel/other tests in this
            // process (or a developer's local Config dir) aren't clobbered.
            _originalKeyBytes = File.Exists(_keyPath) ? File.ReadAllBytes(_keyPath) : null;
            _accountFileExisted = File.Exists(_accountPath);
            _originalAccountContent = _accountFileExisted ? File.ReadAllText(_accountPath) : null;

            File.Delete(_keyPath);
            File.Delete(_accountPath);
        }

        public void Dispose()
        {
            if (_originalKeyBytes != null)
            {
                File.WriteAllBytes(_keyPath, _originalKeyBytes);
            }
            else
            {
                File.Delete(_keyPath);
            }

            if (_accountFileExisted)
            {
                File.WriteAllText(_accountPath, _originalAccountContent);
            }
            else
            {
                File.Delete(_accountPath);
            }
        }

        [Fact]
        public void RoundTrip_WithStableKeyFile_Succeeds()
        {
            var encrypted = TokenStorageEncryption.Encrypt("hello-world");

            // Key file now exists on disk and is reused (not regenerated) for Decrypt.
            Assert.True(File.Exists(_keyPath));
            var decrypted = TokenStorageEncryption.Decrypt(encrypted);

            Assert.Equal("hello-world", decrypted);
        }

        [Fact]
        public void EncryptDecrypt_WrongLengthKeyFile_RegeneratesValidKey()
        {
            // Simulates a torn/partial write (valid Base64, wrong length) - before the length
            // validation hardening, this would be silently accepted as short/malformed HKDF input key
            // material forever instead of being detected and regenerated.
            var shortKey = new byte[16];
            RandomNumberGenerator.Fill(shortKey);
            File.WriteAllText(_keyPath, Convert.ToBase64String(shortKey));

            var encrypted = TokenStorageEncryption.Encrypt("hello-world");
            var decrypted = TokenStorageEncryption.Decrypt(encrypted);

            Assert.Equal("hello-world", decrypted);
            var regenerated = Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());
            Assert.Equal(32, regenerated.Length);
        }

        [Fact]
        public void LoadFromFile_KeyFileRotated_ReturnsFreshManagerAndDeletesStaleFile()
        {
            var encrypted = TokenStorageEncryption.Encrypt(
                "{\"access_token\":\"abc\",\"refresh_token\":\"def\",\"expires_at\":\"2099-01-01T00:00:00\",\"refresh_expires_at\":\"2099-01-01T00:00:00\"}");
            File.WriteAllText(_accountPath, encrypted);

            // Simulate a new container: fresh random key file replaces the one that encrypted the store above.
            var newKey = new byte[32];
            RandomNumberGenerator.Fill(newKey);
            File.WriteAllText(_keyPath, Convert.ToBase64String(newKey));

            var manager = UserAccountManager.LoadFromFile(AnsiConsole.Console, new TestEpicAuthProvider());

            Assert.Null(manager.OauthToken);
            Assert.False(File.Exists(_accountPath));
        }

        [Fact]
        public void LoadFromFile_LegacyPoisonedBlob_ReturnsFreshManagerAndDeletesStaleFile()
        {
            // "ENC:" + valid base64 of random bytes that will never match the current key's tag.
            var garbage = new byte[64];
            RandomNumberGenerator.Fill(garbage);
            File.WriteAllText(_accountPath, "ENC:" + Convert.ToBase64String(garbage));

            var manager = UserAccountManager.LoadFromFile(AnsiConsole.Console, new TestEpicAuthProvider());

            Assert.Null(manager.OauthToken);
            Assert.False(File.Exists(_accountPath));
        }

        [Fact]
        public void TryReadStoredToken_UndecryptableStore_ReturnsNullAndDeletesStaleFile()
        {
            var garbage = new byte[64];
            RandomNumberGenerator.Fill(garbage);
            File.WriteAllText(_accountPath, "ENC:" + Convert.ToBase64String(garbage));

            var token = UserAccountManager.TryReadStoredToken();

            Assert.Null(token);
            Assert.False(File.Exists(_accountPath));
        }

        /// <summary>
        /// SocketCommandInterface has no test seam (it owns a live SocketServer/EpicPrefillApi and can't
        /// be constructed headlessly), so this covers the underlying guarantee HandleLogoutAsync relies on:
        /// deleting the account file makes TryReadStoredToken() report no stored account - i.e. "logout"
        /// really forgets the account rather than just tearing down the live API instance.
        /// </summary>
        [Fact]
        public void DeletingAccountFile_ThenTryReadStoredToken_ReturnsNull()
        {
            var encrypted = TokenStorageEncryption.Encrypt(
                "{\"access_token\":\"abc\",\"refresh_token\":\"def\",\"expires_at\":\"2099-01-01T00:00:00\",\"refresh_expires_at\":\"2099-01-01T00:00:00\"}");
            File.WriteAllText(_accountPath, encrypted);
            Assert.NotNull(UserAccountManager.TryReadStoredToken());

            // This is exactly what HandleLogoutAsync does on logout: best-effort delete the persisted store.
            File.Delete(_accountPath);

            var token = UserAccountManager.TryReadStoredToken();

            Assert.Null(token);
            Assert.False(File.Exists(_accountPath));
        }

        private sealed class TestEpicAuthProvider : IEpicAuthProvider
        {
            public Task<string> GetAuthorizationCodeAsync(string authUrl, CancellationToken cancellationToken = default) =>
                Task.FromResult(string.Empty);

            public void CancelPendingRequest() { }
        }
    }
}
