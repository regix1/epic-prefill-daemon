#nullable enable

using System.Runtime.InteropServices;

namespace EpicPrefill.Api;

/// <summary>
/// Authentication provider that uses socket for Epic OAuth2 credential exchange.
/// Sends the OAuth URL via credential-challenge and waits for the encrypted auth code.
/// </summary>
public sealed class SocketAuthProvider : IEpicAuthProvider, IDisposable
{
    private readonly SocketServer _socketServer;
    private readonly IPrefillProgress _progress;
    private readonly SemaphoreSlim _credentialLock = new(1, 1);
    private TaskCompletionSource<EncryptedCredentialResponse>? _pendingCredential;
    private string? _currentChallengeId;
    private bool _disposed;

    private GCHandle _credentialHandle;
    private char[]? _pinnedCredential;

    public SocketAuthProvider(SocketServer socketServer, IPrefillProgress? progress = null)
    {
        _socketServer = socketServer;
        _progress = progress ?? NullProgress.Instance;
    }

    /// <summary>
    /// Called by the SocketCommandInterface when a provide-credential command is received.
    /// </summary>
    public void ReceiveCredential(EncryptedCredentialResponse response)
    {
        if (_pendingCredential == null || _currentChallengeId == null)
        {
            _progress.OnLog(LogLevel.Warning, "Received credential but no challenge is pending");
            return;
        }

        if (response.ChallengeId != _currentChallengeId)
        {
            _progress.OnLog(LogLevel.Warning, $"Received credential for wrong challenge. Expected: {_currentChallengeId}, Got: {response.ChallengeId}");
            return;
        }

        _progress.OnLog(LogLevel.Debug, "Credential received via socket");
        _pendingCredential.TrySetResult(response);
    }

    /// <summary>
    /// Sends the Epic OAuth URL via socket credential-challenge and waits for the encrypted auth code.
    /// </summary>
    public async Task<string> GetAuthorizationCodeAsync(string authUrl, CancellationToken cancellationToken = default)
    {
        await _credentialLock.WaitAsync(cancellationToken);
        try
        {
            _pendingCredential = new TaskCompletionSource<EncryptedCredentialResponse>();

            // Create secure challenge with "authorization-url" type containing the OAuth URL
            var challenge = SecureCredentialExchange.CreateChallenge("authorization-url", authUrl);
            _currentChallengeId = challenge.ChallengeId;

            _progress.OnLog(LogLevel.Info, $"Sending credential challenge via socket: authorization-url (id: {challenge.ChallengeId})");

            // Send challenge event to all connected clients
            var challengeEvent = new CredentialChallengeEvent(challenge);
            await _socketServer.BroadcastCredentialChallengeAsync(challengeEvent, cancellationToken);

            // Wait for credential with timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() => _pendingCredential.TrySetCanceled());

            try
            {
                var encryptedResponse = await _pendingCredential.Task;

                var credential = SecureCredentialExchange.DecryptCredential(encryptedResponse);
                if (credential == null)
                {
                    throw new InvalidOperationException("Failed to decrypt credential - invalid or expired challenge");
                }

                _progress.OnLog(LogLevel.Debug, "Authorization code decrypted successfully");

                StoreSecurely(credential);

                return credential;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException("Timeout waiting for authorization code");
            }
        }
        finally
        {
            _currentChallengeId = null;
            _pendingCredential = null;
            _credentialLock.Release();
        }
    }

    private void StoreSecurely(string credential)
    {
        ClearPinnedCredential();

        _pinnedCredential = credential.ToCharArray();
        _credentialHandle = GCHandle.Alloc(_pinnedCredential, GCHandleType.Pinned);
    }

    private void ClearPinnedCredential()
    {
        if (_pinnedCredential != null)
        {
            Array.Clear(_pinnedCredential, 0, _pinnedCredential.Length);

            if (_credentialHandle.IsAllocated)
            {
                _credentialHandle.Free();
            }

            _pinnedCredential = null;
        }
    }

    /// <summary>
    /// Cancels any pending credential request.
    /// </summary>
    public void CancelPendingRequest()
    {
        _progress.OnLog(LogLevel.Info, "Cancelling pending credential request...");

        _pendingCredential?.TrySetCanceled();
        _pendingCredential = null;
        _currentChallengeId = null;

        ClearPinnedCredential();

        _progress.OnLog(LogLevel.Info, "Pending credential request cancelled");
    }

    public string? CurrentChallengeId => _currentChallengeId;

    public void Dispose()
    {
        if (_disposed) return;

        ClearPinnedCredential();
        _credentialLock.Dispose();
        _pendingCredential?.TrySetCanceled();
        _disposed = true;
    }
}
