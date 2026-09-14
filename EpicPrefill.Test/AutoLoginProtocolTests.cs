using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EpicPrefill.Api;
using Xunit;

namespace EpicPrefill.Test;

[CollectionDefinition("Secure credential exchange", DisableParallelization = true)]
public sealed class SecureCredentialExchangeCollection
{
}

[Collection("Secure credential exchange")]
public sealed class AutoLoginProtocolTests
{
    [Fact]
    public async Task SavedLogin_BypassesStaleInteractiveCredentialWaitAsync()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var commands = new SocketCommandInterface(0, (refreshToken, _) =>
        {
            received.TrySetResult(refreshToken);
            return Task.CompletedTask;
        });
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var auth = (SocketAuthProvider)typeof(SocketCommandInterface)
            .GetField("_authProvider", flags)!
            .GetValue(commands)!;

        using var staleCts = new CancellationTokenSource();
        var staleWait = auth.GetAuthorizationCodeAsync("https://example.invalid/login", staleCts.Token);
        using var staleReady = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (auth.CurrentChallengeId is null)
        {
            await Task.Delay(10, staleReady.Token);
        }

        try
        {
            var challengeResponse = await SendAsync(commands, new CommandRequest
            {
                Id = "challenge",
                Type = "get-auto-login-challenge"
            });
            Assert.True(challengeResponse.Success);
            var challenge = Assert.IsType<CredentialChallenge>(challengeResponse.Data);
            var serializedLogin = JsonSerializer.Serialize(
                new RefreshTokenLogin { RefreshToken = "saved-refresh-token" },
                DaemonSerializationContext.Default.RefreshTokenLogin);
            var encrypted = Encrypt(challenge, serializedLogin);

            var loginResponse = await SendAsync(commands, new CommandRequest
            {
                Id = "login",
                Type = "provide-auto-login",
                Parameters = new Dictionary<string, string>
                {
                    ["challengeId"] = encrypted.ChallengeId,
                    ["clientPublicKey"] = encrypted.ClientPublicKey,
                    ["encryptedCredential"] = encrypted.EncryptedCredential,
                    ["nonce"] = encrypted.Nonce,
                    ["tag"] = encrypted.Tag
                }
            });

            Assert.True(loginResponse.Success, loginResponse.Error);
            Assert.Equal("saved-refresh-token", await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));

            using var loggedIn = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            CommandResponse statusResponse;
            do
            {
                statusResponse = await SendAsync(commands, new CommandRequest { Id = "status", Type = "status" });
                if (Assert.IsType<StatusData>(statusResponse.Data).IsLoggedIn)
                {
                    break;
                }
                await Task.Delay(10, loggedIn.Token);
            }
            while (true);

            Assert.True(Assert.IsType<StatusData>(statusResponse.Data).IsLoggedIn);
        }
        finally
        {
            auth.CancelPendingRequest();
            await staleCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await staleWait);
        }
    }

    private static Task<CommandResponse> SendAsync(SocketCommandInterface commands, CommandRequest request)
        => (Task<CommandResponse>)typeof(SocketCommandInterface)
            .GetMethod("HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(commands, new object[] { request, CancellationToken.None })!;

    private static EncryptedCredentialResponse Encrypt(CredentialChallenge challenge, string cleartext)
    {
        var serverPublicKey = Convert.FromBase64String(challenge.ServerPublicKey);
        using var client = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var server = ECDiffieHellman.Create();
        server.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = serverPublicKey.AsSpan(1, 32).ToArray(),
                Y = serverPublicKey.AsSpan(33, 32).ToArray()
            }
        });

        var sharedSecret = client.DeriveKeyMaterial(server.PublicKey);
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            32,
            Encoding.UTF8.GetBytes(challenge.ChallengeId),
            Encoding.UTF8.GetBytes("EpicPrefill-Credential-Encryption"));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cleartextBytes = Encoding.UTF8.GetBytes(cleartext);
        var ciphertext = new byte[cleartextBytes.Length];
        var tag = new byte[16];

        using (var cipher = new AesGcm(key, tag.Length))
        {
            cipher.Encrypt(nonce, cleartextBytes, ciphertext, tag);
        }

        var clientParameters = client.ExportParameters(false);
        byte[] clientPublicKey = [4, .. clientParameters.Q.X!, .. clientParameters.Q.Y!];
        CryptographicOperations.ZeroMemory(sharedSecret);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(cleartextBytes);

        return new EncryptedCredentialResponse
        {
            ChallengeId = challenge.ChallengeId,
            ClientPublicKey = Convert.ToBase64String(clientPublicKey),
            EncryptedCredential = Convert.ToBase64String(ciphertext),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag)
        };
    }
}
