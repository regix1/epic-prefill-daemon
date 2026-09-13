#nullable enable

namespace EpicPrefill.Api;

/// <summary>
/// Runs EpicPrefill in daemon mode using Unix Domain Socket or TCP for IPC.
/// </summary>
public static class DaemonMode
{
    public static async Task RunAsync(
        string socketPath = "/responses/daemon.sock",
        CancellationToken cancellationToken = default)
    {
        AnsiConsole.WriteLine($"Starting EpicPrefill daemon on Unix socket {socketPath}");

        using var socketInterface = new SocketCommandInterface(socketPath);

        await socketInterface.StartAsync(cancellationToken);

        using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var lifetimeTimer = StartMaxLifetimeTimer(lifetimeCts);

        try
        {
            await Task.Delay(Timeout.Infinite, lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        AnsiConsole.WriteLine("Daemon stopped.");
    }

    /// <summary>
    /// Reads <c>PREFILL_MAX_LIFETIME_SECONDS</c>. When &gt; 0, returns a timer that, on elapse, logs and cancels the
    /// supplied token source so the long-lived daemon loop exits cleanly (process returns 0 / the container stops).
    /// Returns null (no-op) when the variable is unset, not an integer, or &lt;= 0.
    /// </summary>
    private static Timer? StartMaxLifetimeTimer(CancellationTokenSource lifetimeCts)
    {
        var raw = Environment.GetEnvironmentVariable("PREFILL_MAX_LIFETIME_SECONDS");
        if (!int.TryParse(raw, out var seconds) || seconds <= 0)
        {
            return null;
        }

        AnsiConsole.WriteLine($"PREFILL_MAX_LIFETIME_SECONDS={seconds}: daemon will self-shut down after {seconds}s.");

        return new Timer(_ =>
        {
            AnsiConsole.WriteLine($"Max lifetime of {seconds}s reached. Initiating clean shutdown...");
            try { lifetimeCts.Cancel(); }
            catch (ObjectDisposedException) { /* shutting down already */ }
        }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    public static async Task RunTcpAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        AnsiConsole.WriteLine($"Starting EpicPrefill daemon on TCP port {port}");

        using var socketInterface = new SocketCommandInterface(port);

        await socketInterface.StartAsync(cancellationToken);

        using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var lifetimeTimer = StartMaxLifetimeTimer(lifetimeCts);

        try
        {
            await Task.Delay(Timeout.Infinite, lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        AnsiConsole.WriteLine("Daemon stopped.");
    }
}
