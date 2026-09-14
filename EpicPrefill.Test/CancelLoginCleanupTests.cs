using System.Reflection;
using EpicPrefill.Api;
using Xunit;

namespace EpicPrefill.Test;

public sealed class CancelLoginCleanupTests
{
    [Fact]
    public async Task CancelLogin_WithHiddenCredentialWait_ReleasesTheNextLoginAsync()
    {
        using var commands = new SocketCommandInterface(
            Path.Combine(Path.GetTempPath(), $"epic-cancel-login-{Guid.NewGuid():N}.sock"));
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var auth = (SocketAuthProvider)typeof(SocketCommandInterface)
            .GetField("_authProvider", flags)!
            .GetValue(commands)!;
        var loginTaskField = typeof(SocketCommandInterface).GetField("_loginTask", flags)!;
        var loginCtsField = typeof(SocketCommandInterface).GetField("_loginCts", flags)!;
        var loggingInField = typeof(SocketCommandInterface).GetField("_isLoggingIn", flags)!;
        var handle = typeof(SocketCommandInterface).GetMethod("HandleCommandAsync", flags)!;

        using var firstCts = new CancellationTokenSource();
        var first = auth.GetRefreshTokenAsync(firstCts.Token);
        using var firstReady = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (auth.CurrentChallengeId == null)
        {
            await Task.Delay(10, firstReady.Token);
        }

        loginTaskField.SetValue(commands, first);
        loginCtsField.SetValue(commands, firstCts);
        loggingInField.SetValue(commands, false);

        var response = await (Task<CommandResponse>)handle.Invoke(
            commands,
            new object[]
            {
                new CommandRequest { Id = "cancel", Type = "cancel-login" },
                CancellationToken.None
            })!;

        Assert.True(response.Success);
        Assert.Same(first, await Task.WhenAny(first, Task.Delay(TimeSpan.FromMilliseconds(500))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        Assert.Null(auth.CurrentChallengeId);

        using var nextCts = new CancellationTokenSource();
        var next = auth.GetRefreshTokenAsync(nextCts.Token);
        using var nextReady = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (auth.CurrentChallengeId == null)
        {
            await Task.Delay(10, nextReady.Token);
        }

        auth.CancelPendingRequest();
        await nextCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
    }
}
