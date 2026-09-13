#nullable enable

using System.Threading;

namespace EpicPrefill.Api;

public sealed class PrefillRun : IPrefillProgress
{
    private static readonly AsyncLocal<PrefillRun?> Scope = new();
    private readonly RequestBudget _budget;
    private readonly ItemClaims _claims;
    private readonly IPrefillProgress _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _leases = new();

    public PrefillRun(string operationId, PrefillProtocol protocol, RunOptions options,
        RequestBudget budget, ItemClaims claims, IPrefillProgress log,
        Func<RunSnapshot, CancellationToken, Task>? publish = null)
    {
        Options = protocol.Capture(options);
        Progress = new RunProgress(operationId, protocol.DaemonInstanceId, Options, publish);
        _budget = budget;
        _claims = claims;
        _log = log;
    }

    public static PrefillRun? Current => Scope.Value;
    public RunOptions Options { get; }
    public RunProgress Progress { get; }

    public async Task ExecuteAsync(Func<CancellationToken, Task> execute, CancellationToken cancellationToken)
    {
        var previous = Scope.Value;
        Scope.Value = this;
        try
        {
            await execute(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Progress.TryChooseTerminal("cancelled");
            throw;
        }
        catch (Exception)
        {
            Progress.TryChooseTerminal("failed", "prefill-failed");
            throw;
        }
        finally
        {
            try
            {
                await Progress.CompleteAsync(itemBytesTransferred: _bytes);
            }
            finally
            {
                foreach (var lease in _leases) { lease.Dispose(); }
                _leases.Clear();
                Scope.Value = previous;
            }
        }
    }

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
        => _budget.AcquireAsync(Progress.Snapshot.OperationId, Options.MaxConcurrency, cancellationToken);

    public IDisposable? TryClaim(string appId)
    {
        var lease = _claims.TryClaim(Progress.Snapshot.OperationId, new[] { appId.ToUpperInvariant() });
        if (lease != null) { _leases.Add(lease); }
        return lease;
    }

    public static PrefillProgressUpdate ToUpdate(RunSnapshot snapshot)
    {
        var item = snapshot.CurrentItem;
        return new PrefillProgressUpdate
        {
            OperationId = snapshot.OperationId,
            DaemonInstanceId = snapshot.DaemonInstanceId,
            Sequence = snapshot.Sequence,
            State = snapshot.State is "completed" or "failed" or "cancelled" or "cancelling"
                ? snapshot.State : item?.Result == null ? snapshot.State : "app_completed",
            CurrentAppId = item?.AppId,
            CurrentAppName = item?.Name,
            Result = item?.Result,
            Reason = snapshot.Reason ?? item?.Reason,
            TotalBytes = item?.TotalBytes ?? 0,
            BytesDownloaded = item?.BytesTransferred ?? 0,
            TotalBytesTransferred = snapshot.BytesTransferred,
            TotalApps = snapshot.TotalApps,
            UpdatedApps = snapshot.CompletedApps,
            AlreadyUpToDate = snapshot.CachedApps,
            FailedApps = snapshot.FailedApps,
            SkippedApps = snapshot.SkippedApps,
            CancelledApps = snapshot.CancelledApps,
            UpdatedAt = snapshot.UpdatedAt.UtcDateTime,
            TotalTime = snapshot.UpdatedAt - snapshot.StartedAt
        };
    }

    public void OnLog(LogLevel level, string message) => _log.OnLog(level, message);
    public void OnOperationStarted(string operationName) { }
    public void OnOperationCompleted(string operationName, TimeSpan elapsed) { }
    public void OnError(string message, Exception? exception = null) => _log.OnLog(LogLevel.Error, message);
    public void OnPrefillCompleted(PrefillSummary summary) { }

    public void OnAppStarted(AppDownloadInfo app)
    {
        lock (_sync)
        {
            Progress.UpdateItem(new RunItemSnapshot
            {
                AppId = app.AppId.ToUpperInvariant(),
                Name = app.Name,
                State = "downloading",
                TotalBytes = app.TotalBytes,
                BytesTransferred = _bytes.GetValueOrDefault(app.AppId.ToUpperInvariant())
            });
        }
    }

    public void OnDownloadProgress(DownloadProgressInfo progress)
    {
        lock (_sync)
        {
            var id = progress.AppId.ToUpperInvariant();
            _bytes[id] = Math.Max(_bytes.GetValueOrDefault(id), progress.BytesDownloaded);
            Progress.UpdateItem(new RunItemSnapshot
            {
                AppId = id,
                Name = progress.AppName,
                State = "downloading",
                TotalBytes = progress.TotalBytes,
                BytesTransferred = _bytes[id]
            });
        }
    }

    public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result)
        => CompleteItem(app, result, null);

    public bool TryCommitItem(AppDownloadInfo app, Action commit)
        => CompleteItem(app, AppDownloadResult.Success, commit);

    private bool CompleteItem(AppDownloadInfo app, AppDownloadResult result, Action? commit)
    {
        lock (_sync)
        {
            var outcome = result switch
            {
                AppDownloadResult.Success => "success",
                AppDownloadResult.AlreadyUpToDate => "already_cached",
                AppDownloadResult.Skipped => "skipped",
                _ => "failed"
            };
            var item = new RunItemSnapshot
            {
                AppId = app.AppId.ToUpperInvariant(),
                Name = app.Name,
                State = "app_completed",
                Result = outcome,
                Reason = result == AppDownloadResult.Skipped ? "skippedOverlap" : null,
                TotalBytes = app.TotalBytes,
                BytesTransferred = _bytes.GetValueOrDefault(app.AppId.ToUpperInvariant())
            };
            return commit == null ? Progress.UpdateItem(item) : Progress.TryCommitItem(item, commit);
        }
    }
}
