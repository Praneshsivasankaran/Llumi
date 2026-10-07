using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudeRetrievalFailureTests
{
    private const string Auth = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"fixture@example.invalid","orgId":"00000000-0000-0000-0000-000000000042","orgName":"Fixture","subscriptionType":"pro","analyticsDisabled":true}""";
    private const string GoodLimits = """{"five_hour":{"utilization":40,"resets_at":"2030-01-01T01:00:00Z"}}""";

    [Fact]
    public async Task SupportedNullRetrievalRetainsSameAccountObservationBacksOffAndRecovers()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Coordinator.RefreshAsync());
        var original = Assert.IsType<UsageSnapshot>(fixture.State.Snapshot);
        Assert.Equal("Live", fixture.Status);
        Assert.Equal(60, Assert.Single(original.Windows).RemainingPercent);

        fixture.Response = Payload(true, "null"); fixture.Clock.Advance(30);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(FailureKind.Network, fixture.State.Failure);
        Assert.Equal(AuthenticationStatus.Verified, fixture.State.Authentication);
        Assert.Equal("Stale", fixture.Status);
        Assert.Same(original, fixture.State.Snapshot);
        Assert.Equal(original.ObservedAt, fixture.State.Snapshot!.ObservedAt);
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.State.LastRefresh);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(30), fixture.State.AutomaticRetryAt);
        Assert.Equal(2, fixture.UsageCalls); Assert.Equal(4, fixture.AuthCalls);

        fixture.Clock.Advance(29);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(2, fixture.UsageCalls);
        fixture.Clock.Advance(1);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Same(original, fixture.State.Snapshot);
        Assert.Equal("Stale", fixture.Status);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(60), fixture.State.AutomaticRetryAt);
        fixture.Clock.Advance(59);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(3, fixture.UsageCalls);

        fixture.Response = Payload(true, GoodLimits.Replace(":40", ":10")); fixture.Clock.Advance(1);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal("Live", fixture.Status);
        Assert.Equal(FailureKind.None, fixture.State.Failure);
        Assert.Equal(AuthenticationStatus.Verified, fixture.State.Authentication);
        var recovered = Assert.IsType<UsageSnapshot>(fixture.State.Snapshot);
        Assert.NotSame(original, recovered); Assert.Equal(original.Binding, recovered.Binding);
        Assert.Equal(fixture.Clock.GetUtcNow(), recovered.ObservedAt);
        Assert.Equal(90, Assert.Single(recovered.Windows).RemainingPercent);
        Assert.Null(fixture.State.AutomaticRetryAt);
        fixture.Clock.Advance(29);
        Assert.False(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        fixture.Clock.Advance(1);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(5, fixture.UsageCalls); Assert.Equal(10, fixture.AuthCalls);
    }

    [Fact]
    public async Task FirstSupportedNullRetrievalIsUnavailableWithoutInventingAnObservation()
    {
        var fixture = new Fixture { Response = Payload(true, "null") };
        Assert.True(await fixture.Coordinator.RefreshAsync());
        Assert.Equal(FailureKind.Network, fixture.State.Failure);
        Assert.Equal(ProviderStatus.Error, fixture.State.Status);
        Assert.Equal(AuthenticationStatus.Verified, fixture.State.Authentication);
        Assert.Null(fixture.State.Snapshot);
        Assert.Equal("Unavailable", fixture.Status);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(30), fixture.State.AutomaticRetryAt);
        Assert.Equal(1, fixture.UsageCalls); Assert.Equal(2, fixture.AuthCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChangedAccountCannotRetainAnOldReadingAfterNullRetrieval(bool changesDuringQuery)
    {
        var fixture = new Fixture(); await fixture.Coordinator.RefreshAsync();
        Assert.NotNull(fixture.State.Snapshot);
        fixture.Response = Payload(true, "null");
        fixture.AfterAuth = Auth.Replace("fixture@example.invalid", "changed@example.invalid");
        if (!changesDuringQuery) fixture.BeforeAuth = fixture.AfterAuth;
        fixture.Clock.Advance(30);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(changesDuringQuery ? FailureKind.AccountChanged : FailureKind.Network, fixture.State.Failure);
        Assert.Equal(changesDuringQuery ? AuthenticationStatus.Unknown : AuthenticationStatus.Verified, fixture.State.Authentication);
        Assert.Null(fixture.State.Snapshot);
        Assert.Equal("Unavailable", fixture.Status);
        Assert.Equal(2, fixture.UsageCalls); Assert.Equal(4, fixture.AuthCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingSupportedLimitsIsMalformedAndRetainsOnlyAnExistingVerifiedReading(bool hasPreviousReading)
    {
        var fixture = new Fixture();
        if (hasPreviousReading) { await fixture.Coordinator.RefreshAsync(); fixture.Clock.Advance(30); }
        var original = fixture.State.Snapshot;
        fixture.Response = Payload(true, null);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(FailureKind.Malformed, fixture.State.Failure);
        Assert.Equal(AuthenticationStatus.Verified, fixture.State.Authentication);
        Assert.Same(original, fixture.State.Snapshot);
        Assert.Equal(hasPreviousReading ? "Stale" : "Unsupported format", fixture.Status);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(30), fixture.State.AutomaticRetryAt);
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(false, null)]
    [InlineData(true, "{}")]
    [InlineData(true, "{\"five_hour\":null,\"seven_day\":null}")]
    [InlineData(true, "{\"five_hour\":{\"utilization\":null,\"resets_at\":null}}")]
    public async Task LegitimateNoAllowanceResponsesReplaceOldReadingsWithoutFailureBackoff(bool available, string? limits)
    {
        var fixture = new Fixture(); await fixture.Coordinator.RefreshAsync();
        var original = Assert.IsType<UsageSnapshot>(fixture.State.Snapshot);
        fixture.Clock.Advance(30); fixture.Response = Payload(available, limits);
        Assert.True(await fixture.Coordinator.RefreshAsync(reason: RefreshReason.Background));
        Assert.Equal(FailureKind.None, fixture.State.Failure);
        Assert.Equal(ProviderStatus.Ready, fixture.State.Status);
        Assert.Equal(AuthenticationStatus.Verified, fixture.State.Authentication);
        Assert.Equal("Not reported", fixture.Status);
        var current = Assert.IsType<UsageSnapshot>(fixture.State.Snapshot);
        Assert.NotSame(original, current); Assert.Equal(original.Binding, current.Binding);
        Assert.Equal(fixture.Clock.GetUtcNow(), current.ObservedAt);
        Assert.All(current.Windows, window => Assert.Null(window.RemainingPercent));
        Assert.False(fixture.State.IsStale(fixture.Clock.GetUtcNow()));
        Assert.Null(fixture.State.AutomaticRetryAt);
        Assert.Equal(2, fixture.UsageCalls); Assert.Equal(4, fixture.AuthCalls);
    }

    private static JsonElement J(string value)
    { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }

    private static JsonElement Payload(bool available, string? limits) => J(
        "{\"rate_limits_available\":" + (available ? "true" : "false") +
        ",\"behaviors\":null,\"subscription_type\":\"pro\",\"session\":{\"total_cost_usd\":0,\"total_api_duration_ms\":0,\"model_usage\":{}}" +
        (limits is null ? "" : ",\"rate_limits\":" + limits) + "}");

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public JsonElement Response { get; set; } = Payload(true, GoodLimits);
        public string BeforeAuth { get; set; } = Auth;
        public string AfterAuth { get; set; } = Auth;
        public int AuthCalls { get; private set; }
        public int UsageCalls { get; private set; }
        public RefreshCoordinator Coordinator { get; }
        public ProviderState State => Assert.Single(Coordinator.States);
        public string Status => PopupText.Status(State, Clock.GetUtcNow());
        public Fixture()
        {
            var transport = new ClaudeControlTransport(() => "synthetic-never-launched.exe",
                (_, _) => Task.FromResult((J(++AuthCalls % 2 == 1 ? BeforeAuth : AfterAuth), 0)),
                (_, _, _) => { UsageCalls++; return Task.FromResult(Response); });
            var provider = new ClaudeProvider([new ClockedSource(transport, Clock)], utcNow: Clock.GetUtcNow);
            Coordinator = new RefreshCoordinator([provider], clock: Clock);
        }
    }

    // The real transport currently stamps wall time. Only replace its successful
    // snapshot timestamp so the actual parser/provider/scheduler pipeline can be
    // tested against a deterministic clock without sleeps or provider processes.
    private sealed class ClockedSource(ClaudeControlTransport transport, Clock clock) : IClaudeUsageSource
    {
        public ClaudeClient Client => ClaudeClient.Code;
        public async Task<ClaudeSourceResult> QueryAsync(CancellationToken token)
        {
            var result = await transport.QueryAsync(token);
            return result.Usage.Snapshot is { } snapshot
                ? result with { Usage = result.Usage with { Snapshot = snapshot with { ObservedAt = clock.GetUtcNow() } } }
                : result;
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => 1;
        public void Advance(int seconds) { timestamp += seconds; now = now.AddSeconds(seconds); }
    }
}
