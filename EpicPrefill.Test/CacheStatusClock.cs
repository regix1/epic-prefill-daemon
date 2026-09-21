namespace EpicPrefill.Test;

internal sealed class CacheStatusClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<ClockTimer> _timers = new();
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public CacheStatusClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow.ToUniversalTime();
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        List<(TimerCallback Callback, object? State)> callbacks = new();
        lock (_gate)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
            foreach (var timer in _timers.ToArray())
            {
                if (timer.TryTakeCallback(_utcNow, out var callback, out var state))
                {
                    callbacks.Add((callback, state));
                }
            }
        }

        foreach (var callback in callbacks)
        {
            callback.Callback(callback.State);
        }
    }

    private bool Change(ClockTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime));
        }
        if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        lock (_gate)
        {
            if (timer.Disposed)
            {
                return false;
            }

            timer.DueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : _utcNow + dueTime;
            timer.Period = period;
            _timers.Add(timer);
            return true;
        }
    }

    private void Remove(ClockTimer timer)
    {
        lock (_gate)
        {
            timer.Disposed = true;
            _timers.Remove(timer);
        }
    }

    private sealed class ClockTimer : ITimer
    {
        private readonly CacheStatusClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ClockTimer(CacheStatusClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? DueAtUtc { get; set; }
        public TimeSpan Period { get; set; }
        public bool Disposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
            => _clock.Change(this, dueTime, period);

        public void Dispose() => _clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public bool TryTakeCallback(
            DateTimeOffset utcNow,
            out TimerCallback callback,
            out object? state)
        {
            callback = _callback;
            state = _state;
            if (Disposed || DueAtUtc is null || DueAtUtc > utcNow)
            {
                return false;
            }

            DueAtUtc = Period > TimeSpan.Zero ? utcNow + Period : null;
            return true;
        }
    }
}
