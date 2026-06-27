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
        Console.WriteLine("Starting EpicPrefill daemon...");
        Console.WriteLine($"Socket path: {socketPath}");
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ UNIX SOCKET IPC                                              │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ • Reliable bidirectional communication                       │");
        Console.WriteLine("│ • Low latency (<1ms)                                         │");
        Console.WriteLine("│ • Works in both host and bridge Docker network modes         │");
        Console.WriteLine("│ • Real-time progress streaming                               │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ SECURITY                                                     │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ • Login is REQUIRED before any other commands                │");
        Console.WriteLine("│ • All credentials are encrypted using ECDH + AES-GCM         │");
        Console.WriteLine("│ • Challenges expire after 5 minutes                          │");
        Console.WriteLine("└──────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

        using var socketInterface = new SocketCommandInterface(socketPath);

        await socketInterface.StartAsync(cancellationToken);

        Console.WriteLine("Daemon started. Waiting for connections...");

        using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var lifetimeTimer = StartMaxLifetimeTimer(lifetimeCts);

        try
        {
            await Task.Delay(Timeout.Infinite, lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        Console.WriteLine("Daemon stopped.");
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

        Console.WriteLine($"PREFILL_MAX_LIFETIME_SECONDS={seconds}: daemon will self-shut down after {seconds}s.");

        return new Timer(_ =>
        {
            Console.WriteLine($"Max lifetime of {seconds}s reached. Initiating clean shutdown...");
            try { lifetimeCts.Cancel(); }
            catch (ObjectDisposedException) { /* shutting down already */ }
        }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    public static async Task RunTcpAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Starting EpicPrefill daemon (TCP mode)...");
        Console.WriteLine($"TCP port: {port}");
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ TCP IPC                                                     │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ • Reliable bidirectional communication                       │");
        Console.WriteLine("│ • Useful for Windows Docker Desktop bind mounts              │");
        Console.WriteLine("│ • Real-time progress streaming                               │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ SECURITY                                                     │");
        Console.WriteLine("├──────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│ • Login is REQUIRED before any other commands                │");
        Console.WriteLine("│ • All credentials are encrypted using ECDH + AES-GCM         │");
        Console.WriteLine("│ • Challenges expire after 5 minutes                          │");
        Console.WriteLine("└──────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

        using var socketInterface = new SocketCommandInterface(port);

        await socketInterface.StartAsync(cancellationToken);

        Console.WriteLine("Daemon started. Waiting for connections...");

        using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var lifetimeTimer = StartMaxLifetimeTimer(lifetimeCts);

        try
        {
            await Task.Delay(Timeout.Infinite, lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        Console.WriteLine("Daemon stopped.");
    }
}

public class PrefillProgressUpdate
{
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppId")]
    public string? CurrentAppId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppName")]
    public string? CurrentAppName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesPerSecond")]
    public double BytesPerSecond { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsed")]
    public TimeSpan Elapsed { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds => Elapsed.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public string? Result { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalApps")]
    public int TotalApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("updatedApps")]
    public int UpdatedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("alreadyUpToDate")]
    public int AlreadyUpToDate { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("failedApps")]
    public int FailedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytesTransferred")]
    public long TotalBytesTransferred { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTimeSeconds")]
    public double TotalTimeSeconds => TotalTime.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
