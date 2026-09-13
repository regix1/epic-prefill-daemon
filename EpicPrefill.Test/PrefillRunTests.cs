using System.Reflection;
using System.Text.Json;
using EpicPrefill.Api;
using EpicPrefill.Models.Exceptions;
using LancachePrefill.Common;

namespace EpicPrefill.Test;

[Collection("ProcessEnvironment")]
public sealed class PrefillRunTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    [InlineData("invalid")]
    public void InvalidRunLimitFailsConstruction(string value)
    {
        var previous = Environment.GetEnvironmentVariable("PREFILL_MAX_RUNS");
        try
        {
            Environment.SetEnvironmentVariable("PREFILL_MAX_RUNS", value);
            Assert.Throws<ArgumentException>(() => new SocketCommandInterface(0));
        }
        finally { Environment.SetEnvironmentVariable("PREFILL_MAX_RUNS", previous); }
    }

    [Fact]
    public async Task LimitChangesApplyOnlyToTheNextInstance()
    {
        var previous = Environment.GetEnvironmentVariable("PREFILL_MAX_RUNS");
        try
        {
            Environment.SetEnvironmentVariable("PREFILL_MAX_RUNS", "3");
            using var first = new SocketCommandInterface(0);
            Environment.SetEnvironmentVariable("PREFILL_MAX_RUNS", "16");
            using var second = new SocketCommandInterface(0);
            var firstStatus = Assert.IsType<StatusData>((await Send(first, "status")).Data);
            var secondStatus = Assert.IsType<StatusData>((await Send(second, "status")).Data);
            Assert.Equal(3, firstStatus.MaxConcurrentRuns);
            Assert.Equal(16, secondStatus.MaxConcurrentRuns);
            Assert.NotEqual(firstStatus.DaemonInstanceId, secondStatus.DaemonInstanceId);
        }
        finally { Environment.SetEnvironmentVariable("PREFILL_MAX_RUNS", previous); }
    }

    [Fact]
    public async Task CapturedOptionsAndItemOutcomesSurviveLateCallbacks()
    {
        var protocol = new PrefillProtocol(3);
        using var budget = new RequestBudget(3);
        var ids = new List<string> { "A", "B" };
        var run = new PrefillRun("run", protocol, new RunOptions { AppIds = ids, MaxConcurrency = 2, Force = true },
            budget, new ItemClaims(), NullProgress.Instance);
        ids.Clear();
        await run.ExecuteAsync(_ =>
        {
            run.OnAppCompleted(new AppDownloadInfo { AppId = "A", Name = "A" }, AppDownloadResult.Failed);
            run.OnAppCompleted(new AppDownloadInfo { AppId = "B", Name = "B" }, AppDownloadResult.Skipped);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var before = run.Progress.Snapshot;
        run.OnDownloadProgress(new DownloadProgressInfo { AppId = "A", BytesDownloaded = 900 });
        run.OnAppCompleted(new AppDownloadInfo { AppId = "B" }, AppDownloadResult.Success);
        Assert.Equal(before, run.Progress.Snapshot);
        Assert.Equal("failed", before.State);
        Assert.Equal(2, before.TotalApps);
        Assert.Equal(1, before.SkippedApps);
        Assert.True(run.Options.Force);
        Assert.Equal(new[] { "A", "B" }, run.Options.AppIds);
    }

    [Fact]
    public async Task ProtocolAdmissionReplayRecoveryAndLegacyExclusionUseTheRealCommandHandler()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var commands = new SocketCommandInterface(0, async (_, run, token) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token);
            run?.OnAppCompleted(new AppDownloadInfo { AppId = run.Options.AppIds![0] }, AppDownloadResult.Success);
            return new PrefillResult { Success = true };
        });
        try
        {
            var status = Assert.IsType<StatusData>((await Send(commands, "status")).Data);
            Assert.Equal(2, status.ProtocolVersion);
            Assert.Equal(4, status.MaxConcurrentRuns);
            Assert.Equal(5, status.Features.Count);
            var first = Start("one", status.DaemonInstanceId!, "a");
            var second = Start("two", status.DaemonInstanceId!, "b");
            Assert.True((await Send(commands, first)).Success);
            Assert.True((await Send(commands, second)).Success);
            Assert.True((await Send(commands, first)).Success);
            Assert.Equal("operation-conflict", (await Send(commands, Start("one", status.DaemonInstanceId!, "other"))).Error);
            Assert.False((await Send(commands, "prefill")).Success);
            Assert.Contains("ambiguous", (await Send(commands, "cancel-prefill")).Error!, StringComparison.OrdinalIgnoreCase);
            var page = await Send(commands, new CommandRequest
            {
                Id = "page",
                Type = "get-operation",
                Parameters = new() { ["operationId"] = "one", ["daemonInstanceId"] = status.DaemonInstanceId!, ["limit"] = "1" }
            });
            var recovered = Assert.IsType<OperationPage>(page.Data);
            Assert.Equal("A", Assert.Single(recovered.Items).AppId);
            var json = JsonSerializer.Serialize(page, DaemonSerializationContext.Default.CommandResponse);
            Assert.Contains("daemonInstanceId", json);
            Assert.DoesNotContain("AccessToken", json, StringComparison.OrdinalIgnoreCase);
            Assert.True((await Send(commands, new CommandRequest
            {
                Id = "cancel",
                Type = "cancel-prefill",
                Parameters = new() { ["operationId"] = "one", ["daemonInstanceId"] = status.DaemonInstanceId! }
            })).Success);
            release.TrySetResult();
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AccountLossFailsEveryRunOnceAndClosesAdmission()
    {
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loseAccount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        using var commands = new SocketCommandInterface(0, async (_, run, token) =>
        {
            if (Interlocked.Increment(ref entered) == 3) { allEntered.TrySetResult(); }
            await allEntered.Task.WaitAsync(token);
            if (run!.Options.AppIds![0] == "A")
            {
                await loseAccount.Task.WaitAsync(token);
                throw new EpicLoginException("auth-lost");
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new PrefillResult { Success = true };
        });
        var status = Assert.IsType<StatusData>((await Send(commands, "status")).Data);
        foreach (var app in new[] { "a", "b", "c" })
        {
            Assert.True((await Send(commands, Start(app, status.DaemonInstanceId!, app))).Success);
        }
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        loseAccount.TrySetResult();
        var owner = (OwnedOperationCoordinator)typeof(SocketCommandInterface)
            .GetField("_prefillOperation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(commands)!;
        await Task.WhenAll(new[] { "a", "b", "c" }.Select(id => owner.WaitAsync(id))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(owner.GetRecentOperations(), run =>
        {
            Assert.Equal("failed", run.State);
            Assert.Equal("auth-lost", run.Reason);
        });
        Assert.Equal(3, owner.GetRecentOperations().Count);
        Assert.True((await Send(commands, Start("later", status.DaemonInstanceId!, "later"))).RequiresLogin);
    }

    [Fact]
    public async Task ExplicitEmptySelectionAndWrongInstanceDoNotDispatch()
    {
        var calls = 0;
        using var commands = new SocketCommandInterface(0, (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new PrefillResult { Success = true });
        });
        var status = Assert.IsType<StatusData>((await Send(commands, "status")).Data);
        var empty = Start("empty", status.DaemonInstanceId!, "a");
        empty.Parameters!["appIds"] = "[]";
        Assert.False((await Send(commands, empty)).Success);
        Assert.Equal("instance-changed", (await Send(commands, Start("wrong", "old-instance", "a"))).Error);
        Assert.Equal(0, calls);
    }

    private static CommandRequest Start(string id, string instance, string app) => new()
    {
        Id = id,
        Type = "prefill",
        Parameters = new()
        {
            ["protocolVersion"] = "2",
            ["daemonInstanceId"] = instance,
            ["appIds"] = JsonSerializer.Serialize(new[] { app })
        }
    };

    private static Task<CommandResponse> Send(SocketCommandInterface commands, string type)
        => Send(commands, new CommandRequest { Id = Guid.NewGuid().ToString(), Type = type });

    private static Task<CommandResponse> Send(SocketCommandInterface commands, CommandRequest request)
        => (Task<CommandResponse>)typeof(SocketCommandInterface)
            .GetMethod("HandleCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(commands, new object[] { request, CancellationToken.None })!;
}
