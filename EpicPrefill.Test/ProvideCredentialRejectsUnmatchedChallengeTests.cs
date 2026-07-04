using System.Reflection;
using EpicPrefill.Api;
using Xunit;

namespace EpicPrefill.Test
{
    /// <summary>
    /// RC4 (session 20260703-221336-2070027597): HandleProvideCredential used to call
    /// SocketAuthProvider.ReceiveCredential (a void method) and then unconditionally return
    /// Success=true "Credential received" - even when ReceiveCredential silently dropped the
    /// credential because no challenge was pending (or it was for a different challenge id). This
    /// masked a manager-side cross-session desync (RC3) as an instant false success. Fixed by
    /// making ReceiveCredential return bool and having HandleProvideCredential surface the drop as
    /// Success=false. SocketCommandInterface has no public seam (owns a live SocketServer, never
    /// bound here), so this drives the private HandleCommandAsync via reflection, same pattern as
    /// LogoutPreLoginGateTests.
    /// </summary>
    public sealed class ProvideCredentialRejectsUnmatchedChallengeTests
    {
        [Fact]
        public async Task ProvideCredential_WithNoPendingChallenge_ReturnsSuccessFalse()
        {
            using var socketInterface = new SocketCommandInterface(
                Path.Combine(Path.GetTempPath(), $"epic-provide-credential-{Guid.NewGuid():N}.sock"));

            var handleCommandAsync = typeof(SocketCommandInterface).GetMethod(
                "HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

            // No login/HandleLoginAsync was ever started, so SocketAuthProvider has no pending
            // challenge at all - this is the "no challenge is pending" drop path.
            var request = new CommandRequest
            {
                Id = "1",
                Type = "provide-credential",
                Parameters = new Dictionary<string, string>
                {
                    ["challengeId"] = Guid.NewGuid().ToString("N"),
                    ["clientPublicKey"] = "dGVzdC1wdWJsaWMta2V5", // arbitrary base64-looking placeholder
                    ["encryptedCredential"] = "dGVzdC1jaXBoZXJ0ZXh0",
                    ["nonce"] = "dGVzdC1ub25jZQ==",
                    ["tag"] = "dGVzdC10YWc="
                }
            };

            var response = await (Task<CommandResponse>)handleCommandAsync.Invoke(
                socketInterface, new object[] { request, CancellationToken.None })!;

            // Before the fix: Success=true, Message="Credential received" - the drop was invisible.
            Assert.False(response.Success);
        }
    }
}
