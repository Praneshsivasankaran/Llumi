using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudePresentationTests
{
    private const string Five = "\"five_hour\":{\"utilization\":20,\"resets_at\":null}";
    private const string Week = "\"seven_day\":{\"utilization\":30,\"resets_at\":null}";
    private const string Internal = "\"iguana_necktie\":{\"utilization\":1,\"resets_at\":null}";

    private static ProviderState Parse(string limits, string provider = "Claude Code")
    {
        using var json = JsonDocument.Parse("""
            {"rate_limits_available":true,"behaviors":null,"subscription_type":"pro",
             "session":{"total_cost_usd":0,"total_api_duration_ms":0,"model_usage":{}},"rate_limits":{
            """ + limits + "}}");
        var windows = ClaudeControlTransport.ParseUsage(json.RootElement, "pro");
        return new(provider, ProviderStatus.Ready, new(windows, DateTimeOffset.UtcNow, "fixture"));
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Claude Code")]
    public void StandardWindowsDisplayInIntentionalOrder(string provider)
    {
        var state = Parse(Week + "," + Five, provider);
        Assert.Equal(["five_hour", "seven_day"], UsagePresentation.Windows(state).Select(w => w.Id));
        Assert.Equal(["5 hours", "7 days"], UsagePresentation.Windows(state).Select(w => PopupText.WindowName(provider, w)));
        Assert.Equal("five_hour", MonitorSelection.Select(state)!.Id);
        Assert.Equal("80%", PopupText.Remaining(MonitorSelection.Select(state)!));
        Assert.Equal(UsagePresentation.Windows(state), MonitorSelection.Details(state));
    }

    [Fact]
    public void ObservedInternalBucketIsParsedButNotPresented()
    {
        var state = Parse(Five + "," + Internal + "," + Week);
        var parsed = Assert.Single(state.Snapshot!.Windows, w => w.Id == "iguana_necktie");
        Assert.Equal("iguana_necktie", parsed.Name);
        Assert.Null(parsed.DurationMinutes);
        Assert.Equal(3, state.Snapshot.Windows.Count);
        Assert.Equal(["five_hour", "seven_day"], UsagePresentation.Windows(state).Select(w => w.Id));
        Assert.Null(UsagePresentation.ClaudeLabel(parsed));
        Assert.Equal("", PopupText.WindowName(state.Name, parsed));
        Assert.DoesNotContain("iguana_necktie", UsageText.Provider(state, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UnknownBucketOnlyNeverBecomesPrimary()
    {
        var state = Parse(Internal);
        Assert.Single(state.Snapshot!.Windows);
        Assert.Empty(UsagePresentation.Windows(state));
        Assert.Empty(MonitorSelection.Details(state));
        Assert.Null(MonitorSelection.Select(state));
    }

    [Fact]
    public void WeeklyWithoutFiveHourIsDetailOnly()
    {
        var state = Parse(Week + "," + Internal);
        Assert.Null(MonitorSelection.Select(state));
        Assert.Equal("seven_day", Assert.Single(UsagePresentation.Windows(state)).Id);
        Assert.Equal("seven_day", Assert.Single(MonitorSelection.Details(state)).Id);
    }

    [Fact]
    public void MultipleUnknownAndModelScopedBucketsDoNotLeakToConsumerTextOrDiagnostics()
    {
        var state = Parse(Five + "," + Week + "," + Internal + "," + """
            "future_internal":{"utilization":2},"seven_day_sonnet":{"utilization":3},
            "model_scoped":[{"display_name":"synthetic_model","utilization":4},{"display_name":"five_hour","utilization":5}]
            """);
        Assert.Equal(7, state.Snapshot!.Windows.Count);
        Assert.Contains(state.Snapshot.Windows, w => w.Id == "model:synthetic_model");
        Assert.Contains(state.Snapshot.Windows, w => w.Id == "model:five_hour");
        Assert.Equal(["five_hour", "seven_day"], UsagePresentation.Windows(state).Select(w => w.Id));
        Assert.Equal(["five_hour", "seven_day"], MonitorSelection.Details(state).Select(w => w.Id));
        var text = UsageText.Provider(state, DateTimeOffset.UtcNow) + UsageText.Tooltip([state], DateTimeOffset.UtcNow);
        var diagnostics = SetupDiagnostics.Report([state], "1.1.2", "2");
        foreach (var name in new[] { "iguana_necktie", "future_internal", "seven_day_sonnet", "synthetic_model", "model:" })
        {
            Assert.DoesNotContain(name, text);
            Assert.DoesNotContain(name, diagnostics);
        }
    }

    [Fact]
    public void OnlyUnknownAndModelScopedBucketsRemainUnknown()
    {
        var state = Parse(Internal + "," + """
            "seven_day_sonnet":{"utilization":3},"model_scoped":[{"display_name":"five_hour","utilization":4}]
            """);
        Assert.Null(MonitorSelection.Select(state));
        Assert.Empty(UsagePresentation.Windows(state));
        Assert.Empty(MonitorSelection.Details(state));
    }

    [Fact]
    public void CodexMainAndAdditionalWindowsKeepTheirPresentation()
    {
        using var json = JsonDocument.Parse("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300},
            "secondary":{"usedPercent":30,"windowDurationMins":10080}},
            "codex_bengalfox":{"limitName":"Spark","primary":{"usedPercent":1,"windowDurationMins":300}}}}
            """);
        var state = new ProviderState("Codex", ProviderStatus.Ready, CodexParser.Parse(json.RootElement, DateTimeOffset.UtcNow).Snapshot);
        Assert.Equal(state.Snapshot!.Windows, UsagePresentation.Windows(state));
        Assert.Equal("codex/secondary", MonitorSelection.Select(state)!.Id);
        Assert.Equal("70%", PopupText.Remaining(MonitorSelection.Select(state)!));
        Assert.Contains(UsagePresentation.Windows(state), w => PopupText.WindowName("Codex", w).StartsWith("Spark"));
        Assert.Contains("codex · 5 hours", UsageText.Provider(state, DateTimeOffset.UtcNow));
    }
}
