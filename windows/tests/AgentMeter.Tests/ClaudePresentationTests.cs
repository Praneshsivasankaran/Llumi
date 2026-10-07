using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudePresentationTests
{
    private const string Five = "\"five_hour\":{\"utilization\":20,\"resets_at\":null}";
    private const string Week = "\"seven_day\":{\"utilization\":30,\"resets_at\":null}";
    private const string Internal = "\"iguana_necktie\":{\"utilization\":1,\"resets_at\":null}";
    private const string Auth = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"fixture@example.invalid","orgId":"00000000-0000-0000-0000-000000000042","orgName":"Fixture","subscriptionType":"pro","analyticsDisabled":false}""";
    private static JsonElement J(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static ProviderState Parse(string limits, string provider = "Claude Code")
    {
        var json = J("""
            {"rate_limits_available":true,"behaviors":null,"subscription_type":"pro",
             "session":{"total_cost_usd":0,"total_api_duration_ms":0,"model_usage":{}},"rate_limits":{
            """ + limits + "}}");
        var source = new ClaudeControlTransport(() => "fixture.exe", (_, _) => Task.FromResult((J(Auth), 0)),
            (_, _, _) => Task.FromResult(json));
        var result = source.QueryAsync(default).GetAwaiter().GetResult().Usage;
        Assert.Equal(FailureKind.None, result.Failure);
        return new(provider, ProviderStatus.Ready, result.Snapshot);
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Claude Code")]
    public void StandardWindowsDisplayInDurationOrder(string provider)
    {
        var state = Parse(Week + "," + Five, provider);
        Assert.Equal(["five_hour", "seven_day"], UsagePresentation.Windows(state).Select(w => w.Id));
        Assert.Equal(["5-hour limit", "Weekly limit"], UsagePresentation.Windows(state).Select(w => PopupText.WindowName(provider, w)));
        Assert.Equal("five_hour", MonitorSelection.Select(state)!.Id);
        Assert.Equal("80%", PopupText.Remaining(MonitorSelection.Select(state)!));
        Assert.Equal(UsagePresentation.Windows(state), MonitorSelection.Details(state));
        Assert.DoesNotContain("General", UsageText.Provider(state, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void OpaqueBucketDoesNotBecomeAConsumerAllowance()
    {
        var state = Parse(Five + "," + Internal + "," + Week);
        Assert.Equal(2, state.Snapshot!.Windows.Count);
        Assert.Equal(["five_hour", "seven_day"], UsagePresentation.Windows(state).Select(w => w.Id));
        Assert.DoesNotContain("iguana_necktie", UsageText.Provider(state, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UnknownBucketOnlyIsUnsupportedAndNeverBecomesPrimary()
    {
        var state = Parse(Internal);
        Assert.Empty(state.Snapshot!.Windows);
        Assert.Equal(AllowanceAvailability.UnsupportedFormat, state.Availability);
        Assert.Equal("Unsupported format", PopupText.Status(state, DateTimeOffset.UtcNow));
        Assert.Empty(UsagePresentation.Windows(state));
        Assert.Empty(MonitorSelection.Details(state));
        Assert.Null(MonitorSelection.Select(state));
    }

    [Fact]
    public void WeeklyWithoutFiveHourBecomesCompactGeneralAllowance()
    {
        var state = Parse(Week + "," + Internal);
        Assert.Equal("seven_day", MonitorSelection.Select(state)!.Id);
        Assert.Equal("seven_day", Assert.Single(UsagePresentation.Windows(state)).Id);
        Assert.Equal("seven_day", Assert.Single(MonitorSelection.Details(state)).Id);
    }

    [Fact]
    public void ModelWindowsAppearInDetailsAndDiagnosticsExcludeProviderLabels()
    {
        var state = Parse(Five + "," + Week + "," + Internal + "," + """
            "future_internal":{"utilization":2},"seven_day_sonnet":{"utilization":3},
            "model_scoped":[{"display_name":"synthetic_model","utilization":4},{"display_name":"five_hour","utilization":5}]
            """);
        Assert.Equal(5, state.Snapshot!.Windows.Count);
        Assert.Equal(5, UsagePresentation.Windows(state).Length);
        Assert.Equal(5, MonitorSelection.Details(state).Length);
        Assert.Equal("five_hour", MonitorSelection.Select(state)!.Id);
        var text = UsageText.Provider(state, DateTimeOffset.UtcNow);
        Assert.Contains("Weekly limit · Model: Sonnet", text);
        Assert.Contains("Model: synthetic_model", text);
        var diagnostics = SetupDiagnostics.Report([state], "2.1.0", "0");
        foreach (var name in new[] { "iguana_necktie", "future_internal", "seven_day_sonnet", "synthetic_model", "model:" })
            Assert.DoesNotContain(name, diagnostics);
        Assert.DoesNotContain("iguana_necktie", text);
        Assert.DoesNotContain("future_internal", text);
    }

    [Fact]
    public void ModelOnlyWindowsNeverReplaceCompactGeneralAllowance()
    {
        var state = Parse(Internal + "," + """
            "seven_day_sonnet":{"utilization":3},"model_scoped":[{"display_name":"five_hour","utilization":4}]
            """);
        Assert.Null(MonitorSelection.Select(state));
        Assert.Equal(2, UsagePresentation.Windows(state).Length);
        Assert.Equal(2, MonitorSelection.Details(state).Length);
        Assert.Equal("Live", PopupText.Status(state, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CodexMainAndAdditionalWindowsKeepScopeAndPreferFiveHour()
    {
        var json = J("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300},
            "secondary":{"usedPercent":30,"windowDurationMins":10080}},
            "codex_bengalfox":{"limitName":"Spark","primary":{"usedPercent":1,"windowDurationMins":300}}}}
            """);
        var state = new ProviderState("Codex", ProviderStatus.Ready, CodexParser.Parse(json, DateTimeOffset.UtcNow).Snapshot);
        Assert.Equal(3, UsagePresentation.Windows(state).Length);
        Assert.Equal("codex/primary", MonitorSelection.Select(state)!.Id);
        Assert.Equal("80%", PopupText.Remaining(MonitorSelection.Select(state)!));
        Assert.Contains(UsagePresentation.Windows(state), w => PopupText.WindowName("Codex", w) == "5-hour limit · Additional: Spark");
        Assert.Contains("5-hour limit", UsageText.Provider(state, DateTimeOffset.UtcNow));
    }
}
