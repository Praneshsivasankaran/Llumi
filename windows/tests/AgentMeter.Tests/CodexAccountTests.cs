using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class CodexAccountTests
{
    private const string First = "fixture-first@example.invalid";
    private const string Second = "fixture-second@example.invalid";
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static JsonElement Account(string email = First, string plan = "pro") =>
        JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email, planType = plan } });
    private static JsonElement Usage(double used = 20) => JsonSerializer.SerializeToElement(new
    {
        rateLimits = new { primary = new { usedPercent = used, windowDurationMins = 300,
            resetsAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds() } }
    });

    [Fact]
    public async Task ExistingAppServerProtocolVerifiesIdentityAroundReadOnlyQuotaQuery()
    {
        var session = new Session();
        var result = await Provider(() => session).QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Equal(80, Assert.Single(result.Snapshot!.Windows).RemainingPercent);
        Assert.Equal(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read", "account/read" }, session.Methods);
        Assert.True(session.Disposed);
        Assert.DoesNotContain(First, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("api", string.Join(" ", session.Methods));
        Assert.Equal("2.1.3", session.Version);
        Assert.Equal(AuthenticationStatus.Verified, result.Authentication);
        Assert.NotNull(result.VerifiedBinding);
        Assert.DoesNotContain(result.VerifiedBinding.Digest, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("2.1")]
    [InlineData("2.1.0 preview")]
    [InlineData("2.1.0\n")]
    public void ClientVersionMustBeValidatedProductVersion(string value) =>
        Assert.Throws<ArgumentException>(() => new CodexProvider(clientVersion: value));

    [Fact]
    public async Task ApiKeyAuthenticationIsUnsupportedBillingAndDoesNotReadQuota()
    {
        var session = new Session { Before = Json("""{"account":{"type":"apiKey"}}""") };
        var result = await Provider(() => session).QueryAsync(default);
        Assert.Equal(FailureKind.UnsupportedBilling, result.Failure);
        Assert.Equal(AuthenticationStatus.UnsupportedBilling, result.Authentication);
        Assert.Null(result.VerifiedBinding);
        Assert.DoesNotContain("account/rateLimits/read", session.Methods);
    }

    [Theory]
    [InlineData("signed-out", true)]
    [InlineData("different-account", true)]
    [InlineData("failed-auth", true)]
    [InlineData("signed-out", false)]
    [InlineData("different-account", false)]
    [InlineData("failed-auth", false)]
    public async Task RateEmbargoSurvivesFailedPostQueryContinuityCheck(string change, bool suppliedDuration)
    {
        var session = new Session
        {
            QuotaFailure = FailureKind.RateLimited, QuotaRetry = suppliedDuration ? TimeSpan.FromMinutes(4) : null,
            After = change == "signed-out" ? Json("""{"account":null}""") : Account(Second),
            AfterFailure = change == "failed-auth" ? FailureKind.Network : null
        };
        var result = await Provider(() => session).QueryAsync(default);
        Assert.Null(result.Snapshot);
        Assert.Null(result.VerifiedBinding);
        Assert.Equal(suppliedDuration ? TimeSpan.FromMinutes(4) : (TimeSpan?)null, result.RetryAfter);
        Assert.True(result.RateLimitObserved);
        Assert.Equal(change switch { "signed-out" => FailureKind.LoggedOut, "failed-auth" => FailureKind.Network, _ => FailureKind.AccountChanged }, result.Failure);
    }

    [Theory]
    [InlineData("second-account")]
    [InlineData("new-plan")]
    [InlineData("signed-out")]
    [InlineData("api-key")]
    public async Task AccountChangeDuringUsageNeverPublishesWrongQuota(string change)
    {
        var session = new Session { After = change switch
        {
            "second-account" => Account(Second), "new-plan" => Account(plan: "team"),
            "signed-out" => Json("""{"account":null}"""), _ => Json("""{"account":{"type":"apiKey"}}""")
        } };
        var result = await Provider(() => session).QueryAsync(CancellationToken.None);
        Assert.Null(result.Snapshot);
        Assert.NotEqual(FailureKind.None, result.Failure);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(480)]
    public async Task PostQueryRateLimitNeverShortensQuotaEmbargo(int laterSeconds)
    {
        var session = new Session
        {
            QuotaFailure = FailureKind.RateLimited, QuotaRetry = TimeSpan.FromMinutes(4),
            AfterFailure = FailureKind.RateLimited, AfterRetry = TimeSpan.FromSeconds(laterSeconds)
        };
        var result = await Provider(() => session).QueryAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(Math.Max(240, laterSeconds)), result.RetryAfter);
        Assert.True(result.RateLimitObserved);
        Assert.Null(result.VerifiedBinding); Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("""{"account":{"type":"chatgpt","email":"x@example.invalid","email":"y@example.invalid","planType":"pro"}}""")]
    [InlineData("""{"account":{"type":"chatgpt","planType":"pro"}}""")]
    [InlineData("""{"account":{"type":"chatgpt","email":"x@example.invalid"}}""")]
    [InlineData("""{"account":{"type":"apiKey"}}""")]
    public async Task UnverifiableOrApiKeyAccountNeverRequestsQuota(string account)
    {
        var session = new Session { Before = Json(account) };
        var result = await Provider(() => session).QueryAsync(CancellationToken.None);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain("account/rateLimits/read", session.Methods);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task SameAccountFailureRetainsStaleUsageAndRecoveryReplacesIt()
    {
        var next = new Session();
        var coordinator = new RefreshCoordinator([Provider(() => next)], minimumInterval: TimeSpan.Zero);
        await coordinator.RefreshAsync();
        next = new Session { QuotaFailure = FailureKind.Network };
        await coordinator.RefreshAsync();
        var failed = Assert.Single(coordinator.States);
        Assert.Equal(FailureKind.Network, failed.Failure);
        Assert.True(failed.IsStale(DateTimeOffset.UtcNow));
        Assert.NotNull(failed.Snapshot);
        next = new Session { Quota = Usage(35) };
        await coordinator.RefreshAsync();
        Assert.Equal(FailureKind.None, Assert.Single(coordinator.States).Failure);
        Assert.Equal(65, Assert.Single(Assert.Single(coordinator.States).Snapshot!.Windows).RemainingPercent);
    }

    [Fact]
    public async Task NewAccountFailureAndLostIdentityClearPriorAccountState()
    {
        var next = new Session();
        var coordinator = new RefreshCoordinator([Provider(() => next)], minimumInterval: TimeSpan.Zero);
        await coordinator.RefreshAsync();
        next = new Session { Before = Account(Second), After = Account(Second), QuotaFailure = FailureKind.Network };
        await coordinator.RefreshAsync();
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
        next = new Session { Before = Account(Second), After = Account(Second), Quota = Usage(60) };
        await coordinator.RefreshAsync();
        Assert.NotNull(Assert.Single(coordinator.States).Snapshot);
        next = new Session { AccountFailure = FailureKind.Timeout };
        await coordinator.RefreshAsync();
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
    }

    [Fact]
    public async Task LogoutClearsOldUsageAndPreservesSignedOutClassification()
    {
        var next = new Session();
        var coordinator = new RefreshCoordinator([Provider(() => next)], minimumInterval: TimeSpan.Zero);
        await coordinator.RefreshAsync();
        next = new Session { Before = Json("""{"account":null}""") };
        await coordinator.RefreshAsync();
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
        Assert.Equal(FailureKind.LoggedOut, Assert.Single(coordinator.States).Failure);
    }

    private static CodexProvider Provider(Func<Session> session) => new(executableLocator: () => "fixture.exe", startProcess: _ => session());
    private sealed class Session : IProviderRpcProcess
    {
        public JsonElement Before { get; init; } = Account();
        public JsonElement After { get; init; } = Account();
        public JsonElement Quota { get; init; } = Usage();
        public FailureKind? QuotaFailure { get; init; }
        public FailureKind? AccountFailure { get; init; }
        public FailureKind? AfterFailure { get; init; }
        public TimeSpan? QuotaRetry { get; init; }
        public TimeSpan? AfterRetry { get; init; }
        public string? Version { get; private set; }
        public List<string> Methods { get; } = [];
        public bool Disposed { get; private set; }
        public Task SendAsync(object message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.SerializeToElement(message);
            Methods.Add(json.GetProperty("method").GetString()!);
            if (json.GetProperty("method").GetString() == "initialize")
                Version = json.GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString();
            if (json.GetProperty("method").GetString() == "account/read")
                Assert.False(json.GetProperty("params").GetProperty("refreshToken").GetBoolean());
            return Task.CompletedTask;
        }
        public Task<JsonElement> ReadRpcResultAsync(int id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == 2 && AccountFailure is { } auth) throw new ProviderQueryException(auth);
            if (id == 3 && QuotaFailure is { } quota) throw new ProviderQueryException(quota, QuotaRetry);
            if (id == 4 && AfterFailure is { } after) throw new ProviderQueryException(after, AfterRetry);
            return Task.FromResult(id switch { 2 => Before, 3 => Quota, 4 => After, _ => Json("{}") });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
