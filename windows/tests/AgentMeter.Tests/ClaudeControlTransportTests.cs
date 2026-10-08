using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudeControlTransportTests
{
    private const string Auth = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"fixture@example.invalid","orgId":"00000000-0000-0000-0000-000000000042","orgName":"Fixture","subscriptionType":"pro","analyticsDisabled":true}""";
    private const string Usage = """{"rate_limits_available":true,"behaviors":null,"subscription_type":"pro","session":{"total_cost_usd":0,"total_api_duration_ms":0,"model_usage":{}},"rate_limits":{"five_hour":{"utilization":12,"resets_at":"2030-01-01T12:30:00Z"},"seven_day":{"utilization":0,"resets_at":null}}}""";
    private static JsonElement J(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static JsonElement WithModels(string models) => J(Usage.Replace("\"rate_limits\":{", "\"rate_limits\":{\"model_scoped\":" + models + ","));

    [Fact]
    public async Task DirectQueryRevalidatesAccountAndNeedsNoBridge()
    {
        var authCalls = 0;
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => { authCalls++; return Task.FromResult((J(Auth), 0)); },
            (_, identity, _) => { Assert.Equal("pro", identity.Plan); return Task.FromResult(J(Usage)); });
        var result = await source.QueryAsync(default);
        Assert.Equal(2, authCalls);
        Assert.Equal([88d, 100d], result.Usage.Snapshot!.Windows.Select(w => w.RemainingPercent!.Value));
        Assert.DoesNotContain("fixture@example", result.ToString());
        Assert.DoesNotContain(ClaudeControlTransport.Arguments, a => a.Contains("sdk", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("fixture@example.invalid", "changed@example.invalid")]
    [InlineData("pro", "max")]
    [InlineData("Fixture", "Other organization")]
    public async Task ChangedAccountContextDiscardsUsage(string before, string after)
    {
        var calls = 0;
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => Task.FromResult((J(++calls == 1 ? Auth : Auth.Replace(before, after)), 0)),
            (_, _, _) => Task.FromResult(J(Usage)));
        var result = await source.QueryAsync(default);
        Assert.Null(result.Binding); Assert.Null(result.Usage.Snapshot); Assert.Equal(FailureKind.AccountChanged, result.Usage.Failure);
    }

    [Theory]
    [InlineData("\"total_cost_usd\":0", "\"total_cost_usd\":1")]
    [InlineData("\"total_api_duration_ms\":0", "\"total_api_duration_ms\":1")]
    [InlineData("\"model_usage\":{}", "\"model_usage\":{\"model\":1}")]
    [InlineData("\"behaviors\":null", "\"behaviors\":{}")]
    [InlineData("\"utilization\":12", "\"utilization\":12,\"utilization\":12")]
    public void MalformedOrInferenceBearingResponsesFailClosed(string old, string replacement) =>
        Assert.Throws<ProviderQueryException>(() => ClaudeControlTransport.ParseUsage(J(Usage.Replace(old, replacement)), "pro"));

    [Theory]
    [InlineData("\"utilization\":12", "\"utilization\":101")]
    [InlineData("\"utilization\":12", "\"utilization\":\"12\"")]
    [InlineData("2030-01-01T12:30:00Z", "2030-01-01T12:30:00")]
    public void MalformedOptionalFieldsNeverInventValuesAndPreserveAnotherSupportedWindow(string old, string replacement)
    {
        var windows = ClaudeControlTransport.ParseUsage(J(Usage.Replace(old, replacement)), "pro");
        Assert.Equal(100, windows.Single(w => w.Id == "seven_day").RemainingPercent);
        var five = windows.Single(w => w.Id == "five_hour");
        if (old.Contains("utilization", StringComparison.Ordinal)) Assert.Null(five.RemainingPercent);
        else Assert.Null(five.ResetsAt);
    }

    [Theory]
    [InlineData("{}", AllowanceAvailability.NotReported)]
    [InlineData("{\"future_bucket\":{\"utilization\":2}}", AllowanceAvailability.UnsupportedFormat)]
    [InlineData("{\"extra_usage\":{\"spend\":0}}", AllowanceAvailability.UnsupportedFormat)]
    [InlineData("{\"extra_usage\":{}}", AllowanceAvailability.NotReported)]
    [InlineData("{\"five_hour\":{\"utilization\":null}}", AllowanceAvailability.NotReported)]
    public async Task SuccessfulNoDataResponsesRetainReadinessAndDoNotInventAllowance(string limits, AllowanceAvailability expected)
    {
        var payload = J("{\"rate_limits_available\":true,\"behaviors\":null,\"subscription_type\":\"pro\",\"session\":{\"total_cost_usd\":0,\"total_api_duration_ms\":0,\"model_usage\":{}},\"rate_limits\":" + limits + "}");
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => Task.FromResult((J(Auth), 0)), (_, _, _) => Task.FromResult(payload));
        var provider = new ClaudeProvider([source]);
        var result = await provider.QueryAsync(default);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Equal(AuthenticationStatus.Verified, result.Authentication);
        Assert.NotNull(result.VerifiedBinding);
        Assert.Equal(expected, result.Snapshot!.Availability);
        Assert.All(result.Snapshot.Windows, w => Assert.Null(w.RemainingPercent));
        Assert.DoesNotContain("future_bucket", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void VerifiedModelWindowsRetainScopeAndWeeklyDuration()
    {
        var payload = Usage.Replace("\"seven_day\":{\"utilization\":0,\"resets_at\":null}", "\"seven_day_sonnet\":{\"utilization\":100},\"seven_day_opus\":{\"utilization\":0},\"model_scoped\":[{\"display_name\":\"Fixture model\",\"utilization\":25}]");
        var windows = ClaudeControlTransport.ParseUsage(J(payload), "pro");
        Assert.Equal(4, windows.Count);
        Assert.All(windows.Where(w => w.Scope == UsageScope.Model), w => Assert.Equal(10080, w.DurationMinutes));
        Assert.Equal(0, windows.Single(w => w.ScopeLabel == "Sonnet").RemainingPercent);
        Assert.Equal(100, windows.Single(w => w.ScopeLabel == "Opus").RemainingPercent);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[17]")]
    [InlineData("[{}]")]
    [InlineData("[{\"display_name\":null,\"utilization\":20}]")]
    [InlineData("[{\"display_name\":\" \",\"utilization\":20}]")]
    [InlineData("[{\"display_name\":\"internal\\nmodel\",\"utilization\":20}]")]
    public void MalformedModelEntryPreservesOtherSupportedAllowances(string models)
    {
        var parsed = ClaudeControlTransport.ParseAllowance(WithModels(models), "pro");
        Assert.Equal(AllowanceAvailability.Reported, parsed.Availability);
        Assert.Equal(2, parsed.Windows.Count);
        Assert.All(parsed.Windows, w => Assert.Equal(UsageScope.General, w.Scope));
        Assert.Equal(88, parsed.Windows.Single(w => w.Id == "five_hour").RemainingPercent);
        Assert.Equal(100, parsed.Windows.Single(w => w.Id == "seven_day").RemainingPercent);
        Assert.DoesNotContain("internal", JsonSerializer.Serialize(parsed.Windows));
    }

    [Fact]
    public void MalformedModelOnlyResponseStillFailsWithoutInventingAllowance()
    {
        var response = J("""{"rate_limits_available":true,"behaviors":null,"subscription_type":"pro","session":{"total_cost_usd":0,"total_api_duration_ms":0,"model_usage":{}},"rate_limits":{"model_scoped":[{"display_name":null,"utilization":20}]}}""");
        Assert.Equal(FailureKind.Malformed, Assert.Throws<ProviderQueryException>(() =>
            ClaudeControlTransport.ParseUsage(response, "pro")).Failure);
    }

    [Fact]
    public void EquivalentModelEntriesMergeAndCompleteMissingValues()
    {
        var parsed = ClaudeControlTransport.ParseAllowance(WithModels("""[{"display_name":"Fixture model","utilization":100},{"display_name":"Fixture model","utilization":100,"resets_at":"2030-01-01T12:30:00Z"},{"display_name":"Fixture model","resets_at":"2030-01-01T12:30:00Z"}]"""), "pro");
        Assert.Equal(AllowanceAvailability.Reported, parsed.Availability);
        var model = Assert.Single(parsed.Windows, w => w.Scope == UsageScope.Model);
        Assert.Equal("Fixture model", model.ScopeLabel);
        Assert.Equal(10080, model.DurationMinutes);
        Assert.Equal(0, model.RemainingPercent);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T12:30:00Z"), model.ResetsAt);
        Assert.Equal(3, parsed.Windows.Count);
    }

    [Theory]
    [InlineData("{\"display_name\":\"Fixture model\",\"utilization\":1}")]
    [InlineData("{\"display_name\":\"Fixture model\",\"utilization\":0,\"resets_at\":\"2030-01-01T12:31:00Z\"}")]
    public void ContradictoryModelEntriesRejectTheObservation(string conflicting)
    {
        var response = WithModels("[{\"display_name\":\"Fixture model\",\"utilization\":0,\"resets_at\":\"2030-01-01T12:30:00Z\"}," + conflicting + "]");
        Assert.Equal(FailureKind.Unsupported, Assert.Throws<ProviderQueryException>(() =>
            ClaudeControlTransport.ParseUsage(response, "pro")).Failure);
    }

    [Theory]
    [InlineData("\"authMethod\":\"claude.ai\"", "\"authMethod\":\"apiKey\"")]
    [InlineData("\"apiProvider\":\"firstParty\"", "\"apiProvider\":\"bedrock\"")]
    [InlineData("\"apiProvider\":\"firstParty\"", "\"apiProvider\":\"foundry\"")]
    public void KnownUnsupportedBillingUsesAuthenticationEvidenceOnly(string before, string after)
    {
        var failure = Assert.Throws<ProviderQueryException>(() => ClaudeControlTransport.ParseIdentity(J(Auth.Replace(before, after)), 0));
        Assert.Equal(FailureKind.UnsupportedBilling, failure.Failure);
    }

    [Fact]
    public async Task FoundryBillingDoesNotQuerySubscriptionAllowance()
    {
        var usageCalls = 0;
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => Task.FromResult((J(Auth.Replace("firstParty", "foundry")), 0)),
            (_, _, _) => { usageCalls++; return Task.FromResult(J(Usage)); });
        var result = await new ClaudeProvider([source]).QueryAsync(default);
        Assert.Equal(FailureKind.UnsupportedBilling, result.Failure);
        Assert.Equal(AuthenticationStatus.UnsupportedBilling, result.Authentication);
        Assert.Null(result.Snapshot);
        Assert.Null(result.VerifiedBinding);
        Assert.Equal(0, usageCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task KnownRateEmbargoSurvivesLostOrChangedPostQueryAuthentication(bool signedOut, bool suppliedDuration)
    {
        var calls = 0;
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => Task.FromResult((J(++calls == 1 ? Auth :
            signedOut ? "{\"loggedIn\":false}" : Auth.Replace("fixture@example.invalid", "changed@example.invalid")), 0)),
            (_, _, _) => throw new ProviderQueryException(FailureKind.RateLimited, suppliedDuration ? TimeSpan.FromMinutes(3) : null));
        var result = await new ClaudeProvider([source]).QueryAsync(default);
        Assert.Null(result.VerifiedBinding);
        Assert.Null(result.Snapshot);
        Assert.Equal(signedOut ? FailureKind.LoggedOut : FailureKind.AccountChanged, result.Failure);
        Assert.Equal(suppliedDuration ? TimeSpan.FromMinutes(3) : (TimeSpan?)null, result.RetryAfter);
        Assert.True(result.RateLimitObserved);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public async Task UsageNeverStartsUnlessChildConfirmsTelemetryDisabled(string analytics)
    {
        var usageCalls = 0;
        var source = new ClaudeControlTransport(() => "fixture.exe",
            (_, _) => Task.FromResult((J(Auth.Replace("\"analyticsDisabled\":true", "\"analyticsDisabled\":" + analytics)), 0)),
            (_, _, _) => { usageCalls++; return Task.FromResult(J(Usage)); });
        var result = await source.QueryAsync(default);
        Assert.Equal(FailureKind.Unsupported, result.Usage.Failure);
        Assert.Null(result.Binding); Assert.Null(result.Usage.Snapshot);
        Assert.Equal(0, usageCalls);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(480)]
    public async Task PostQueryRateLimitNeverShortensUsageEmbargo(int laterSeconds)
    {
        var calls = 0;
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => ++calls == 1
            ? Task.FromResult((J(Auth), 0))
            : throw new ProviderQueryException(FailureKind.RateLimited, TimeSpan.FromSeconds(laterSeconds)),
            (_, _, _) => throw new ProviderQueryException(FailureKind.RateLimited, TimeSpan.FromMinutes(4)));
        var result = await new ClaudeProvider([source]).QueryAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(Math.Max(240, laterSeconds)), result.RetryAfter);
        Assert.True(result.RateLimitObserved);
        Assert.Null(result.VerifiedBinding); Assert.Null(result.Snapshot);
    }

    [Fact]
    public void MissingOrDuplicateTelemetryEvidenceFailsClosed()
    {
        Assert.Throws<ProviderQueryException>(() => ClaudeControlTransport.ParseIdentity(J(Auth.Replace(",\"analyticsDisabled\":true", "")), 0));
        Assert.Throws<ProviderQueryException>(() => ClaudeControlTransport.ParseIdentity(J(Auth.Replace("\"analyticsDisabled\":true", "\"analyticsDisabled\":true,\"analyticsDisabled\":false")), 0));
    }

    [Theory]
    [InlineData("DISABLE_TELEMETRY", "1")]
    [InlineData("DISABLE_TELEMETRY", "0")]
    [InlineData("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1")]
    [InlineData("DO_NOT_TRACK", "true")]
    public async Task RealOwnedControlChildrenKeepPrivacyAndUsageOnlyProtocol(string inheritedFlag, string value)
    {
        var parentValue = Environment.GetEnvironmentVariable(inheritedFlag);
        var executable = Path.Combine(AppContext.BaseDirectory, "AgentMeter.ProcessHost.exe");
        await using var process = ProviderProcess.Start(executable, ["--claude-control-query"],
            new Dictionary<string, string> { [inheritedFlag] = value });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (result, exitCode) = await process.ReadJsonOutputAsync(timeout.Token);
        Assert.Equal(0, exitCode);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("parentEnvironmentUnchanged").GetBoolean());
        Assert.Equal(88, result.GetProperty("remaining").GetDouble());
        Assert.Equal(parentValue, Environment.GetEnvironmentVariable(inheritedFlag));
    }

    [Fact]
    public void SessionMustMatchProviderOwnedAuthentication()
    {
        var identity = ClaudeControlTransport.ParseIdentity(J(Auth), 0);
        ClaudeControlTransport.VerifySession(J("""{"email":"fixture@example.invalid","organization":"Fixture","apiProvider":"firstParty","apiKeySource":"none","tokenSource":"oauth"}"""), identity);
        Assert.Throws<ProviderQueryException>(() => ClaudeControlTransport.VerifySession(J("""{"email":"other@example.invalid","organization":"Fixture","apiProvider":"firstParty"}"""), identity));
        Assert.NotEqual(identity.Binding, (identity with { Plan = "max" }).Binding);
    }

    [Fact]
    public async Task FailedUsageRetainsOnlyReverifiedBinding()
    {
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => Task.FromResult((J(Auth), 0)),
            (_, _, _) => throw new ProviderQueryException(FailureKind.Network));
        var result = await source.QueryAsync(default);
        Assert.NotNull(result.Binding); Assert.Null(result.Usage.Snapshot); Assert.Equal(FailureKind.Network, result.Usage.Failure);
    }

    [Fact]
    public async Task TimeoutAndCancellationDoNotRetainIdentity()
    {
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => Task.FromResult((J(Auth), 0)),
            async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); return J(Usage); }, TimeSpan.FromMilliseconds(30));
        var result = await source.QueryAsync(default);
        Assert.Equal(FailureKind.Timeout, result.Usage.Failure); Assert.Null(result.Binding);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.QueryAsync(cancel.Token));
    }

    [Fact]
    public void IsolatedWorkspaceOverridesWorktreeEnvironmentAndRemovesEmptyDirectory()
    {
        var work = new ProviderWorkspace();
        var info = new System.Diagnostics.ProcessStartInfo();
        info.Environment["GIT_DIR"] = "private-repo"; info.Environment["GIT_WORK_TREE"] = "private-repo";
        work.Configure(info);
        Assert.False(info.Environment.ContainsKey("GIT_DIR")); Assert.False(info.Environment.ContainsKey("GIT_WORK_TREE"));
        Assert.Equal(work.DirectoryPath, info.WorkingDirectory);
        Assert.Equal(Path.GetDirectoryName(work.DirectoryPath), info.Environment["GIT_CEILING_DIRECTORIES"]);
        work.Dispose(); Assert.False(Directory.Exists(work.DirectoryPath));
    }
}
