using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class CatchUpSemanticsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T10:00:00Z");
    private static readonly AccountBinding AccountA = new("fixture-account-a");
    private static readonly AccountBinding AccountB = new("fixture-account-b");
    private static ProviderResult Good(DateTimeOffset? at = null, double used = 0, DateTimeOffset? reset = null) =>
        new(new UsageSnapshot([new("codex/primary", "fixture", used, reset, 300)], at ?? Now, "fixture"))
        { VerifiedBinding = AccountA, Authentication = AuthenticationStatus.Verified };
    private static ProviderResult Failure(FailureKind kind, AccountBinding? account = null, TimeSpan? retry = null) =>
        ProviderResult.Fail(kind) with { VerifiedBinding = account,
            Authentication = account is null ? AuthenticationStatus.Unknown : AuthenticationStatus.Verified, RetryAfter = retry };
    private static ProviderState State(params UsageWindow[] windows) => new("Codex", ProviderStatus.Ready, new(windows, Now, "fixture"));

    [Theory]
    [InlineData(UsageScope.General, 300, null, "5-hour limit", "5-hour limit")]
    [InlineData(UsageScope.General, 10080, null, "Weekly limit", "Weekly limit")]
    [InlineData(UsageScope.Model, 10080, "Opus", "Opus", "Opus · Weekly model allowance")]
    [InlineData(UsageScope.Model, 10080, "Sonnet", "Sonnet", "Sonnet · Weekly model allowance")]
    [InlineData(UsageScope.Additional, 300, "Spark", "Spark limit", "Spark limit · 5-hour additional allowance")]
    [InlineData(UsageScope.Additional, 2880, "Research", "Research limit", "Research limit · 2-day additional allowance")]
    [InlineData(UsageScope.Additional, 60, "Fixture limit", "Fixture limit", "Fixture limit · 1-hour additional allowance")]
    [InlineData(UsageScope.Model, 10080, null, "Model limit", "Model limit · Weekly model allowance")]
    [InlineData(UsageScope.Additional, 300, " ", "Additional limit", "Additional limit · 5-hour additional allowance")]
    [InlineData(UsageScope.Unknown, 300, "internal fixture", "", "")]
    public void ScopeNamesAreConciseWhileAccessibilityPreservesPeriodAndScope(UsageScope scope, long minutes,
        string? name, string visible, string accessible)
    {
        var window = new UsageWindow("fixture", "raw-provider-label", 20, Now.AddHours(1), minutes, scope, name);
        Assert.Equal(visible, PopupText.WindowName("fixture provider", window));
        Assert.Equal(accessible, PopupText.AccessibleWindowName("fixture provider", window));
        Assert.DoesNotContain("raw-provider-label", accessible);
    }

    [Fact]
    public void MissingScopeNamesAndPeriodsRemainExplicitWithoutChangingCompactSelection()
    {
        var general = new UsageWindow("codex/primary", "raw", 100, Now.AddHours(1), 300);
        var model = new UsageWindow("model", "raw", 0, null, scope: UsageScope.Model);
        var additional = new UsageWindow("additional", "raw", null, null, scope: UsageScope.Additional);
        Assert.Equal("Model limit", PopupText.WindowName("Codex", model));
        Assert.Equal("Model limit · model allowance · Period not reported", PopupText.AccessibleWindowName("Codex", model));
        Assert.Equal("Additional limit", PopupText.WindowName("Codex", additional));
        Assert.Equal("Additional limit · additional allowance · Period not reported", PopupText.AccessibleWindowName("Codex", additional));
        Assert.Equal("Reset unavailable", PopupText.Reset(additional, Now));
        Assert.Same(general, UsagePresentation.Primary(State(additional, model, general)));
        Assert.Equal("0%", PopupText.Remaining(general));
        Assert.Null(UsagePresentation.Primary(State(additional, model)));
    }

    [Fact]
    public void CompactChoosesFiveHourThenWeeklyThenShortestAndNeverAdditionalOrModel()
    {
        var shortGeneral = new UsageWindow("other-duration", "fixture", 40, null, 60, UsageScope.General);
        var week = new UsageWindow("codex/secondary", "fixture", 30, null, 10080);
        var five = new UsageWindow("codex/primary", "fixture", 20, null, 300);
        var model = new UsageWindow("model:x", "fixture", 0, null, 300, UsageScope.Model, "X");
        var additional = new UsageWindow("spark/primary", "fixture", 0, null, 5, UsageScope.Additional, "Spark");
        Assert.Same(five, UsagePresentation.Primary(State(additional, model, shortGeneral, week, five)));
        Assert.Same(week, UsagePresentation.Primary(State(additional, shortGeneral, week)));
        Assert.Same(shortGeneral, UsagePresentation.Primary(State(model, shortGeneral, additional)));
        Assert.Null(UsagePresentation.Primary(State(model, additional)));
        Assert.Equal(5, UsagePresentation.Windows(State(model, shortGeneral, additional, week, five)).Length);
    }
    [Fact]
    public void UnknownOrMissingPercentOrDurationCannotReplaceUsableGeneral()
    {
        var week = new UsageWindow("codex/secondary", "fixture", 100, null, 10080);
        Assert.Same(week, UsagePresentation.Primary(State(new UsageWindow("codex/primary", "fixture", null, null, 300), week)));
        Assert.Null(UsagePresentation.Primary(State(new UsageWindow("codex/primary", "fixture", 0, null))));
        Assert.Null(UsagePresentation.Primary(State(new UsageWindow("opaque", "5-hour", 0, null, 300))));
        Assert.Equal(0, week.RemainingPercent);
    }
    [Fact]
    public void ConflictingIdentityIsExcludedAndEquivalentDuplicateAppearsOnce()
    {
        var first = new UsageWindow("codex/primary", "fixture", 20, null, 300);
        Assert.Single(UsagePresentation.Windows(State(first, first with { })));
        Assert.Empty(UsagePresentation.Windows(State(first, new("codex/primary", "fixture", 50, null, 300))));
    }
    [Theory]
    [InlineData(AllowanceAvailability.NotReported, "Not reported")]
    [InlineData(AllowanceAvailability.UnsupportedFormat, "Unsupported format")]
    [InlineData(AllowanceAvailability.UnsupportedBilling, "Unsupported billing")]
    public async Task SuccessfulMissingResponseReplacesPreviousObservation(AllowanceAvailability availability, string label)
    {
        var clock = new Clock(); var result = Good(); var provider = new Provider(_ => Task.FromResult(result));
        var c = new RefreshCoordinator([provider], clock: clock);
        await c.RefreshAsync(); clock.Advance(10);
        result = new(new UsageSnapshot([], clock.UtcNow, "fixture", Availability: availability))
            { VerifiedBinding = AccountA, Authentication = AuthenticationStatus.Verified };
        await c.RefreshAsync();
        Assert.Empty(c.States[0].Snapshot!.Windows);
        Assert.Equal(AuthenticationStatus.Verified, c.States[0].Authentication);
        Assert.Equal(label, PopupText.Status(c.States[0], clock.UtcNow));
    }
    [Theory]
    [InlineData(FailureKind.Network)] [InlineData(FailureKind.Malformed)] [InlineData(FailureKind.Timeout)]
    [InlineData(FailureKind.ProcessExited)] [InlineData(FailureKind.RateLimited)]
    public async Task StaleRetentionRequiresFreshSameAccountProof(FailureKind failure)
    {
        var clock = new Clock(); var result = Good(); var provider = new Provider(_ => Task.FromResult(result));
        var c = new RefreshCoordinator([provider], clock: clock);
        await c.RefreshAsync(); clock.Advance(10); result = Failure(failure, AccountA);
        await c.RefreshAsync();
        Assert.NotNull(c.States[0].Snapshot); Assert.Equal("Stale", PopupText.Status(c.States[0], clock.UtcNow));
        clock.Advance(900); result = Failure(failure, AccountB); await c.RefreshAsync();
        Assert.Null(c.States[0].Snapshot);
    }
    [Theory]
    [InlineData(FailureKind.Network)] [InlineData(FailureKind.Malformed)] [InlineData(FailureKind.LoggedOut)]
    [InlineData(FailureKind.NotInstalled)] [InlineData(FailureKind.AccountChanged)]
    public async Task UnverifiedFailureClearsReadinessAndAllowance(FailureKind failure)
    {
        var clock = new Clock(); var result = Good(); var c = new RefreshCoordinator([new Provider(_ => Task.FromResult(result))], clock: clock);
        await c.RefreshAsync(); clock.Advance(10); result = Failure(failure); await c.RefreshAsync();
        Assert.Null(c.States[0].Snapshot); Assert.NotEqual(AuthenticationStatus.Verified, c.States[0].Authentication);
    }
    [Fact]
    public async Task DisableCancelsAndFencesLateResultsEvenAfterReenable()
    {
        var release = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false; var clock = new Clock();
        var provider = new Provider(token => { token.Register(() => canceled = true); started.SetResult(); return release.Task; });
        var c = new RefreshCoordinator([provider], clock: clock);
        var work = c.RefreshAsync(); await started.Task;
        c.SetEnabled("Codex", false); await work;
        Assert.True(canceled); Assert.False(c.States[0].Enabled); Assert.Null(c.States[0].Snapshot);
        c.SetEnabled("Codex", true); clock.Advance(10);
        Assert.False(await c.RefreshAsync()); // owned uncooperative task still pending
        release.SetResult(Good()); await c.DrainAsync();
        Assert.Null(c.States[0].Snapshot); Assert.Equal(1, provider.Calls);
    }
    [Fact]
    public async Task DisabledProviderNeverQueriesAndBothOffCanResume()
    {
        var clock = new Clock(); var p = new Provider(_ => Task.FromResult(Good())); var c = new RefreshCoordinator([p], clock: clock);
        c.SetEnabled("Codex", false); Assert.False(await c.RefreshAsync()); Assert.Equal(0, p.Calls);
        Assert.Equal("Monitoring off", PopupText.Status(c.States[0], clock.UtcNow));
        c.SetEnabled("Codex", true); Assert.True(await c.RefreshAsync(reason: RefreshReason.Enable)); Assert.Equal(1, p.Calls);
    }
    [Fact]
    public async Task BackgroundBackoffIsExponentialCappedAndManualBypassesOnlyGenericCooldown()
    {
        var clock = new Clock(); var p = new Provider(_ => Task.FromResult(Failure(FailureKind.Network))); var c = new RefreshCoordinator([p], clock: clock);
        for (var i = 0; i < 8; i++)
        {
            Assert.True(await c.RefreshAsync(reason: RefreshReason.Background));
            var delay = Math.Min(900, 30 * (1 << i));
            clock.Advance(delay - 1); Assert.False(await c.RefreshAsync(reason: RefreshReason.Background));
            clock.Advance(1);
        }
        Assert.True(await c.RefreshAsync()); clock.Advance(9); Assert.False(await c.RefreshAsync());
        clock.Advance(1); Assert.True(await c.RefreshAsync());
    }
    [Fact]
    public async Task RateEmbargoSurvivesToggleSleepClockJumpAndExplicitRetry()
    {
        var clock = new Clock(); var result = Failure(FailureKind.RateLimited, AccountA, TimeSpan.FromMinutes(2));
        var p = new Provider(_ => Task.FromResult(result)); var c = new RefreshCoordinator([p], clock: clock);
        await c.RefreshAsync();
        Assert.Equal(clock.UtcNow.AddMinutes(2), c.States[0].RetryAt);
        c.SetEnabled("Codex", false); c.SetEnabled("Codex", true); c.Suspend(); c.Resume();
        clock.UtcNow = Now.AddDays(1); Assert.False(await c.RefreshAsync());
        clock.Advance(119); Assert.False(await c.RefreshAsync(reason: RefreshReason.Enable));
        clock.Advance(1); result = Good(clock.UtcNow); Assert.True(await c.RefreshAsync()); Assert.Equal(2, p.Calls);
    }
    [Fact]
    public async Task ResetRefreshHasThirtySecondFloorAndDoesNotInventRefill()
    {
        var clock = new Clock(); var result = Good(reset: Now.AddSeconds(1), used: 100);
        var p = new Provider(_ => Task.FromResult(result)); var c = new RefreshCoordinator([p], clock: clock);
        await c.RefreshAsync(); clock.Advance(10); await c.RefreshAsync(reason: RefreshReason.Background);
        Assert.Equal(2, p.Calls); Assert.Equal(0, c.States[0].Snapshot!.Windows[0].RemainingPercent);
        clock.Advance(29); Assert.False(await c.RefreshAsync(reason: RefreshReason.Background));
        clock.Advance(1); Assert.True(await c.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal("Stale", PopupText.Status(c.States[0], clock.UtcNow));
    }
    [Fact]
    public async Task ResetCannotBypassFailureBackoff()
    {
        var clock = new Clock(); var result = Good(reset: Now.AddSeconds(1));
        var c = new RefreshCoordinator([new Provider(_ => Task.FromResult(result))], clock: clock);
        await c.RefreshAsync(); clock.Advance(10); result = Failure(FailureKind.Network, AccountA); await c.RefreshAsync();
        clock.Advance(10); Assert.False(await c.RefreshAsync(reason: RefreshReason.Reset));
        clock.Advance(20); Assert.True(await c.RefreshAsync(reason: RefreshReason.Background));
    }
    [Fact]
    public async Task SuspendCancelsPendingReadAndResumeDoesNotPublishIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new Provider(async token => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return Good(); });
        var c = new RefreshCoordinator([p]); var work = c.RefreshAsync(); await started.Task;
        c.Suspend(); await work; Assert.False(await c.RefreshAsync()); Assert.Null(c.States[0].Snapshot);
        c.Resume(); Assert.Null(c.States[0].Snapshot);
    }
    [Fact]
    public void AccountEvidenceNeverEntersSerializationOrToString()
    {
        var result = Good(); var state = State(new UsageWindow("codex/primary", "fixture", 10, null, 300)) with
            { Snapshot = Good().Snapshot! with { Binding = AccountA } };
        var text = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(state) + result + AccountA + state;
        Assert.DoesNotContain("fixture-account-a", text);
    }
    [Fact]
    public void InconsistentAvailabilityCannotClaimLiveForEmptyUnknownOrAmbiguousData()
    {
        var state = new ProviderState("Codex", ProviderStatus.Ready, new([], Now, "fixture", Availability: AllowanceAvailability.Reported));
        Assert.Equal("Not reported", PopupText.Status(state, Now));
        state = state with { Snapshot = new([new("opaque", "5-hour", 0, null, 300)], Now, "fixture", Availability: AllowanceAvailability.Reported) };
        Assert.Equal("Unsupported format", PopupText.Status(state, Now));
        state = State(new("codex/primary", "fixture", 20, null, 300), new("codex/primary", "fixture", 40, null, 300)) with
            { Snapshot = new([new("codex/primary", "fixture", 20, null, 300), new("codex/primary", "fixture", 40, null, 300)], Now, "fixture", Availability: AllowanceAvailability.Reported) };
        Assert.Equal("Unsupported format", PopupText.Status(state, Now));
        state = State(new UsageWindow("codex/primary", "fixture", 0, null, 300)) with
            { Snapshot = new([new("codex/primary", "fixture", 0, null, 300)], Now, "fixture", Availability: AllowanceAvailability.UnsupportedFormat) };
        Assert.Equal("Live", PopupText.Status(state, Now));
    }
    [Fact]
    public async Task RateWaitRemainsIndependentWhenAccountVerificationFails()
    {
        var clock = new Clock(); var result = Failure(FailureKind.AccountChanged, retry: TimeSpan.FromMinutes(2));
        var c = new RefreshCoordinator([new Provider(_ => Task.FromResult(result))], clock: clock);
        await c.RefreshAsync(); Assert.Null(c.States[0].Snapshot); Assert.Equal(AuthenticationStatus.Unknown, c.States[0].Authentication);
        clock.Advance(119); Assert.False(await c.RefreshAsync());
        clock.Advance(1); result = Good(clock.UtcNow); Assert.True(await c.RefreshAsync());
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
    private sealed class Provider(Func<CancellationToken, Task<ProviderResult>> query) : IUsageProvider
    {
        public string Name => "Codex";
        public int Calls { get; private set; }
        public Task<ProviderResult> QueryAsync(CancellationToken token) { ++Calls; return query(token); }
    }
}
