using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ScopedRetryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T10:00:00Z");
    private static ProviderResult Good(Clock clock) => new(new UsageSnapshot(
        [new("codex/primary", "fixture", 20, null, 300)], clock.UtcNow, "fixture"));

    [Fact]
    public async Task ProviderScopeQueriesOnlyItsExactEnabledMatch()
    {
        var clock = new Clock();
        var codex = new Provider("Codex", _ => Task.FromResult(Good(clock)));
        var claude = new Provider("Claude", _ => Task.FromResult(Good(clock)));
        var coordinator = new RefreshCoordinator([codex, claude], clock: clock);

        Assert.True(await coordinator.RefreshAsync(providerName: "Codex"));
        Assert.Equal(1, codex.Calls); Assert.Equal(0, claude.Calls);
        foreach (var invalid in new[] { "codex", "Claude Code", "Missing", "", " Codex" })
            Assert.False(await coordinator.RefreshAsync(providerName: invalid));
        coordinator.SetEnabled("Claude", false);
        Assert.False(await coordinator.RefreshAsync(providerName: "Claude"));
        Assert.Equal(1, codex.Calls); Assert.Equal(0, claude.Calls);

        clock.Advance(10); coordinator.SetEnabled("Claude", true);
        Assert.True(await coordinator.RefreshAsync());
        Assert.Equal(2, codex.Calls); Assert.Equal(1, claude.Calls);
    }

    [Fact]
    public async Task ScopedRefreshKeepsSingleFlightAndOtherProvidersIndependent()
    {
        var clock = new Clock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codex = new Provider("Codex", _ => { started.TrySetResult(); return release.Task; });
        var claude = new Provider("Claude", _ => Task.FromResult(Good(clock)));
        var coordinator = new RefreshCoordinator([codex, claude], clock: clock);
        var pending = coordinator.RefreshAsync(providerName: "Codex");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Advance(10);
            Assert.False(await coordinator.RefreshAsync(providerName: "Codex"));
            Assert.True(await coordinator.RefreshAsync(providerName: "Claude"));
            Assert.Equal(1, codex.Calls); Assert.Equal(1, claude.Calls);
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(Good(clock)); }
        Assert.True(await pending);
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task ScopedManualRefreshHonorsOnlyTheSelectedProvidersMinimum()
    {
        var clock = new Clock();
        var codex = new Provider("Codex", _ => Task.FromResult(Good(clock)));
        var claude = new Provider("Claude", _ => Task.FromResult(Good(clock)));
        var coordinator = new RefreshCoordinator([codex, claude], clock: clock);
        Assert.True(await coordinator.RefreshAsync(providerName: "Codex"));
        clock.Advance(9);
        Assert.False(await coordinator.RefreshAsync(providerName: "Codex"));
        Assert.True(await coordinator.RefreshAsync(providerName: "Claude"));
        clock.Advance(1);
        Assert.True(await coordinator.RefreshAsync(providerName: "Codex"));
        Assert.False(await coordinator.RefreshAsync(providerName: "Claude"));
        Assert.Equal(2, codex.Calls); Assert.Equal(1, claude.Calls);
    }

    [Fact]
    public async Task ScopedRateEmbargoCannotBeBypassedAndDoesNotBlockAnotherProvider()
    {
        var clock = new Clock(); var result = ProviderResult.Fail(FailureKind.RateLimited) with { RetryAfter = TimeSpan.FromSeconds(90) };
        var codex = new Provider("Codex", _ => Task.FromResult(result));
        var claude = new Provider("Claude", _ => Task.FromResult(Good(clock)));
        var coordinator = new RefreshCoordinator([codex, claude], clock: clock);
        await coordinator.RefreshAsync(providerName: "Codex");
        Assert.Equal(Now.AddSeconds(90), coordinator.States[0].AutomaticRetryAt);
        clock.Advance(89);
        Assert.False(await coordinator.RefreshAsync(providerName: "Codex"));
        Assert.False(await coordinator.RefreshAsync(reason: RefreshReason.Background, providerName: "Codex"));
        Assert.True(await coordinator.RefreshAsync(providerName: "Claude"));
        Assert.Equal(1, codex.Calls);
        clock.Advance(1); result = Good(clock);
        Assert.True(await coordinator.RefreshAsync(providerName: "Codex"));
        Assert.Equal(Now.AddSeconds(100), coordinator.States[0].RetryAt);
        Assert.Null(coordinator.States[0].AutomaticRetryAt);
    }

    [Fact]
    public async Task AutomaticFailureWaitUsesMonotonicTimeAndExpiresWithoutChangingScheduling()
    {
        var clock = new Clock(); var result = ProviderResult.Fail(FailureKind.Network);
        var provider = new Provider("Codex", _ => Task.FromResult(result));
        var coordinator = new RefreshCoordinator([provider], clock: clock);
        await coordinator.RefreshAsync(providerName: "Codex");
        Assert.Equal(Now.AddSeconds(10), coordinator.States[0].RetryAt);
        Assert.Equal(Now.AddSeconds(30), coordinator.States[0].AutomaticRetryAt);
        clock.Advance(10);
        Assert.Null(coordinator.States[0].RetryAt);
        Assert.Equal(Now.AddSeconds(30), coordinator.States[0].AutomaticRetryAt);

        clock.UtcNow += TimeSpan.FromDays(1);
        Assert.Equal(clock.UtcNow.AddSeconds(20), coordinator.States[0].AutomaticRetryAt);
        Assert.False(await coordinator.RefreshAsync(reason: RefreshReason.Background, providerName: "Codex"));
        clock.Advance(19);
        Assert.Equal(clock.UtcNow.AddSeconds(1), coordinator.States[0].AutomaticRetryAt);
        Assert.False(await coordinator.RefreshAsync(reason: RefreshReason.Background, providerName: "Codex"));
        clock.Advance(1);
        Assert.Null(coordinator.States[0].AutomaticRetryAt);
        result = Good(clock);
        Assert.True(await coordinator.RefreshAsync(reason: RefreshReason.Background, providerName: "Codex"));
        Assert.Null(coordinator.States[0].AutomaticRetryAt); // Normal successful polling has no failure countdown.
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task AutomaticWaitIncludesLongerMinimumAndSeparatesShortEmbargoFromBackoff()
    {
        var clock = new Clock();
        var provider = new Provider("Codex", _ => Task.FromResult(ProviderResult.Fail(FailureKind.Network)));
        var coordinator = new RefreshCoordinator([provider], clock: clock, minimumInterval: TimeSpan.FromSeconds(60));
        await coordinator.RefreshAsync();
        Assert.Equal(Now.AddSeconds(60), coordinator.States[0].RetryAt);
        Assert.Equal(Now.AddSeconds(60), coordinator.States[0].AutomaticRetryAt);

        var otherClock = new Clock(); var result = ProviderResult.Fail(FailureKind.Network);
        var other = new RefreshCoordinator([new Provider("Codex", _ => Task.FromResult(result))], clock: otherClock);
        await other.RefreshAsync(); otherClock.Advance(10);
        result = ProviderResult.Fail(FailureKind.RateLimited) with { RetryAfter = TimeSpan.FromSeconds(15) };
        Assert.True(await other.RefreshAsync(providerName: "Codex"));
        Assert.Equal(Now.AddSeconds(25), other.States[0].RetryAt);
        Assert.Equal(Now.AddSeconds(70), other.States[0].AutomaticRetryAt);
        otherClock.Advance(15);
        Assert.Null(other.States[0].RetryAt);
        Assert.False(await other.RefreshAsync(reason: RefreshReason.Background, providerName: "Codex"));
    }

    [Fact]
    public async Task ManualBypassHidesWaitDuringQueryAndSuccessClearsFailureCountdown()
    {
        var clock = new Clock(); var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider("Codex", _ =>
        {
            if (++calls == 1) return Task.FromResult(ProviderResult.Fail(FailureKind.Network));
            started.TrySetResult(); return release.Task;
        });
        var coordinator = new RefreshCoordinator([provider], clock: clock);
        await coordinator.RefreshAsync(); clock.Advance(10);
        Assert.Equal(Now.AddSeconds(30), coordinator.States[0].AutomaticRetryAt);
        var retry = coordinator.RefreshAsync(providerName: "Codex");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(coordinator.States[0].AutomaticRetryAt);
        }
        finally { release.TrySetResult(Good(clock)); }
        Assert.True(await retry);
        Assert.Null(coordinator.States[0].AutomaticRetryAt);
        clock.Advance(10);
        Assert.False(await coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Null(coordinator.States[0].AutomaticRetryAt);
    }

    [Fact]
    public async Task AutomaticCountdownIsHiddenWhileSuspendedOrDisabled()
    {
        var clock = new Clock(); var result = ProviderResult.Fail(FailureKind.Network);
        var provider = new Provider("Codex", _ => Task.FromResult(result));
        var coordinator = new RefreshCoordinator([provider], clock: clock);
        await coordinator.RefreshAsync();
        coordinator.Suspend(); Assert.Null(coordinator.States[0].AutomaticRetryAt);
        Assert.False(await coordinator.RefreshAsync(providerName: "Codex"));
        clock.Advance(5); coordinator.Resume();
        Assert.Equal(Now.AddSeconds(30), coordinator.States[0].AutomaticRetryAt);
        coordinator.SetEnabled("Codex", false); Assert.Null(coordinator.States[0].AutomaticRetryAt);
        coordinator.SetEnabled("Codex", true); Assert.Null(coordinator.States[0].AutomaticRetryAt);
        Assert.False(await coordinator.RefreshAsync(reason: RefreshReason.Enable, providerName: "Codex"));
        clock.Advance(5); result = Good(clock);
        Assert.True(await coordinator.RefreshAsync(reason: RefreshReason.Enable, providerName: "Codex"));
        Assert.Null(coordinator.States[0].AutomaticRetryAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReenableDoesNotClaimCheckingWhileMinimumOrRateEmbargoBlocksTheQuery(bool rateLimited)
    {
        var clock = new Clock();
        var result = rateLimited
            ? ProviderResult.Fail(FailureKind.RateLimited) with { RetryAfter = TimeSpan.FromSeconds(90) }
            : Good(clock);
        var provider = new Provider("Codex", _ => Task.FromResult(result));
        var coordinator = new RefreshCoordinator([provider], clock: clock);
        await coordinator.RefreshAsync();
        coordinator.SetEnabled("Codex", false); coordinator.SetEnabled("Codex", true);
        var state = coordinator.States[0];
        Assert.True(state.Enabled); Assert.Equal(ProviderStatus.Unavailable, state.Status);
        Assert.Equal(rateLimited ? FailureKind.RateLimited : FailureKind.None, state.Failure);
        Assert.Equal(AuthenticationStatus.Unknown, state.Authentication); Assert.Null(state.Snapshot);
        Assert.Equal(Now.AddSeconds(rateLimited ? 90 : 10), state.RetryAt);
        Assert.Null(state.AutomaticRetryAt);
        Assert.False(await coordinator.RefreshAsync(reason: RefreshReason.Enable, providerName: "Codex"));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(ProviderStatus.Unavailable, coordinator.States[0].Status);
        clock.Advance(rateLimited ? 90 : 10); result = Good(clock);
        Assert.True(await coordinator.RefreshAsync(reason: RefreshReason.Enable, providerName: "Codex"));
        Assert.Equal(2, provider.Calls); Assert.Equal(ProviderStatus.Ready, coordinator.States[0].Status);
    }

    [Fact]
    public async Task CancelledManualRetryDoesNotAdvertiseAutomaticQueryWhileOwnedTaskStillRuns()
    {
        var clock = new Clock(); var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider("Codex", _ =>
        {
            if (++calls == 1) return Task.FromResult(ProviderResult.Fail(FailureKind.Network));
            started.TrySetResult(); return release.Task;
        });
        var coordinator = new RefreshCoordinator([provider], clock: clock);
        await coordinator.RefreshAsync(); clock.Advance(10);
        using var cancellation = new CancellationTokenSource();
        var retry = coordinator.RefreshAsync(cancellation.Token, providerName: "Codex");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel(); await retry;
            Assert.Null(coordinator.States[0].AutomaticRetryAt);
            Assert.False(await coordinator.RefreshAsync(reason: RefreshReason.Background, providerName: "Codex"));
        }
        finally { release.TrySetResult(Good(clock)); }
        await coordinator.DrainAsync();
        Assert.Equal(Now.AddSeconds(30), coordinator.States[0].AutomaticRetryAt);
        Assert.Equal(FailureKind.Network, coordinator.States[0].Failure);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => 1;
        public void Advance(int seconds) { timestamp += seconds; UtcNow += TimeSpan.FromSeconds(seconds); }
    }
    private sealed class Provider(string name, Func<CancellationToken, Task<ProviderResult>> query) : IUsageProvider
    {
        private int calls;
        public string Name => name;
        public int Calls => Volatile.Read(ref calls);
        public Task<ProviderResult> QueryAsync(CancellationToken token) { Interlocked.Increment(ref calls); return query(token); }
    }
}
