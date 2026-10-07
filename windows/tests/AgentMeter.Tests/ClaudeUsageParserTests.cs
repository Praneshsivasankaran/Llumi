using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudeUsageParserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T15:00:00Z");
    private const string Account = "claude-email-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Organization = "00000000-0000-0000-0000-000000000042";
    private const string Binding = "{\"accountId\":\"" + Account + "\",\"organizationId\":\"" + Organization + "\"}";

    [Fact]
    public void RealQuotaShapePreservesZeroPercentAndReceiptTimestamp()
    {
        var result = Parse("""{"five_hour":{"utilization":0,"resets_at":"2026-09-15T19:30:00Z"},"seven_day":{"utilization":8,"resets_at":"2026-09-19T15:00:00+00:00"}}""");
        Assert.Equal(ClaudeAuthentication.Authenticated, result.Authentication);
        Assert.Equal(new ClaudeAccountBinding(Account, Organization), result.Binding);
        Assert.Equal(new double?[] { 100, 92 }, result.Usage.Snapshot!.Windows.Select(window => window.RemainingPercent));
        Assert.Equal(Now, result.Usage.Snapshot.ObservedAt);
        Assert.False(result.Usage.Snapshot.IsCached);
        Assert.All(result.Usage.Snapshot.Windows, window => Assert.NotNull(window.ResetsAt));
        Assert.DoesNotContain(Account, result.Usage.Snapshot.Source);
    }

    [Fact]
    public void OnlyActualPresentWindowsAreReportedIncludingModelScopedUsage()
    {
        var result = Parse("""{"seven_day_sonnet":{"utilization":21},"five_hour":null,"model_scoped":[{"display_name":"Fixture model","utilization":25,"resets_at":"2026-09-15T22:00:00+05:30"}]}""");
        Assert.Equal(new[] { "seven_day_sonnet", "model:Fixture model" }, result.Usage.Snapshot!.Windows.Select(window => window.Id));
        Assert.Equal(DateTimeOffset.Parse("2026-09-15T16:30:00Z"), result.Usage.Snapshot.Windows[1].ResetsAt);
        Assert.Null(result.Usage.Snapshot.Windows[0].ResetsAt);
        Assert.All(result.Usage.Snapshot.Windows, w =>
        {
            Assert.Equal(UsageScope.Model, w.Scope);
            Assert.Equal(10080, w.DurationMinutes);
        });
    }

    [Theory]
    [InlineData("{\"extra_usage\":{\"spend\":0}}", AllowanceAvailability.UnsupportedFormat)]
    [InlineData("{\"extra_usage\":{}}", AllowanceAvailability.NotReported)]
    [InlineData("{\"seven_day_oauth_apps\":{\"utilization\":12}}", AllowanceAvailability.UnsupportedFormat)]
    public void MetadataOnlyCannotEstablishTimeAllowanceOrBilling(string usage, AllowanceAvailability expected)
    {
        var result = Parse(usage);
        Assert.Equal(FailureKind.None, result.Usage.Failure);
        Assert.Equal(expected, result.Usage.Snapshot!.Availability);
        Assert.Empty(result.Usage.Snapshot.Windows);
    }

    [Fact]
    public void ContradictoryDuplicateModelAllowanceFailsClosed()
    {
        var result = Parse("""{"seven_day_sonnet":{"utilization":20},"model_scoped":[{"display_name":"Sonnet","utilization":21}]}""");
        Assert.Equal(FailureKind.Malformed, result.Usage.Failure);
        Assert.Null(result.Usage.Snapshot);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1e999")]
    [InlineData("\"28%\"")]
    [InlineData("true")]
    [InlineData("{}")]
    public void InvalidPercentageRemainsUnknownAndNeverBecomesZeroOrFull(string value)
    {
        var result = Parse("{\"five_hour\":{\"utilization\":" + value + ",\"resets_at\":\"2026-09-15T19:00:00Z\"},\"seven_day\":{\"utilization\":8}}");
        var window = Assert.Single(result.Usage.Snapshot!.Windows, w => w.Id == "five_hour");
        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
        Assert.NotNull(window.ResetsAt);
    }

    [Theory]
    [InlineData("\"2026-09-15T19:00:00\"")]
    [InlineData("\"tomorrow\"")]
    [InlineData("1234567890")]
    [InlineData("true")]
    [InlineData("\"2026-09-15T19:00:00+99:00\"")]
    public void InvalidResetNeverUsesLocalTimezoneOrInventsCountdown(string value)
    {
        var result = Parse("{\"five_hour\":{\"utilization\":8,\"resets_at\":" + value + "}}");
        var window = Assert.Single(result.Usage.Snapshot!.Windows);
        Assert.Equal(92, window.RemainingPercent);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void MissingAndNullFieldsStayUnknown()
    {
        var result = Parse("""{"five_hour":{"utilization":null,"resets_at":"2026-09-15T19:00:00Z"},"seven_day":{"utilization":8,"resets_at":null}}""");
        Assert.Null(result.Usage.Snapshot!.Windows[0].RemainingPercent);
        Assert.Null(result.Usage.Snapshot.Windows[1].ResetsAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"five_hour\":{}}")]
    [InlineData("{\"five_hour\":{\"utilization\":null,\"resets_at\":null}}")]
    public void MissingUsageIsNotReportedAndVerifiedIdentitySurvives(string usage)
    {
        var result = Parse(usage);
        Assert.Equal(FailureKind.None, result.Usage.Failure);
        Assert.Equal(AllowanceAvailability.NotReported, result.Usage.Snapshot!.Availability);
        Assert.All(result.Usage.Snapshot.Windows, w => Assert.Null(w.RemainingPercent));
        Assert.NotNull(result.Binding);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void SuccessWithUnsuccessfulExitIsRejected(int exitCode) =>
        AssertMalformed(ClaudeUsageParser.Parse(Envelope("{\"five_hour\":{\"utilization\":0}}"), exitCode, Now));

    [Fact]
    public void FailureWithSuccessfulExitIsRejected() =>
        AssertMalformed(ClaudeUsageParser.Parse(Envelope("null", failure: "timeout", detail: "queryTimedOut"), 0, Now));

    [Theory]
    [InlineData("{\"five_hour\":{\"utilization\":1,\"utilization\":2}}")]
    [InlineData("{\"five_hour\":{\"utilization\":1},\"five_hour\":{\"utilization\":2}}")]
    [InlineData("{\"unexpected_window\":{\"utilization\":1}}")]
    [InlineData("{\"five_hour\":{\"utilization\":1,\"prompt\":\"fixture\"}}")]
    [InlineData("{\"five_hour\":123}")]
    [InlineData("{\"model_scoped\":{}}")]
    [InlineData("{\"model_scoped\":[{\"display_name\":\"\",\"utilization\":1}]}")]
    [InlineData("{\"model_scoped\":[{\"display_name\":\"Fixture\",\"utilization\":1},{\"display_name\":\"Fixture\",\"utilization\":2}]}")]
    public void UnknownOrDuplicateUsageStructureFailsClosed(string usage) => AssertMalformed(Parse(usage));

    [Fact]
    public void DuplicateRootAndBindingKeysAreRejected()
    {
        var json = Envelope("{\"five_hour\":{\"utilization\":0}}");
        AssertMalformed(ClaudeUsageParser.Parse(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"), 0, Now));
        AssertMalformed(ClaudeUsageParser.Parse(json.Replace("\"accountId\":", "\"accountId\":\"ignored\",\"accountId\":"), 0, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("{}")]
    public void MalformedEnvelopeIsSafe(string json) => AssertMalformed(ClaudeUsageParser.Parse(json, 0, Now));

    [Fact]
    public void SchemaChangeUnknownDetailAndRawIdentityAreRejected()
    {
        var json = Envelope("{\"five_hour\":{\"utilization\":0}}");
        AssertMalformed(ClaudeUsageParser.Parse(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"), 0, Now));
        AssertMalformed(ClaudeUsageParser.Parse(json.Replace("usageAvailable", "raw server diagnostic"), 0, Now));
        AssertMalformed(ClaudeUsageParser.Parse(json.Replace(Account, "fixture@example.invalid"), 0, Now));
        AssertMalformed(ClaudeUsageParser.Parse(json.Replace(Organization, "not-an-org-id"), 0, Now));
    }

    [Theory]
    [InlineData("signedOut")]
    [InlineData("missing")]
    [InlineData("unknown")]
    public void NonAuthenticatedSourceNeverPublishesSnapshotOrBinding(string authentication) =>
        AssertMalformed(ClaudeUsageParser.Parse(Envelope("{\"five_hour\":{\"utilization\":0}}", authentication: authentication), 0, Now));

    [Fact]
    public void AuthenticatedSuccessRequiresBinding() =>
        AssertMalformed(ClaudeUsageParser.Parse(Envelope("{\"five_hour\":{\"utilization\":0}}", binding: "null"), 0, Now));

    [Fact]
    public void AccountChangeClearsBindingAndNumbersWithoutPrintingIdentity()
    {
        var expected = new ClaudeAccountBinding("claude-email-sha256:" + new string('b', 64), Organization);
        var result = ClaudeUsageParser.Parse(Envelope("{\"five_hour\":{\"utilization\":0}}"), 0, Now, expected);
        Assert.Equal(FailureKind.AccountChanged, result.Usage.Failure);
        Assert.Equal(ClaudeAuthentication.Unknown, result.Authentication);
        Assert.Null(result.Binding);
        Assert.Null(result.Usage.Snapshot);
        Assert.DoesNotContain(Account, result.ToString());
        Assert.DoesNotContain(Organization, result.ToString());
    }

    [Theory]
    [InlineData("timeout", "queryTimedOut", FailureKind.Timeout)]
    [InlineData("unsupported", "quotaUnavailable", FailureKind.Unsupported)]
    [InlineData("network", "usageQueryFailed", FailureKind.Network)]
    public void AuthenticatedFailurePreservesOnlyVerifiedBinding(string failure, string detail, FailureKind expected)
    {
        var result = ClaudeUsageParser.Parse(Envelope("null", failure: failure, detail: detail), 1, Now);
        Assert.Equal(expected, result.Usage.Failure);
        Assert.NotNull(result.Binding);
        Assert.Null(result.Usage.Snapshot);
    }

    [Theory]
    [InlineData("signedOut", "loggedOut", "identityUnavailable", ClaudeAuthentication.SignedOut)]
    [InlineData("missing", "notInstalled", "cliMissing", ClaudeAuthentication.Missing)]
    [InlineData("unknown", "unsupported", "identityChanged", ClaudeAuthentication.Unknown)]
    public void AuthenticationFailuresHaveNoBinding(string authentication, string failure, string detail, ClaudeAuthentication expected)
    {
        var result = ClaudeUsageParser.Parse(Envelope("null", authentication, failure, detail, "null"), 1, Now);
        Assert.Equal(expected, result.Authentication);
        Assert.Null(result.Binding);
        Assert.Null(result.Usage.Snapshot);
    }

    [Theory]
    [InlineData("privacyOptOut")]
    [InlineData("identityChanged")]
    [InlineData("sdkAccountMismatch")]
    [InlineData("identityUnavailable")]
    public void FailedIdentityOrPrivacyCheckCannotRetainBinding(string detail) =>
        AssertMalformed(ClaudeUsageParser.Parse(Envelope("null", failure: "unsupported", detail: detail), 1, Now));

    [Fact]
    public void ContradictoryAuthenticatedLoggedOutOrMissingFailuresAreRejected()
    {
        AssertMalformed(ClaudeUsageParser.Parse(Envelope("null", failure: "loggedOut", detail: "identityUnavailable", binding: "null"), 1, Now));
        AssertMalformed(ClaudeUsageParser.Parse(Envelope("null", failure: "notInstalled", detail: "cliMissing", binding: "null"), 1, Now));
    }

    internal static string Envelope(string usage, string authentication = "authenticated", string failure = "none",
        string detail = "usageAvailable", string binding = Binding) =>
        "{\"schemaVersion\":1,\"authentication\":\"" + authentication + "\",\"failure\":\"" + failure +
        "\",\"detailCode\":\"" + detail + "\",\"binding\":" + binding + ",\"usage\":" + usage + "}";
    private static ClaudeSourceResult Parse(string usage) => ClaudeUsageParser.Parse(Envelope(usage), 0, Now);
    private static void AssertMalformed(ClaudeSourceResult result)
    {
        Assert.Equal(FailureKind.Malformed, result.Usage.Failure);
        Assert.Null(result.Binding);
        Assert.Null(result.Usage.Snapshot);
    }
}
