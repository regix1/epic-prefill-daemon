using EpicPrefill.Handlers;
using EpicPrefill.Models;
using EpicPrefill.Api;
using LancachePrefill.Common;

namespace EpicPrefill.Test;

public sealed class CacheCommitTests
{
    [Fact]
    public async Task CancellationBeforeMarkerCommitPreservesDiskAndMemory()
    {
        var directory = Directory.CreateTempSubdirectory("epic-marker-");
        try
        {
            var path = Path.Combine(directory.FullName, "success.json");
            var commits = 0;
            var handler = new AppInfoHandler(ConcurrentPrefillTests.CreateConsole(), path, (source, target) =>
            {
                Interlocked.Increment(ref commits);
                File.Move(source, target, true);
            });
            handler.MarkDownloadAsSuccessful(new AppInfo { AppId = "existing", BuildVersion = "1" });
            var before = File.ReadAllText(path);
            var app = new AppInfo { AppId = "target", BuildVersion = "2", Title = "Target" };
            using var budget = new RequestBudget(1);
            var run = new PrefillRun("cancel-first", new PrefillProtocol(1),
                new RunOptions { AppIds = new[] { "TARGET" }, MaxConcurrency = 1 }, budget, new ItemClaims(), NullProgress.Instance);
            await run.ExecuteAsync(_ =>
            {
                run.OnDownloadProgress(new DownloadProgressInfo { AppId = "target", BytesDownloaded = 12, TotalBytes = 12 });
                Assert.True(run.Progress.TryChooseTerminal("cancelled"));
                Assert.False(handler.MarkDownloadAsSuccessful(app, run, 12));
                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.Equal(1, commits);
            Assert.Equal(before, File.ReadAllText(path));
            Assert.False(handler.AppIsUpToDate(app));
            Assert.Single(directory.GetFiles());
            Assert.Equal("cancelled", run.Progress.Snapshot.State);
            Assert.Equal(0, run.Progress.Snapshot.CompletedApps);
            Assert.Equal(12, run.Progress.Snapshot.BytesTransferred);
            Assert.Equal("cancelled", Assert.Single(run.Progress.GetPage().Items).Result);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task MarkerCommitBeforeCancellationRetainsOneSuccessfulItem()
    {
        var directory = Directory.CreateTempSubdirectory("epic-marker-");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? commit = null;
        Task? cancel = null;
        try
        {
            var path = Path.Combine(directory.FullName, "success.json");
            var handler = new AppInfoHandler(ConcurrentPrefillTests.CreateConsole(), path, (source, target) =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Commit was not released."); }
                File.Move(source, target, true);
            });
            var app = new AppInfo { AppId = "target", BuildVersion = "2", Title = "Target" };
            using var budget = new RequestBudget(1);
            var run = new PrefillRun("commit-first", new PrefillProtocol(1),
                new RunOptions { AppIds = new[] { "TARGET" }, MaxConcurrency = 1 }, budget, new ItemClaims(), NullProgress.Instance);
            run.OnDownloadProgress(new DownloadProgressInfo { AppId = "target", BytesDownloaded = 12, TotalBytes = 12 });
            var sequence = run.Progress.Snapshot.Sequence;
            commit = Task.Run(() => Assert.True(handler.MarkDownloadAsSuccessful(app, run, 12)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancel = Task.Run(() =>
            {
                cancelling.TrySetResult();
                Assert.True(run.Progress.TryChooseTerminal("cancelled"));
            });
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            await Task.WhenAll(commit, cancel).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(sequence + 1, run.Progress.Snapshot.Sequence);
            await run.Progress.CompleteAsync();

            Assert.True(handler.AppIsUpToDate(app));
            Assert.True(new AppInfoHandler(ConcurrentPrefillTests.CreateConsole(), path,
                (source, target) => File.Move(source, target, true)).AppIsUpToDate(app));
            Assert.Single(directory.GetFiles());
            Assert.Equal("cancelled", run.Progress.Snapshot.State);
            Assert.Equal(1, run.Progress.Snapshot.CompletedApps);
            Assert.Equal(12, run.Progress.Snapshot.BytesTransferred);
            Assert.Equal("success", Assert.Single(run.Progress.GetPage().Items).Result);
        }
        finally
        {
            release.Set();
            if (commit != null) { await commit; }
            if (cancel != null) { await cancel; }
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task MarkerReplacementFailureUsesTheItemFailureFunnel()
    {
        var directory = Directory.CreateTempSubdirectory("epic-marker-");
        try
        {
            var path = Path.Combine(directory.FullName, "success.json");
            var fail = false;
            var failure = new IOException("Injected commit failure");
            var handler = new AppInfoHandler(ConcurrentPrefillTests.CreateConsole(), path, (source, target) =>
            {
                if (fail) { throw failure; }
                File.Move(source, target, true);
            });
            handler.MarkDownloadAsSuccessful(new AppInfo { AppId = "existing", BuildVersion = "1" });
            var before = File.ReadAllText(path);
            fail = true;
            var app = new AppInfo { AppId = "target", BuildVersion = "2", Title = "Target" };
            using var budget = new RequestBudget(1);
            var run = new PrefillRun("commit-fails", new PrefillProtocol(1),
                new RunOptions { AppIds = new[] { "TARGET" }, MaxConcurrency = 1 }, budget, new ItemClaims(), NullProgress.Instance);
            await run.ExecuteAsync(_ =>
            {
                run.OnDownloadProgress(new DownloadProgressInfo { AppId = "target", BytesDownloaded = 12, TotalBytes = 12 });
                var snapshot = run.Progress.Snapshot;
                Assert.Same(failure, Assert.Throws<IOException>(() => handler.MarkDownloadAsSuccessful(app, run, 12)));
                Assert.Equal(snapshot, run.Progress.Snapshot);
                run.OnAppCompleted(new AppDownloadInfo { AppId = "target", Name = "Target", TotalBytes = 12 }, AppDownloadResult.Failed);
                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.Equal(before, File.ReadAllText(path));
            Assert.False(handler.AppIsUpToDate(app));
            Assert.Single(directory.GetFiles());
            Assert.Equal("failed", run.Progress.Snapshot.State);
            Assert.Equal(0, run.Progress.Snapshot.CompletedApps);
            Assert.Equal(1, run.Progress.Snapshot.FailedApps);
            Assert.Equal(12, run.Progress.Snapshot.BytesTransferred);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ConcurrentSuccessfulAppsMergeWithoutLosingVersions()
    {
        var directory = Directory.CreateTempSubdirectory("epic-marker-");
        try
        {
            var path = Path.Combine(directory.FullName, "success.json");
            var handler = new AppInfoHandler(ConcurrentPrefillTests.CreateConsole(), path,
                (source, target) => File.Move(source, target, true));
            var apps = Enumerable.Range(0, 16).Select(i => new AppInfo { AppId = "app-" + i, BuildVersion = "version" }).ToArray();
            await Task.WhenAll(apps.Select(app => Task.Run(() => handler.MarkDownloadAsSuccessful(app))));
            var loaded = new AppInfoHandler(ConcurrentPrefillTests.CreateConsole(), path,
                (source, target) => File.Move(source, target, true));
            Assert.All(apps, app => Assert.True(loaded.AppIsUpToDate(app)));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void FailedCommitDoesNotPublishMemoryOrReplacePreviousSuccess()
    {
        var directory = Directory.CreateTempSubdirectory("epic-marker-");
        try
        {
            var path = Path.Combine(directory.FullName, "success.json");
            var fail = false;
            var handler = new AppInfoHandler(ConcurrentPrefillTests.CreateConsole(), path, (source, target) =>
            {
                if (fail) { throw new IOException("Injected commit failure"); }
                File.Move(source, target, true);
            });
            var first = new AppInfo { AppId = "a", BuildVersion = "1" };
            var second = new AppInfo { AppId = "b", BuildVersion = "2" };
            handler.MarkDownloadAsSuccessful(first);
            var before = File.ReadAllText(path);
            fail = true;
            Assert.Throws<IOException>(() => handler.MarkDownloadAsSuccessful(second));
            Assert.True(handler.AppIsUpToDate(first));
            Assert.False(handler.AppIsUpToDate(second));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }
}
