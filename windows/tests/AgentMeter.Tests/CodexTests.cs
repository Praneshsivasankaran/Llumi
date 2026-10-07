using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class CodexTests
{
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-09-15T13:00:00Z");

    private static ProviderResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CodexParser.Parse(document.RootElement, Observed);
    }

    [Fact]
    public void EquivalentLegacyAndMultipleBucketsMergeAndUseActualDurations()
    {
        var result = Parse("""
            {"rateLimits":{"primary":{"usedPercent":43,"windowDurationMins":10080,"resetsAt":1789817100}},
             "rateLimitsByLimitId":{
               "codex":{"limitName":"Codex","primary":{"usedPercent":43,"windowDurationMins":10080,"resetsAt":1789817100}},
               "spark":{"limitName":"Spark","primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":1789477200},"secondary":{"usedPercent":25.5,"windowDurationMins":10080,"resetsAt":null}}
             }}
            """);
        Assert.Equal(FailureKind.None, result.Failure);
        var windows = Assert.IsType<UsageSnapshot>(result.Snapshot).Windows;
        Assert.Equal(3, windows.Count);
        Assert.Equal("Codex · 7 days", windows[0].Name);
        Assert.Equal(43, windows[0].UsedPercent);
        Assert.Equal(57, windows[0].RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789817100), windows[0].ResetsAt);
        Assert.Equal("Spark · 5 hours", windows[1].Name);
        Assert.Equal(100, windows[1].RemainingPercent);
        Assert.Equal(74.5, windows[2].RemainingPercent);
        Assert.Null(windows[2].ResetsAt);
        Assert.Equal(Observed, result.Snapshot!.ObservedAt);
        Assert.False(result.Snapshot.IsCached);
    }

    [Theory]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":30}}}")]
    [InlineData("{\"rateLimitsByLimitId\":null,\"rateLimits\":{\"primary\":{\"usedPercent\":30}}}")]
    public void LegacyWindowsRemainSupported(string json)
    {
        var result = Parse(json);
        var window = Assert.Single(result.Snapshot!.Windows);
        Assert.Equal(70, window.RemainingPercent);
        Assert.Contains("duration unavailable", window.Name);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void EmptyNewMapCanUseExplicitLegacyAllowanceWithoutInventingWindows()
    {
        var result = Parse("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":30}}}""");
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Equal(70, Assert.Single(result.Snapshot!.Windows).RemainingPercent);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"rateLimitsByLimitId\":[]}")]
    [InlineData("{\"rateLimitsByLimitId\":{\"codex\":null}}")]
    [InlineData("{\"rateLimits\":{\"primary\":5}}")]
    [InlineData("{\"rateLimits\":\"changed\"}")]
    public void ChangedStructuralSchemaIsAnError(string json)
    {
        var result = Parse(json);
        Assert.Equal(FailureKind.Malformed, result.Failure);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rateLimits\":null}")]
    [InlineData("{\"rateLimits\":{\"primary\":null,\"secondary\":null}}")]
    public void NoWindowsMeansNotReportedAndDoesNotInventValues(string json)
    {
        var result = Parse(json);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Empty(result.Snapshot!.Windows);
        Assert.Equal(AllowanceAvailability.NotReported, result.Snapshot.Availability);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    [InlineData("1e400")]
    [InlineData("null")]
    [InlineData("\"52\"")]
    [InlineData("true")]
    [InlineData("[]")]
    public void InvalidPercentageStaysUnknown(string percent)
    {
        var result = Parse("""{"rateLimits":{"primary":{"usedPercent":PERCENT,"resetsAt":1789817100},"secondary":{"usedPercent":10,"windowDurationMins":10080}}}""".Replace("PERCENT", percent));
        var window = Assert.Single(result.Snapshot!.Windows, w => w.Id == "codex/primary");
        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
        Assert.NotNull(window.ResetsAt);
    }

    [Fact]
    public void MissingPercentageAndResetRemainUnknown()
    {
        var window = Assert.Single(Parse("""{"rateLimits":{"primary":{"windowDurationMins":60}}}""").Snapshot!.Windows);
        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
        Assert.Null(window.ResetsAt);
        Assert.Equal("Codex · 1 hour", window.Name);
    }

    [Theory]
    [InlineData("999999999999999999")]
    [InlineData("1789817100000")]
    [InlineData("\"1789817100\"")]
    [InlineData("true")]
    [InlineData("123.5")]
    public void InvalidResetIsUnknownAndNeverMistakenForMilliseconds(string reset)
    {
        var window = Assert.Single(Parse("""{"rateLimits":{"primary":{"usedPercent":20,"resetsAt":RESET}}}""".Replace("RESET", reset)).Snapshot!.Windows);
        Assert.Null(window.ResetsAt);
        Assert.Equal(80, window.RemainingPercent);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-300")]
    [InlineData("2.5")]
    [InlineData("\"300\"")]
    public void InvalidDurationDoesNotAssumePrimaryMeansFiveHours(string duration)
    {
        var window = Assert.Single(Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":DURATION}}}""".Replace("DURATION", duration)).Snapshot!.Windows);
        Assert.Contains("duration unavailable", window.Name);
    }

    [Fact]
    public void ProviderHealthIsVisibleButRawMetadataIsIgnored()
    {
        var result = Parse("""
            {"rateLimits":{"limitName":"Codex\n","spendControlReached":true,"ordinaryUsageAllowed":false,"rateLimitReachedType":"usage","accountId":"should-not-appear","credits":{"secret":"should-not-appear"},"primary":{"usedPercent":100,"windowDurationMins":60}}}
            """);
        Assert.Contains("spend limit", result.Detail);
        Assert.Contains("ordinary usage", result.Detail);
        Assert.Contains("reached limit", result.Detail);
        Assert.DoesNotContain("should-not-appear", JsonSerializer.Serialize(result));
        Assert.Equal("Codex · 1 hour", Assert.Single(result.Snapshot!.Windows).Name);
    }

    [Fact]
    public void RootOrdinaryUsageRestrictionIsPreserved()
    {
        var result = Parse("""{"ordinaryUsageAllowed":false,"rateLimits":{"primary":{"usedPercent":10}}}""");
        Assert.Equal("Provider reports ordinary usage is unavailable.", result.Detail);
        Assert.Equal(90, Assert.Single(result.Snapshot!.Windows).RemainingPercent);
    }

    [Fact]
    public void ScopeComesFromExactBucketIdentityRatherThanTransportSlotOrLabel()
    {
        var result = Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":100,"windowDurationMins":10080},"secondary":{"usedPercent":0,"windowDurationMins":300}},"codex-other":{"limitName":"codex","primary":{"usedPercent":1,"windowDurationMins":300}}}}""");
        var windows = result.Snapshot!.Windows;
        Assert.All(windows.Take(2), w => Assert.Equal(UsageScope.General, w.Scope));
        Assert.Equal(UsageScope.Additional, windows[2].Scope);
        Assert.Equal(0, windows[0].RemainingPercent);
        Assert.Equal(100, windows[1].RemainingPercent);
    }

    [Theory]
    [InlineData("{\"credits\":{\"balance\":3}}")]
    [InlineData("{\"rateLimits\":{\"spend\":{\"used\":0}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"monetaryAllowance\":1}}}")]
    public void MonetaryOrUnknownOnlySuccessIsUnsupportedFormatAndNeverBilling(string json)
    {
        var result = Parse(json);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Equal(AllowanceAvailability.UnsupportedFormat, result.Snapshot!.Availability);
        Assert.Empty(result.Snapshot.Windows);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"credits\":{}}")]
    [InlineData("{\"spend\":null}")]
    public void MissingOrEmptyMetadataMeansNotReported(string json) =>
        Assert.Equal(AllowanceAvailability.NotReported, Parse(json).Snapshot!.Availability);

    [Theory]
    [InlineData("{\"usedPercent\":21,\"windowDurationMins\":300}")]
    [InlineData("{\"usedPercent\":20,\"windowDurationMins\":300,\"resetsAt\":1789817101}")]
    public void ContradictoryEquivalentLegacyObservationIsRejected(string legacy)
    {
        var result = Parse("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300,\"resetsAt\":1789817100}}},\"rateLimits\":{\"secondary\":" + legacy + "}}");
        Assert.Equal(FailureKind.Unsupported, result.Failure);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void LegacyMirrorWithMissingDurationMergesByTransportSlotAndCompletesFields()
    {
        var result = Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":0,"windowDurationMins":300}}},"rateLimits":{"primary":{"usedPercent":0,"resetsAt":1789817100}}}""");
        var window = Assert.Single(result.Snapshot!.Windows);
        Assert.Equal(300, window.DurationMinutes);
        Assert.Equal(100, window.RemainingPercent);
        Assert.NotNull(window.ResetsAt);
    }

    [Theory]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":101}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"windowDurationMins\":0}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"resetsAt\":0}}}")]
    public void MalformedOnlyResponseFailsInsteadOfAppearingNotReported(string json) =>
        Assert.Equal(FailureKind.Malformed, Parse(json).Failure);

    [Fact]
    public async Task MissingExecutableReturnsNotInstalled()
    {
        var result = await new CodexProvider(executableLocator: () => null).QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.NotInstalled, result.Failure);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task MissingExecutableAtLaunchIsContained()
    {
        var result = await new CodexProvider(executableLocator: () => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")).QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.NotInstalled, result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("codex.exe")]
    [InlineData("C:\\missing-agentmeter-fixture\\codex.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe.invalid")]
    public void InvalidExecutablePathCannotStartAShellOrFallback(string path) => Assert.Null(CliLocator.ExistingExecutable(path));
}
