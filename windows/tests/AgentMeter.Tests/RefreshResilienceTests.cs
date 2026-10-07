using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class RefreshResilienceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T00:00:00Z");
    private static ProviderResult Good(double used = 25) => new(new UsageSnapshot([new("primary", "5 hours", used, Now.AddHours(2), 300, UsageScope.General)], Now, "fixture")) { VerifiedBinding = new("fixture"), Authentication = AuthenticationStatus.Verified };

    [Fact]
    public async Task HealthyProviderCanRefreshAgainWhileAnotherQueryIsStillPending()
    {
        var release = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHealthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new Provider("Slow", _ => release.Task);
        var healthy = new Provider("Healthy", _ => Task.FromResult(Good()));
        var coordinator = new RefreshCoordinator([slow, healthy], minimumInterval: TimeSpan.Zero, clock: new Clock());
        coordinator.Changed += () => { if (coordinator.States[1].Status == ProviderStatus.Ready) firstHealthy.TrySetResult(); };
        var first = coordinator.RefreshAsync();
        try
        {
            await firstHealthy.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(await coordinator.RefreshAsync());
            Assert.Equal(2, healthy.Calls);
            Assert.Equal(1, slow.Calls);
            Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(Good()); }
        await first;
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task BurstOfRefreshRequestsNeverOverlapsAnyProvider()
    {
        var first = new Provider("First", async token => { await Task.Delay(10, token); return Good(); });
        var second = new Provider("Second", async token => { await Task.Delay(2, token); return Good(); });
        var coordinator = new RefreshCoordinator([first, second], minimumInterval: TimeSpan.Zero, clock: new Clock());
        for (var batch = 0; batch < 20; batch++)
            await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => coordinator.RefreshAsync())));
        Assert.Equal(1, first.MaximumConcurrent);
        Assert.Equal(1, second.MaximumConcurrent);
        Assert.True(first.Calls >= 20);
        Assert.True(second.Calls >= 20);
        Assert.All(coordinator.States, state => Assert.Equal(ProviderStatus.Ready, state.Status));
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task RepeatedFailureRecoveryNeverLeavesLiveFailureOrLosesRecovery()
    {
        var next = Good();
        var coordinator = new RefreshCoordinator([new Provider("Test", _ => Task.FromResult(next))],
            minimumInterval: TimeSpan.Zero, clock: new Clock());
        for (var index = 0; index < 100; index++)
        {
            next = Good(index % 101);
            await coordinator.RefreshAsync();
            Assert.Equal("Live", PopupText.Status(coordinator.States[0], Now));
            next = ProviderResult.Fail(FailureKind.Network) with { VerifiedBinding = new("fixture"), Authentication = AuthenticationStatus.Verified };
            await coordinator.RefreshAsync();
            Assert.Equal("Stale", PopupText.Status(coordinator.States[0], Now));
            Assert.Equal(100 - index, coordinator.States[0].Snapshot!.Windows[0].RemainingPercent);
        }
    }

    [Fact]
    public async Task ClockJumpsDoNotBypassCooldownAndResetCountdownUsesCurrentInstant()
    {
        var clock = new Clock();
        var provider = new Provider("Test", _ => Task.FromResult(Good()));
        var coordinator = new RefreshCoordinator([provider], clock: clock);
        await coordinator.RefreshAsync();
        clock.UtcNow = Now.AddDays(2);
        Assert.False(await coordinator.RefreshAsync());
        Assert.Equal("Reset passed · awaiting update", PopupText.Reset(coordinator.States[0].Snapshot!.Windows[0], clock.UtcNow));
        clock.UtcNow = Now.AddDays(-1);
        Assert.False(await coordinator.RefreshAsync());
        clock.Timestamp += 10;
        clock.UtcNow = Now;
        Assert.True(await coordinator.RefreshAsync());
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ResetReachedAndChangedWindowDoNotInventFullAllowance()
    {
        var clock = new Clock();
        var next = Good(100);
        var coordinator = new RefreshCoordinator([new Provider("Test", _ => Task.FromResult(next))], minimumInterval: TimeSpan.Zero, clock: clock);
        await coordinator.RefreshAsync();
        clock.UtcNow = Now.AddHours(2);
        Assert.Equal("Stale", PopupText.Status(coordinator.States[0], clock.UtcNow));
        Assert.Equal(0, coordinator.States[0].Snapshot!.Windows[0].RemainingPercent);
        next = new(new UsageSnapshot([new("primary", "5 hours", 4, Now.AddHours(7), 300, UsageScope.General)], clock.UtcNow, "fixture"));
        await coordinator.RefreshAsync();
        Assert.Equal("Live", PopupText.Status(coordinator.States[0], clock.UtcNow));
        Assert.Equal(96, coordinator.States[0].Snapshot!.Windows[0].RemainingPercent);
    }

    [Theory]
    [InlineData(FailureKind.NotInstalled)]
    [InlineData(FailureKind.LoggedOut)]
    [InlineData(FailureKind.Unsupported)]
    public async Task MissingOrUnverifiedAccountCannotRetainLastAccountValues(FailureKind failure)
    {
        var next = Good();
        var coordinator = new RefreshCoordinator([new Provider("Test", _ => Task.FromResult(next))], minimumInterval: TimeSpan.Zero, clock: new Clock());
        await coordinator.RefreshAsync();
        next = ProviderResult.Fail(failure);
        await coordinator.RefreshAsync();
        Assert.Null(coordinator.States[0].Snapshot);
    }

    [Fact]
    public async Task CancellationRestoresPreviousStateAndDrainWaitsForProviderCleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var call = 0;
        var provider = new Provider("Test", async token =>
        {
            if (++call == 1) return Good();
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await Task.Delay(50); cleaned = true; }
            return Good();
        });
        var coordinator = new RefreshCoordinator([provider], minimumInterval: TimeSpan.Zero, clock: new Clock());
        await coordinator.RefreshAsync();
        using var cancellation = new CancellationTokenSource();
        var refresh = coordinator.RefreshAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await refresh;
        Assert.Equal(ProviderStatus.Ready, coordinator.States[0].Status);
        await coordinator.DrainAsync();
        Assert.True(cleaned);
    }

    [Fact]
    public async Task AdapterCannotReportLiveWithNoSnapshotOrFutureObservation()
    {
        var next = new ProviderResult(null);
        var coordinator = new RefreshCoordinator([new Provider("Test", _ => Task.FromResult(next))], minimumInterval: TimeSpan.Zero, clock: new Clock());
        await coordinator.RefreshAsync();
        Assert.Equal(FailureKind.Malformed, coordinator.States[0].Failure);
        next = new(new UsageSnapshot([new("test", "Test", 0, null)], Now.AddMinutes(2), "fixture"));
        await coordinator.RefreshAsync();
        Assert.Equal(FailureKind.Malformed, coordinator.States[0].Failure);
        Assert.Null(coordinator.States[0].Snapshot);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public long Timestamp { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => 1;
    }
    private sealed class Provider(string name, Func<CancellationToken, Task<ProviderResult>> query) : IUsageProvider
    {
        private int calls, concurrent, maximumConcurrent;
        public string Name => name;
        public int Calls => Volatile.Read(ref calls);
        public int MaximumConcurrent => Volatile.Read(ref maximumConcurrent);
        public async Task<ProviderResult> QueryAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref concurrent);
            int previous;
            do { previous = maximumConcurrent; }
            while (current > previous && Interlocked.CompareExchange(ref maximumConcurrent, current, previous) != previous);
            try { return await query(cancellationToken); }
            finally { Interlocked.Decrement(ref concurrent); }
        }
    }
}
