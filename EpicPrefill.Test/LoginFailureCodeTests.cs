using System.Text.Json;
using EpicPrefill.Api;
using Xunit;

namespace EpicPrefill.Test;

public sealed class LoginFailureCodeTests
{
    [Fact]
    public void FailedLoginPush_CarriesLoginFailedCode()
    {
        var json = JsonSerializer.Serialize(
            new AuthStateEvent("awaiting-login", "Login failed: x", errorCode: "login-failed"),
            DaemonSerializationContext.Default.SocketEventAuthStateData);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("login-failed", document.RootElement.GetProperty("data").GetProperty("errorCode").GetString());
    }

    [Fact]
    public void LoggedInPush_HasNoErrorCode()
    {
        var json = JsonSerializer.Serialize(
            new AuthStateEvent("logged-in", "Authenticated and ready for commands", "Player"),
            DaemonSerializationContext.Default.SocketEventAuthStateData);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("data").TryGetProperty("errorCode", out _));
    }
}
