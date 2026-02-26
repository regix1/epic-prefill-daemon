#nullable enable

namespace EpicPrefill.Api;

/// <summary>
/// Interface for providing Epic Games authentication credentials.
/// Epic uses OAuth2 with authorization codes instead of username/password.
/// </summary>
public interface IEpicAuthProvider
{
    /// <summary>
    /// Gets an authorization code for Epic login.
    /// The implementation should send the authUrl to the user and wait for the auth code.
    /// </summary>
    /// <param name="authUrl">The Epic OAuth URL the user needs to visit</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The authorization code from Epic's OAuth redirect</returns>
    Task<string> GetAuthorizationCodeAsync(string authUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels any pending credential request.
    /// </summary>
    void CancelPendingRequest();
}
