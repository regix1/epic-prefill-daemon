using System.Reflection;
using EpicPrefill.Api;
using EpicPrefill.Handlers;
using EpicPrefill.Settings;
using Xunit;

namespace EpicPrefill.Test
{
    /// <summary>
    /// RC6 (session 20260703-221336-2070027597): HandleLogoutAsync deleted the account file but
    /// left storage.key on disk, so any later login could re-persist a token the SAME key could
    /// still decrypt - making the erase-on-stop / "clear stored logins" policy incomplete. Logout
    /// must now delete both files. Touches the shared AppConfig.ConfigDir/AccountSettingsStorePath
    /// paths, so this runs in the same non-parallel collection as TokenStorageEncryptionTests /
    /// LogoutClearsOAuthTokenTests to avoid a file-in-use race.
    /// </summary>
    [Collection("EpicAccountFile")]
    public sealed class LogoutErasesStorageKeyTests : IDisposable
    {
        private readonly string _keyPath = Path.Combine(AppConfig.ConfigDir, "storage.key");
        private readonly string _accountPath = AppConfig.AccountSettingsStorePath;
        private readonly byte[]? _originalKeyBytes;
        private readonly bool _accountFileExisted;
        private readonly string? _originalAccountContent;

        public LogoutErasesStorageKeyTests()
        {
            _originalKeyBytes = File.Exists(_keyPath) ? File.ReadAllBytes(_keyPath) : null;
            _accountFileExisted = File.Exists(_accountPath);
            _originalAccountContent = _accountFileExisted ? File.ReadAllText(_accountPath) : null;
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

            if (_accountFileExisted && _originalAccountContent != null)
            {
                File.WriteAllText(_accountPath, _originalAccountContent);
            }
            else
            {
                File.Delete(_accountPath);
            }
        }

        [Fact]
        public async Task Logout_DeletesBothAccountFileAndStorageKey()
        {
            // Force-create storage.key (encrypting anything lazily creates it) and a plaintext
            // account file (migration-path content is fine - only its existence is asserted).
            TokenStorageEncryption.Encrypt("seed");
            File.WriteAllText(_accountPath, "{}");

            Assert.True(File.Exists(_keyPath), "Precondition: storage.key must exist before logout.");
            Assert.True(File.Exists(_accountPath), "Precondition: account file must exist before logout.");

            using var socketInterface = new SocketCommandInterface(
                Path.Combine(Path.GetTempPath(), $"epic-logout-storage-key-{Guid.NewGuid():N}.sock"));

            var handleCommandAsync = typeof(SocketCommandInterface).GetMethod(
                "HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

            var request = new CommandRequest { Id = "1", Type = "logout" };
            var response = await (Task<CommandResponse>)handleCommandAsync.Invoke(
                socketInterface, new object[] { request, CancellationToken.None })!;

            Assert.True(response.Success);
            // Before the fix: only _accountPath was deleted; storage.key survived.
            Assert.False(File.Exists(_accountPath), "Account file should be deleted after logout.");
            Assert.False(File.Exists(_keyPath), "storage.key should be deleted after logout.");
        }
    }
}
