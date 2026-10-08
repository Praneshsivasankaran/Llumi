using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ResetSchedulingTests
{
    private static readonly AccountBinding Account = new("synthetic-account");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ResetPassingDuringQueryKeepsOneFollowUpEvenWhenResponseMovesResetIntoFuture(bool pollWhileActive)
    {
        var fixture = new Fixture();
        await fixture.Seed();
        fixture.Clock.Advance(10);
        var held = fixture.Provider.HoldNext();
        var query = fixture.Coordinator.RefreshAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            fixture.Clock.Advance(6);
            if (pollWhileActive)
                for (var index = 0; index < 20; index++)
                    Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        }
        finally { held.Release(fixture.Good()); }
        await query;

        Assert.Equal(2, fixture.Provider.Calls);
        Assert.Equal(60, fixture.Coordinator.States[0].Snapshot!.Windows[0].RemainingPercent);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        fixture.Clock.Advance(3);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        fixture.Clock.Advance(1);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(3, fixture.Provider.Calls);
        Assert.Equal(1, fixture.Provider.MaximumConcurrent);
        fixture.Clock.Advance(10);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(3, fixture.Provider.Calls);
    }

    [Fact]
    public async Task PendingResetAlsoHonorsThirtySecondResetCadence()
    {
        var fixture = new Fixture();
        await fixture.Seed(resetSeconds: 1);
        fixture.Clock.Advance(10);
        fixture.Provider.Next = _ => Task.FromResult(fixture.Good(fixture.Clock.GetUtcNow().AddSeconds(15)));
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        fixture.Clock.Advance(10);
        var held = fixture.Provider.HoldNext();
        var query = fixture.Coordinator.RefreshAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(6);
        held.Release(fixture.Good()); await query;
        fixture.Clock.Advance(13); // Manual floor has passed; previous reset query was only 29 seconds ago.
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        fixture.Clock.Advance(1);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(4, fixture.Provider.Calls); Assert.Equal(1, fixture.Provider.MaximumConcurrent);
    }

    [Theory]
    [InlineData(false, 30)] [InlineData(true, 90)]
    public async Task PendingResetCannotShortenFailureBackoffOrRateEmbargo(bool rateLimited, int wait)
    {
        var fixture = new Fixture(); await fixture.Seed(); fixture.Clock.Advance(10);
        var held = fixture.Provider.HoldNext(); var query = fixture.Coordinator.RefreshAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(6);
        held.Release(ProviderResult.Fail(rateLimited ? FailureKind.RateLimited : FailureKind.Network) with
        {
            Authentication = AuthenticationStatus.Verified, VerifiedBinding = Account,
            RetryAfter = rateLimited ? TimeSpan.FromSeconds(wait) : null
        });
        await query;
        Assert.Equal("Stale", PopupText.Status(fixture.Coordinator.States[0], fixture.Clock.GetUtcNow()));
        fixture.Clock.Advance(wait - 1);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Reset));
        if (rateLimited) Assert.False(await fixture.Coordinator.RefreshAsync());
        fixture.Clock.Advance(1);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(3, fixture.Provider.Calls); Assert.Equal(1, fixture.Provider.MaximumConcurrent);
        fixture.Clock.Advance(10);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
    }

    [Theory]
    [InlineData("empty")] [InlineData("changed")] [InlineData("unverified")]
    public async Task EmptyOrDiscontinuousObservationDoesNotCarryPreviousAccountsResetIntent(string resultKind)
    {
        var fixture = new Fixture(); await fixture.Seed(); fixture.Clock.Advance(10);
        var held = fixture.Provider.HoldNext(); var query = fixture.Coordinator.RefreshAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(6);
        var result = resultKind switch
        {
            "empty" => fixture.Good() with { Snapshot = new([], fixture.Clock.GetUtcNow(), "synthetic", Availability: AllowanceAvailability.NotReported) },
            "changed" => fixture.Good() with { VerifiedBinding = new("different-synthetic-account") },
            _ => fixture.Good() with { Authentication = AuthenticationStatus.Unknown, VerifiedBinding = null }
        };
        held.Release(result); await query;
        fixture.Clock.Advance(10);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(2, fixture.Provider.Calls);
        if (resultKind == "empty") Assert.Equal("Not reported", PopupText.Status(fixture.Coordinator.States[0], fixture.Clock.GetUtcNow()));
        if (resultKind == "changed") Assert.Equal(result.VerifiedBinding, fixture.Coordinator.States[0].Snapshot!.Binding);
        if (resultKind == "unverified") Assert.Equal(AuthenticationStatus.Unknown, fixture.Coordinator.States[0].Authentication);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DisableOrSuspendRejectsResetFromLateUncooperativeResult(bool suspend)
    {
        var fixture = new Fixture(); await fixture.Seed(); fixture.Clock.Advance(10);
        var held = fixture.Provider.HoldNext(ignoreCancellation: true); var query = fixture.Coordinator.RefreshAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(6);
        if (suspend) fixture.Coordinator.Suspend(); else fixture.Coordinator.SetEnabled("Codex", false);
        await query;
        if (suspend) fixture.Coordinator.Resume(); else fixture.Coordinator.SetEnabled("Codex", true);
        fixture.Clock.Advance(4);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Enable));
        held.Release(fixture.Good()); await fixture.Coordinator.DrainAsync();
        Assert.Equal(2, fixture.Provider.Calls);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Enable));
        fixture.Clock.Advance(10);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(3, fixture.Provider.Calls); Assert.Equal(1, fixture.Provider.MaximumConcurrent);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPublishLateResetIntent()
    {
        var fixture = new Fixture(); await fixture.Seed(); fixture.Clock.Advance(10);
        var held = fixture.Provider.HoldNext(ignoreCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var query = fixture.Coordinator.RefreshAsync(cancellation.Token);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(6); cancellation.Cancel(); await query;
        held.Release(fixture.Good()); await fixture.Coordinator.DrainAsync();
        fixture.Clock.Advance(4);
        Assert.True(await fixture.Coordinator.RefreshAsync());
        fixture.Clock.Advance(10);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(3, fixture.Provider.Calls); Assert.Equal(1, fixture.Provider.MaximumConcurrent);
    }

    [Fact]
    public async Task StoppingAfterResetWasQueuedClearsFollowUpBeforeResume()
    {
        var fixture = new Fixture(); await fixture.Seed(); fixture.Clock.Advance(10);
        var held = fixture.Provider.HoldNext(); var query = fixture.Coordinator.RefreshAsync();
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(6); held.Release(fixture.Good()); await query;
        fixture.Coordinator.Suspend(); fixture.Coordinator.Resume(); fixture.Clock.Advance(10);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(2, fixture.Provider.Calls);
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public Provider Provider { get; }
        public RefreshCoordinator Coordinator { get; }
        public Fixture()
        {
            Provider = new(() => Good()); Coordinator = new([Provider], clock: Clock);
        }
        public ProviderResult Good(DateTimeOffset? reset = null) => new(new UsageSnapshot(
            [new("codex/primary", "synthetic", 40, reset ?? Clock.GetUtcNow().AddHours(2), 300)], Clock.GetUtcNow(), "synthetic"))
            { Authentication = AuthenticationStatus.Verified, VerifiedBinding = Account };
        public async Task Seed(int resetSeconds = 15)
        {
            Provider.Next = _ => Task.FromResult(Good(Clock.GetUtcNow().AddSeconds(resetSeconds)));
            Assert.True(await Coordinator.RefreshAsync());
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => 1;
        public void Advance(int seconds) { now = now.AddSeconds(seconds); timestamp += seconds; }
    }
    private sealed class Held(bool ignoreCancellation)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProviderResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProviderResult> Query(CancellationToken token)
        { Started.TrySetResult(); return ignoreCancellation ? result.Task : result.Task.WaitAsync(token); }
        public void Release(ProviderResult value) => result.TrySetResult(value);
    }
    private sealed class Provider(Func<ProviderResult> fallback) : IUsageProvider
    {
        public string Name => "Codex";
        public Func<CancellationToken, Task<ProviderResult>>? Next;
        public int Calls { get; private set; }
        public int MaximumConcurrent { get; private set; }
        private int concurrent;
        public Held HoldNext(bool ignoreCancellation = false)
        { var held = new Held(ignoreCancellation); Next = held.Query; return held; }
        public async Task<ProviderResult> QueryAsync(CancellationToken token)
        {
            Calls++; var active = Interlocked.Increment(ref concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            var query = Next; Next = null;
            try { return query is null ? fallback() : await query(token); }
            finally { Interlocked.Decrement(ref concurrent); }
        }
    }
}
