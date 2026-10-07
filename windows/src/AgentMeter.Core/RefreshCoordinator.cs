namespace AgentMeter.Core;

public enum RefreshReason { Manual, Background, Reset, Enable }

public sealed class RefreshCoordinator
{
    private readonly IUsageProvider[] providers;
    private readonly Dictionary<string, Entry> entries;
    private readonly object stateLock = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout, minimumInterval;
    private readonly Action<string> log;
    private bool suspended;
    public event Action? Changed;
    public bool IsRefreshing { get { lock (stateLock) return entries.Values.Any(e => e.State.Enabled && e.Active); } }

    private sealed class Entry(string name)
    {
        public ProviderState State = new(name, ProviderStatus.Loading);
        public bool Active;
        public int Generation, Failures;
        public long? LastStart, LastReset;
        public Deadline? Background, Embargo;
        public CancellationTokenSource? Cancellation;
        public Task<ProviderResult>? Pending;
    }
    private readonly record struct Deadline(long Start, TimeSpan Delay);
    private TimeSpan Left(Deadline? due) => due is { } d
        ? Max(TimeSpan.Zero, d.Delay - clock.GetElapsedTime(d.Start, clock.GetTimestamp())) : TimeSpan.Zero;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private TimeSpan ManualWait(Entry e) => Max(Left(e.Embargo), e.LastStart is { } last
        ? Max(TimeSpan.Zero, minimumInterval - clock.GetElapsedTime(last, clock.GetTimestamp())) : TimeSpan.Zero);

    public RefreshCoordinator(IEnumerable<IUsageProvider> providers, Action<string>? log = null,
        TimeProvider? clock = null, TimeSpan? timeout = null, TimeSpan? minimumInterval = null)
    {
        this.providers = providers.ToArray();
        entries = this.providers.ToDictionary(p => p.Name, p => new Entry(p.Name));
        this.log = log ?? (_ => { }); this.clock = clock ?? TimeProvider.System;
        this.timeout = timeout ?? TimeSpan.FromSeconds(25);
        this.minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(10);
        if (this.timeout <= TimeSpan.Zero || this.timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (this.minimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
    }
    public IReadOnlyList<ProviderState> States
    {
        get
        {
            lock (stateLock) return providers.Select(p =>
            {
                var e = entries[p.Name]; var wait = ManualWait(e);
                return e.State with { RetryAt = e.State.Enabled && wait > TimeSpan.Zero ? clock.GetUtcNow() + wait : null };
            }).ToArray();
        }
    }
    public void SetEnabled(string name, bool enabled)
    {
        CancellationTokenSource? cancellation;
        lock (stateLock)
        {
            if (!entries.TryGetValue(name, out var e) || e.State.Enabled == enabled) return;
            ++e.Generation; cancellation = e.Cancellation;
            e.State = new(name, enabled ? ProviderStatus.Loading : ProviderStatus.Unavailable, Enabled: enabled);
            // A toggle never shortens a provider rate-limit embargo.
            e.Background = null; e.Failures = 0; e.LastReset = null;
        }
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        Changed?.Invoke();
    }
    public void Suspend()
    {
        CancellationTokenSource[] cancellations;
        lock (stateLock)
        {
            suspended = true;
            foreach (var e in entries.Values)
            {
                ++e.Generation;
                if (e.Active) e.State = e.State with { Status = e.State.Snapshot is null ? ProviderStatus.Unavailable : ProviderStatus.Ready };
            }
            cancellations = entries.Values.Select(e => e.Cancellation).OfType<CancellationTokenSource>().ToArray();
        }
        foreach (var c in cancellations) try { c.Cancel(); } catch (ObjectDisposedException) { }
        Changed?.Invoke();
    }
    public void Resume()
    {
        lock (stateLock) suspended = false;
        Changed?.Invoke();
    }
    public async Task DrainAsync()
    {
        Task[] operations;
        lock (stateLock) operations = entries.Values.Select(e => e.Pending).OfType<Task>().ToArray();
        try { await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
        catch (Exception) { /* Owned adapter cleanup stays bounded at shutdown. */ }
    }
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default, RefreshReason reason = RefreshReason.Manual)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = new List<(IUsageProvider Provider, ProviderState Previous, int Generation, CancellationTokenSource Cancellation)>();
        lock (stateLock)
        {
            if (suspended) return false;
            foreach (var provider in providers)
            {
                var e = entries[provider.Name];
                if (!e.State.Enabled || e.Active || e.Pending is { IsCompleted: false } || ManualWait(e) > TimeSpan.Zero) continue;
                var at = clock.GetUtcNow(); var stamp = clock.GetTimestamp();
                var resetDue = UsagePresentation.Windows(e.State).Any(w => w.ResetsAt <= at);
                var resetWait = e.LastReset is { } lastReset && clock.GetElapsedTime(lastReset, stamp) < TimeSpan.FromSeconds(30);
                if (reason is RefreshReason.Background or RefreshReason.Reset)
                {
                    if (e.Failures > 0 && Left(e.Background) > TimeSpan.Zero) continue;
                    if (Left(e.Background) > TimeSpan.Zero && (!resetDue || resetWait)) continue;
                    if (reason == RefreshReason.Reset && resetWait) continue;
                }
                e.Active = true; e.LastStart = stamp;
                if (resetDue || reason == RefreshReason.Reset) e.LastReset = stamp;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                e.Cancellation = cancellation;
                selected.Add((provider, e.State, e.Generation, cancellation));
                e.State = e.State with { Status = ProviderStatus.Loading };
            }
        }
        if (selected.Count == 0) return false;
        log("refresh.started"); Changed?.Invoke();
        await Task.WhenAll(selected.Select(item => RefreshProviderAsync(item.Provider, item.Previous, item.Generation, item.Cancellation, cancellationToken))).ConfigureAwait(false);
        log("refresh.completed"); return true;
    }
    private async Task RefreshProviderAsync(IUsageProvider provider, ProviderState previous, int generation,
        CancellationTokenSource cancellation, CancellationToken lifetime)
    {
        try
        {
            ProviderResult result;
            cancellation.CancelAfter(timeout);
            try
            {
                var operation = Task.Run(() => provider.QueryAsync(cancellation.Token), cancellation.Token);
                lock (stateLock) entries[provider.Name].Pending = operation;
                _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                result = await operation.WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (stateLock)
                {
                    var e = entries[provider.Name];
                    if (e.Generation != generation || suspended) return;
                    if (lifetime.IsCancellationRequested) { e.State = previous; return; }
                }
                result = ProviderResult.Fail(FailureKind.Timeout);
            }
            catch (UnauthorizedAccessException) { result = ProviderResult.Fail(FailureKind.AccessDenied); }
            catch (Exception) { result = ProviderResult.Fail(FailureKind.Unexpected); }

            if (result.Failure == FailureKind.None && (result.Snapshot is not { } snapshot ||
                snapshot.ObservedAt > clock.GetUtcNow().AddMinutes(1)))
                result = result with { Snapshot = null, Failure = FailureKind.Malformed };
            var success = result.Snapshot is not null && result.Failure == FailureKind.None;
            lock (stateLock)
            {
                var e = entries[provider.Name];
                if (e.Generation != generation || !e.State.Enabled || suspended) return;
                var auth = result.Failure switch
                {
                    FailureKind.LoggedOut => AuthenticationStatus.SignedOut,
                    FailureKind.NotInstalled => AuthenticationStatus.Missing,
                    FailureKind.UnsupportedBilling => AuthenticationStatus.UnsupportedBilling,
                    FailureKind.AccountChanged => AuthenticationStatus.Unknown,
                    _ => result.Authentication == AuthenticationStatus.Verified && result.VerifiedBinding is { Digest.Length: > 0 }
                        ? AuthenticationStatus.Verified : AuthenticationStatus.Unknown
                };
                var retained = !success && auth == AuthenticationStatus.Verified &&
                    previous.Snapshot?.Binding is { } oldBinding && oldBinding == result.VerifiedBinding &&
                    result.Failure is FailureKind.Timeout or FailureKind.Network or FailureKind.Malformed or FailureKind.ProcessExited or FailureKind.RateLimited or FailureKind.Unexpected
                    ? previous.Snapshot : null;
                e.State = new(provider.Name, success ? ProviderStatus.Ready :
                    result.Failure is FailureKind.NotInstalled or FailureKind.LoggedOut or FailureKind.Unsupported or FailureKind.UnsupportedBilling
                        ? ProviderStatus.Unavailable : ProviderStatus.Error,
                    success ? result.Snapshot! with { Binding = result.VerifiedBinding } : retained,
                    result.Failure, result.Detail, clock.GetUtcNow(), Authentication: auth);
                var stamp = clock.GetTimestamp();
                e.Failures = success ? 0 : Math.Min(e.Failures + 1, 10);
                var delay = success ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(Math.Min(900, 30 * Math.Pow(2, e.Failures - 1)));
                e.Background = new(stamp, delay);
                if (result.Failure == FailureKind.RateLimited || result.RetryAfter is not null)
                {
                    var requested = result.RetryAfter ?? delay;
                    var embargo = TimeSpan.FromSeconds(Math.Clamp(requested.TotalSeconds, 10, 86400));
                    e.Embargo = new(stamp, Max(Left(e.Embargo), embargo));
                }
            }
            log($"provider.{provider.Name}.{(success ? "succeeded" : "failed")}.{result.Failure}");
        }
        finally
        {
            lock (stateLock)
            {
                var e = entries[provider.Name]; e.Active = false;
                if (ReferenceEquals(e.Cancellation, cancellation)) e.Cancellation = null;
            }
            cancellation.Dispose(); Changed?.Invoke();
        }
    }
}
