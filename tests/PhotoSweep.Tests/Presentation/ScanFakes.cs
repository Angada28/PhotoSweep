using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Tests.Presentation;

/// <summary>
/// Records every call and leaves it pending until the test finishes it, so a test controls exactly when the scan
/// reports progress, completes, fails or stops.
/// </summary>
internal sealed class FakeScanService : IScanService
{
    public List<ScanCall> Scans { get; } = [];

    public List<GroupCall> Groupings { get; } = [];

    public ScanCall LastScan => Scans[^1];

    public GroupCall LastGrouping => Groupings[^1];

    public Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var call = new ScanCall(options, progress, ct);
        Scans.Add(call);
        return call.Task;
    }

    public Task<IReadOnlyList<PhotoGroup>> GroupAsync(ScanResult scan, MatchLevel level, CancellationToken ct)
    {
        var call = new GroupCall(scan, level, ct);
        Groupings.Add(call);
        return call.Task;
    }
}

internal sealed class ScanCall(ScanOptions options, IProgress<ScanProgress> progress, CancellationToken token)
{
    // No RunContinuationsAsynchronously: with InlineSynchronizationContext the view-model reacts before Complete returns.
    private readonly TaskCompletionSource<ScanResult> _result = new();

    public ScanOptions Options { get; } = options;
    public IProgress<ScanProgress> Progress { get; } = progress;
    public CancellationToken Token { get; } = token;
    public Task<ScanResult> Task => _result.Task;

    public ScanResult Complete(params ScannedFile[] files)
    {
        var result = new ScanResult(files, cacheHits: 0);
        _result.SetResult(result);
        return result;
    }

    public void Fail(Exception ex) => _result.SetException(ex);

    /// <summary>What the real scanner does once its workers have stopped and the cache is saved.</summary>
    public void StopCancelled() => _result.SetCanceled(Token);
}

internal sealed class GroupCall(ScanResult scan, MatchLevel level, CancellationToken token)
{
    private readonly TaskCompletionSource<IReadOnlyList<PhotoGroup>> _result = new();

    public ScanResult Scan { get; } = scan;
    public MatchLevel Level { get; } = level;
    public CancellationToken Token { get; } = token;
    public Task<IReadOnlyList<PhotoGroup>> Task => _result.Task;

    public void Complete(params PhotoGroup[] groups) => _result.SetResult(groups);

    public void Fail(Exception ex) => _result.SetException(ex);
}

/// <summary>Scanned files as the scanner would report them.</summary>
internal static class Scanned
{
    public static ScannedFile Photo(string path, long size = 1000) =>
        new(path, size, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)) { Status = ScanStatus.Ok, Sha256 = path };

    public static ScannedFile OnlineOnly(string path, long size) =>
        new(path, size, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)) { Status = ScanStatus.OnlineOnlySkipped };

    /// <summary>A chosen folder that couldn't be opened: the scanner records it under the folder's own path.</summary>
    public static ScannedFile MissingFolder(string folder) =>
        new(folder, 0, default) { Status = ScanStatus.Unreadable, Error = "Can't read folder: Could not find a part of the path." };

    public static PhotoGroup Group(params ScannedFile[] files) =>
        new(files.Select((f, i) => new GroupMember(f, i == 0 ? MatchKind.Keeper : MatchKind.Identical, null, null)).ToList(), "test");
}

/// <summary>
/// Runs posted callbacks immediately on the posting thread. With no UI, <see cref="Progress{T}"/> and <c>await</c>
/// would otherwise post to the thread pool and tests would race; with this, a progress report or a completed fake
/// call has been fully handled by the view-model by the time the test's next line runs.
/// </summary>
/// <remarks>
/// Install it inside the test method (xUnit sets its own context between the constructor and the method), with
/// <c>using</c> so it's removed again.
/// </remarks>
internal sealed class InlineSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public static IDisposable Install()
    {
        var previous = Current;
        SetSynchronizationContext(new InlineSynchronizationContext());
        return new Restore(previous);
    }

    private sealed class Restore(SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => SetSynchronizationContext(previous);
    }
}

/// <summary>A clock that only moves when the test calls <see cref="Advance"/>; timers created from it fire then.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now;

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_now);

    /// <summary>Timers created and not yet disposed.</summary>
    public int LiveTimers => _timers.Count;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock forward, firing each timer at its due times in order.</summary>
    public void Advance(TimeSpan by)
    {
        var target = _now + by.Ticks;
        while (_timers.Where(t => t.Due <= target).MinBy(t => t.Due) is { } next)
        {
            _now = next.Due!.Value;
            next.Fire();
        }

        _now = target;
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private long _period;

        public long? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime.Ticks;
            _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            return true;
        }

        public void Fire()
        {
            Due = _period > 0 ? Due + _period : null;
            callback(state);
        }

        public void Dispose()
        {
            Due = null;
            clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
