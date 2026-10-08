using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class CoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 13, 0, 0, TimeSpan.Zero);
    private static UsageSnapshot Snapshot(bool cached = false) => new([new("weekly", "Weekly", 43, Now.AddDays(2), 10080, UsageScope.General)], Now, "test", cached);

    [Theory]
    [InlineData(-1)] [InlineData(101)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidPercentNeverBecomesAnAllowance(double input)
    {
        var window = new UsageWindow("id", "Window", input, null);
        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
    }

    [Theory]
    [InlineData(0, 100)] [InlineData(100, 0)] [InlineData(43, 57)] [InlineData(2.5, 97.5)]
    public void RemainingUsesCorrectDirection(double used, double expected) =>
        Assert.Equal(expected, new UsageWindow("id", "Window", used, null).RemainingPercent);

    [Fact]
    public void UnknownPercentageAndResetStayUnknown()
    {
        var window = new UsageWindow("id", "Window", null, null);
        Assert.Null(window.RemainingPercent);
        Assert.Equal("Unavailable", UsageText.Reset(null, Now));
    }

    [Fact]
    public void ResetUsesAbsoluteInstantAndDoesNotInventRecovery()
    {
        var sameInstant = Now.ToOffset(TimeSpan.FromHours(5.5));
        Assert.True(new UsageWindow("id", "Window", 100, sameInstant).ResetPassed(Now));
        Assert.Contains("awaiting provider update", UsageText.Reset(sameInstant, Now));
        Assert.Contains("in 1m", UsageText.Reset(Now.AddSeconds(1), Now));
        Assert.Contains("in 1h 1m", UsageText.Reset(Now.AddMinutes(60).AddSeconds(1), Now));
    }

    [Fact]
    public void CacheAgeComesFromObservationNotReadTime()
    {
        var state = new ProviderState("Test", ProviderStatus.Ready, Snapshot(), LastRefresh: Now.AddHours(1));
        Assert.True(state.IsStale(Now.AddHours(1)));
        Assert.False(state.IsStale(Now));
        Assert.True((state with { Snapshot = Snapshot(true) }).IsStale(Now));
        Assert.True(state.IsStale(Now.AddHours(-1)));
    }

    [Fact]
    public void FailedProviderRetainsExplicitlyStaleValues()
    {
        var state = new ProviderState("Test", ProviderStatus.Error, Snapshot(), FailureKind.Network);
        var text = UsageText.Provider(state, Now);
        Assert.Contains("Stale", text);
        Assert.Contains("network", text);
        Assert.Contains("57% remaining (stale)", text);
    }

    [Fact]
    public void ExpiredResetMakesHeaderAndTooltipStale()
    {
        var snapshot = new UsageSnapshot([new("id", "Weekly", 100, Now, 10080, UsageScope.General)], Now, "test");
        var state = new ProviderState("Test", ProviderStatus.Ready, snapshot);
        Assert.StartsWith("Stale", UsageText.Provider(state, Now));
        Assert.Contains("stale", UsageText.Tooltip([state], Now));
        Assert.DoesNotContain("live", UsageText.Tooltip([state], Now));
    }

    [Fact]
    public async Task OneProviderFailureDoesNotBlockAnother()
    {
        var failing = new FakeProvider("Bad", _ => throw new IOException("sensitive raw exception"));
        var good = new FakeProvider("Good", _ => Task.FromResult(new ProviderResult(Snapshot())));
        var coordinator = new RefreshCoordinator([failing, good], clock: new FixedClock(Now));
        Assert.True(await coordinator.RefreshAsync());
        Assert.Equal(ProviderStatus.Error, coordinator.States[0].Status);
        Assert.Equal(FailureKind.Unexpected, coordinator.States[0].Failure);
        Assert.Null(coordinator.States[0].Detail);
        Assert.Equal(ProviderStatus.Ready, coordinator.States[1].Status);
    }

    [Fact]
    public async Task SuccessfulProviderIsVisibleBeforeSlowProviderFinishes()
    {
        var pending = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RefreshCoordinator([new FakeProvider("Slow", _ => pending.Task),
            new FakeProvider("Fast", _ => Task.FromResult(new ProviderResult(Snapshot())))], clock: new FixedClock(Now));
        coordinator.Changed += () => { if (coordinator.States[1].Status == ProviderStatus.Ready) ready.TrySetResult(); };
        var refresh = coordinator.RefreshAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(refresh.IsCompleted);
        pending.SetResult(ProviderResult.Fail(FailureKind.Network));
        await refresh;
    }

    [Fact]
    public async Task SimultaneousRefreshIsCoalesced()
    {
        var pending = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider("Test", _ => pending.Task);
        var coordinator = new RefreshCoordinator([provider]);
        var first = coordinator.RefreshAsync();
        Assert.False(await coordinator.RefreshAsync());
        pending.SetResult(new ProviderResult(Snapshot()));
        Assert.True(await first);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task RepeatedManualRefreshHonorsCooldown()
    {
        var provider = new FakeProvider("Test", _ => Task.FromResult(new ProviderResult(Snapshot())));
        var coordinator = new RefreshCoordinator([provider], clock: new FixedClock(Now));
        await coordinator.RefreshAsync();
        Assert.False(await coordinator.RefreshAsync());
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task TimeoutIsIsolatedAndUncooperativeProviderNeverOverlaps()
    {
        var pending = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider("Hung", _ => pending.Task);
        var coordinator = new RefreshCoordinator([provider], timeout: TimeSpan.FromMilliseconds(150), minimumInterval: TimeSpan.Zero);
        await coordinator.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(FailureKind.Timeout, coordinator.States[0].Failure);
        await coordinator.RefreshAsync();
        Assert.Equal(1, provider.Calls);
        pending.SetResult(new ProviderResult(Snapshot()));
        Assert.Null(coordinator.States[0].Snapshot); // late result cannot silently replace timeout state
    }

    [Fact]
    public async Task FailureKeepsLastSuccessAndLogoutClearsIt()
    {
        var next = new ProviderResult(Snapshot()) { VerifiedBinding = new("fixture"), Authentication = AuthenticationStatus.Verified };
        var coordinator = new RefreshCoordinator([new FakeProvider("Test", _ => Task.FromResult(next))], minimumInterval: TimeSpan.Zero);
        await coordinator.RefreshAsync();
        next = ProviderResult.Fail(FailureKind.Network) with { VerifiedBinding = new("fixture"), Authentication = AuthenticationStatus.Verified };
        await coordinator.RefreshAsync();
        Assert.NotNull(coordinator.States[0].Snapshot);
        Assert.True(coordinator.States[0].IsStale(Now));
        next = ProviderResult.Fail(FailureKind.LoggedOut);
        await coordinator.RefreshAsync();
        Assert.Null(coordinator.States[0].Snapshot);
    }

    [Fact]
    public async Task RestartBeginsUnknownAndDoesNotLoadOldNumbers()
    {
        var provider = new FakeProvider("Test", _ => Task.FromResult(new ProviderResult(Snapshot())));
        var original = new RefreshCoordinator([provider]);
        await original.RefreshAsync();
        var restarted = new RefreshCoordinator([provider]);
        Assert.Null(restarted.States[0].Snapshot);
        Assert.Equal(ProviderStatus.Loading, restarted.States[0].Status);
    }

    [Fact]
    public async Task AmbiguousAccountInvalidatesPriorNumbers()
    {
        var next = new ProviderResult(Snapshot(true));
        var coordinator = new RefreshCoordinator([new FakeProvider("Test", _ => Task.FromResult(next))], minimumInterval: TimeSpan.Zero);
        await coordinator.RefreshAsync();
        next = ProviderResult.Fail(FailureKind.Unsupported, "Account is ambiguous");
        await coordinator.RefreshAsync();
        Assert.Null(coordinator.States[0].Snapshot);
    }

    [Fact]
    public async Task CancellationStopsRefreshPromptly()
    {
        var provider = new FakeProvider("Test", async ct => { await Task.Delay(Timeout.Infinite, ct); return new ProviderResult(Snapshot()); });
        var coordinator = new RefreshCoordinator([provider]);
        using var cancel = new CancellationTokenSource();
        var task = coordinator.RefreshAsync(cancel.Token);
        await cancel.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(coordinator.IsRefreshing);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class FakeProvider(string name, Func<CancellationToken, Task<ProviderResult>> query) : IUsageProvider
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public string Name => name;
        public Task<ProviderResult> QueryAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref calls); return query(cancellationToken); }
    }
}
