using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LancachePrefill.Common;
using EpicPrefill.Api;
using EpicPrefill.Settings;

namespace EpicPrefill.Test;

[Collection("ProcessEnvironment")]
public sealed class DaemonAdapterTests
{
    private const string SocketSecretVariable = "PREFILL_SOCKET_SECRET";

    [Fact]
    public async Task SocketReconnectRecoversAcceptedRunsAndCancelsOnlyTheRequestedOperation()
    {
        var originalSecret = Environment.GetEnvironmentVariable(SocketSecretVariable);
        Environment.SetEnvironmentVariable(SocketSecretVariable, null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var port = GetUnusedTcpPort();
        using var commands = new SocketCommandInterface(port, async (_, run, token) =>
        {
            if (Interlocked.Increment(ref calls) == 3) { entered.TrySetResult(); }
            await release.Task.WaitAsync(token);
            run!.OnAppCompleted(new AppDownloadInfo { AppId = run.Options.AppIds![0] }, AppDownloadResult.Success);
            return new PrefillResult { Success = true };
        });
        try
        {
            await commands.StartAsync();
            using var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            var stream = first.GetStream();
            await WriteCommandAsync(stream, new CommandRequest { Id = "status", Type = "status" });
            using var status = await ReadJsonFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
            var instance = status.RootElement.GetProperty("data").GetProperty("daemonInstanceId").GetString()!;
            foreach (var id in new[] { "one", "two", "three" })
            {
                await WriteCommandAsync(stream, new CommandRequest
                {
                    Id = id,
                    Type = "prefill",
                    Parameters = new()
                    {
                        ["protocolVersion"] = "2",
                        ["daemonInstanceId"] = instance,
                        ["appIds"] = JsonSerializer.Serialize(new[] { id })
                    }
                });
                using var ack = await ReadJsonFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(id, ack.RootElement.GetProperty("data").GetProperty("runId").GetString());
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.Dispose();
            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            stream = second.GetStream();
            await WriteCommandAsync(stream, new CommandRequest { Id = "reconnected", Type = "status" });
            using var recovered = await ReadJsonFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, recovered.RootElement.GetProperty("data").GetProperty("activeOperations").GetArrayLength());
            await WriteCommandAsync(stream, new CommandRequest
            {
                Id = "cancel",
                Type = "cancel-prefill",
                Parameters = new() { ["operationId"] = "one", ["daemonInstanceId"] = instance }
            });
            var receivedAck = false;
            var receivedTerminal = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!receivedAck || !receivedTerminal)
            {
                using var frame = await ReadJsonFrameAsync(stream).WaitAsync(timeout.Token);
                var root = frame.RootElement;
                if (root.TryGetProperty("id", out var id))
                {
                    Assert.Equal("cancel", id.GetString());
                    Assert.True(root.GetProperty("success").GetBoolean());
                    receivedAck = true;
                }
                else
                {
                    var progress = root.GetProperty("data");
                    Assert.Equal("one", progress.GetProperty("operationId").GetString());
                    receivedTerminal |= progress.GetProperty("state").GetString() == "cancelled";
                }
            }
            Assert.Equal(3, calls);
        }
        finally
        {
            release.TrySetResult();
            await commands.StopAsync();
            Environment.SetEnvironmentVariable(SocketSecretVariable, originalSecret);
        }
    }

    [Fact]
    public async Task SocketServer_BlockedSerializedCommand_DoesNotBlockControlResponse()
    {
        var originalSecret = Environment.GetEnvironmentVariable(SocketSecretVariable);
        Environment.SetEnvironmentVariable(SocketSecretVariable, null);
        var releaseSlowCommand = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCommandStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var port = GetUnusedTcpPort();
        await using var server = new SocketServer(port, bindAddress: IPAddress.Loopback)
        {
            CommandLaneSelector = request => request.Type == "status"
                ? DaemonCommandLane.Control
                : DaemonCommandLane.Serialized,
            OnCommand = async (request, cancellationToken) =>
            {
                if (request.Type == "slow")
                {
                    slowCommandStarted.TrySetResult();
                    await releaseSlowCommand.Task.WaitAsync(cancellationToken);
                }

                return new CommandResponse { Id = request.Id, Success = true };
            }
        };

        try
        {
            await server.StartAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();

            await WriteCommandAsync(stream, new CommandRequest { Id = "slow-id", Type = "slow" });
            await slowCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WriteCommandAsync(stream, new CommandRequest { Id = "status-id", Type = "status" });

            var firstResponse = await ReadResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("status-id", firstResponse.Id);

            releaseSlowCommand.TrySetResult();
            var secondResponse = await ReadResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("slow-id", secondResponse.Id);
        }
        finally
        {
            releaseSlowCommand.TrySetResult();
            Environment.SetEnvironmentVariable(SocketSecretVariable, originalSecret);
        }
    }

    [Fact]
    public async Task SocketServer_RemoteDisconnect_CancelsAndDrainsClientHandler()
    {
        var originalSecret = Environment.GetEnvironmentVariable(SocketSecretVariable);
        Environment.SetEnvironmentVariable(SocketSecretVariable, null);
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? stopTask = null;
        var port = GetUnusedTcpPort();
        await using var server = new SocketServer(port, bindAddress: IPAddress.Loopback)
        {
            CommandLaneSelector = _ => DaemonCommandLane.Serialized,
            OnCommand = async (request, cancellationToken) =>
            {
                handlerStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new CommandResponse { Id = request.Id, Success = true };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await releaseCleanup.Task;
                    throw;
                }
            }
        };

        try
        {
            await server.StartAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await WriteCommandAsync(
                client.GetStream(),
                new CommandRequest { Id = "blocked-id", Type = "blocked" });
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            client.Dispose();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stopTask = server.StopAsync();
            await Assert.ThrowsAsync<TimeoutException>(
                () => stopTask.WaitAsync(TimeSpan.FromMilliseconds(200)));

            releaseCleanup.TrySetResult();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            if (stopTask != null)
            {
                await stopTask;
            }

            Environment.SetEnvironmentVariable(SocketSecretVariable, originalSecret);
        }
    }

    [Fact]
    public void SocketProgress_DefaultSuppressesDebugAndRetainsWarning()
    {
        var originalOutput = Console.Out;
        var originalDebug = AppConfig.DebugLogs;
        var originalVerbose = AppConfig.VerboseLogs;
        using var output = new StringWriter();

        try
        {
            AppConfig.DebugLogs = false;
            AppConfig.VerboseLogs = false;
            Console.SetOut(output);
            var progress = new SocketCommandInterface.SocketProgress();

            progress.OnLog(LogLevel.Debug, "debug-sentinel");
            progress.OnLog(LogLevel.Warning, "warning-sentinel");

            Assert.DoesNotContain("debug-sentinel", output.ToString());
            Assert.Contains("warning-sentinel", output.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            AppConfig.DebugLogs = originalDebug;
            AppConfig.VerboseLogs = originalVerbose;
        }
    }

    [Fact]
    public async Task SocketProgress_CancellationBroadcastsCancelledTerminal()
    {
        var originalSecret = Environment.GetEnvironmentVariable(SocketSecretVariable);
        Environment.SetEnvironmentVariable(SocketSecretVariable, null);
        var progress = new SocketCommandInterface.SocketProgress();
        var port = GetUnusedTcpPort();
        await using var server = new SocketServer(port, progress, IPAddress.Loopback)
        {
            CommandLaneSelector = _ => DaemonCommandLane.Concurrent,
            OnCommand = (request, _) => Task.FromResult(new CommandResponse
            {
                Id = request.Id,
                Success = true
            })
        };
        progress.SocketServer = server;

        try
        {
            await server.StartAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();

            await WriteCommandAsync(stream, new CommandRequest { Id = "ready-id", Type = "status" });
            _ = await ReadResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));

            progress.OnCancelled("Prefill cancelled by user");
            using var progressEvent = await ReadJsonFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
            var root = progressEvent.RootElement;

            Assert.Equal("progress", root.GetProperty("type").GetString());
            Assert.Equal("cancelled", root.GetProperty("data").GetProperty("state").GetString());
            Assert.Equal("Prefill cancelled by user", root.GetProperty("data").GetProperty("errorMessage").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SocketSecretVariable, originalSecret);
        }
    }

    private static int GetUnusedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteCommandAsync(NetworkStream stream, CommandRequest request)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            request,
            DaemonSerializationContext.Default.CommandRequest);
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<CommandResponse> ReadResponseAsync(NetworkStream stream)
    {
        using var frame = await ReadJsonFrameAsync(stream);
        return frame.RootElement.Deserialize(DaemonSerializationContext.Default.CommandResponse)
            ?? throw new InvalidOperationException("Response frame was empty.");
    }

    private static async Task<JsonDocument> ReadJsonFrameAsync(NetworkStream stream)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes);
        var payload = new byte[BitConverter.ToInt32(lengthBytes, 0)];
        await stream.ReadExactlyAsync(payload);
        return JsonDocument.Parse(payload);
    }
}
