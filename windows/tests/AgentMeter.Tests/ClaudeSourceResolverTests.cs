using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudeSourceResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T15:00:00Z");
    private static readonly ClaudeAccountBinding Account = new("fixture-account", "fixture-organization");

    [Theory]
    [InlineData(ClaudeClient.Desktop, ClaudeAuthentication.Missing)]
    [InlineData(ClaudeClient.Desktop, ClaudeAuthentication.SignedOut)]
    [InlineData(ClaudeClient.Code, ClaudeAuthentication.Missing)]
    [InlineData(ClaudeClient.Code, ClaudeAuthentication.SignedOut)]
    public void VerifiedClientWorksIndependently(ClaudeClient client, ClaudeAuthentication otherAuthentication)
    {
        var good = Good(client);
        var other = Absent(Other(client), otherAuthentication);
        var resolution = Resolve(good, other);
        Assert.Same(good.Usage.Snapshot, resolution.Result.Snapshot);
        Assert.Equal(FailureKind.None, resolution.Result.Failure);
        Assert.Equal(client, resolution.Client);
        Assert.Equal(Account, resolution.Binding);
    }

    [Fact]
    public void NoDetectedClientsIsNotInstalled()
    {
        Assert.Equal(FailureKind.NotInstalled, Resolve().Result.Failure);
        Assert.Equal(FailureKind.NotInstalled, Resolve(Absent(ClaudeClient.Desktop), Absent(ClaudeClient.Code)).Result.Failure);
    }

    [Fact]
    public void SignedOutClientReportsAuthenticationWithoutInventingBinding()
    {
        var result = Resolve(Absent(ClaudeClient.Desktop), Absent(ClaudeClient.Code, ClaudeAuthentication.SignedOut));
        Assert.Equal(FailureKind.LoggedOut, result.Result.Failure);
        Assert.Null(result.Result.Snapshot);
        Assert.Null(result.Binding);
    }

    [Fact]
    public void SameAccountSelectsNewestWholeSnapshotWithoutDuplicatingAllowance()
    {
        var desktop = Good(ClaudeClient.Desktop, Snapshot(Now.AddMinutes(-1), [new("five_hour", "5 hours", 10, null)]));
        var code = Good(ClaudeClient.Code, Snapshot(Now, [new("weekly", "7 days", 20, Now.AddDays(3))]));
        var result = Resolve(desktop, code);
        Assert.Same(code.Usage.Snapshot, result.Result.Snapshot);
        Assert.Equal("weekly", Assert.Single(result.Result.Snapshot!.Windows).Id);
        Assert.Equal(ClaudeClient.Code, result.Client);
    }

    [Fact]
    public void AccountScopedSourcesCanAgreeWithoutOrganizationIdentifiers()
    {
        var binding = new ClaudeAccountBinding("fixture-account");
        var result = Resolve(Good(ClaudeClient.Desktop) with { Binding = binding }, Good(ClaudeClient.Code) with { Binding = binding });
        Assert.NotNull(result.Result.Snapshot);
        Assert.Equal(binding, result.Binding);
    }

    [Fact]
    public void NewerCachedObservationKeepsItsOriginalTimestampAndCachedStatus()
    {
        var desktop = Good(ClaudeClient.Desktop, Snapshot(Now.AddMinutes(-3), cached: true));
        var code = Good(ClaudeClient.Code, Snapshot(Now.AddMinutes(-4)));
        var first = Resolve(desktop, code);
        var later = ClaudeSourceResolver.Resolve([desktop, code], Now.AddHours(1));
        Assert.Same(desktop.Usage.Snapshot, first.Result.Snapshot);
        Assert.Same(first.Result.Snapshot, later.Result.Snapshot);
        Assert.Equal(Now.AddMinutes(-3), later.Result.Snapshot!.ObservedAt);
        Assert.True(later.Result.Snapshot.IsCached);
        Assert.True(new ProviderState("Claude", ProviderStatus.Ready, later.Result.Snapshot).IsStale(Now));
    }

    [Fact]
    public void EqualTimestampPrefersDirectObservationThenStableClientOrder()
    {
        var desktop = Good(ClaudeClient.Desktop, Snapshot(Now, cached: true));
        var code = Good(ClaudeClient.Code);
        Assert.Equal(ClaudeClient.Code, Resolve(desktop, code).Client);
        Assert.Equal(ClaudeClient.Desktop, Resolve(code, Good(ClaudeClient.Desktop)).Client);
    }

    [Theory]
    [InlineData(FailureKind.Network)]
    [InlineData(FailureKind.Timeout)]
    [InlineData(FailureKind.Malformed)]
    [InlineData(FailureKind.AccessDenied)]
    [InlineData(FailureKind.Unsupported)]
    public void KnownSameAccountUsageFailureDoesNotBlockHealthySource(FailureKind failure)
    {
        var healthy = Good(ClaudeClient.Desktop);
        var failed = Failure(ClaudeClient.Code, ClaudeAuthentication.Authenticated, failure, Account);
        Assert.Same(healthy.Usage.Snapshot, Resolve(failed, healthy).Result.Snapshot);
    }

    [Fact]
    public void KnownIdentitySurvivesUsageFailureForProviderContinuityCheck()
    {
        var resolution = Resolve(Failure(ClaudeClient.Desktop, ClaudeAuthentication.Authenticated, FailureKind.Malformed, Account));
        Assert.Equal(FailureKind.Malformed, resolution.Result.Failure);
        Assert.Equal(Account, resolution.Binding);
        Assert.Null(resolution.Result.Snapshot);
    }

    [Theory]
    [InlineData("other-account", "fixture-organization")]
    [InlineData("fixture-account", "other-organization")]
    [InlineData("FIXTURE-ACCOUNT", "fixture-organization")]
    public void DifferentAccountsOrOrganizationsAreNeverMerged(string accountId, string organizationId)
    {
        var other = Good(ClaudeClient.Code) with { Binding = new(accountId, organizationId) };
        var resolution = Resolve(Good(ClaudeClient.Desktop), other);
        Assert.Equal(FailureKind.Unsupported, resolution.Result.Failure);
        Assert.Equal(ClaudeSourceResolver.MultipleAccountsMessage, resolution.Result.Detail);
        Assert.Null(resolution.Result.Snapshot);
        Assert.Null(resolution.Binding);
        Assert.DoesNotContain(accountId, resolution.Result.Detail!);
        Assert.DoesNotContain(organizationId, resolution.Result.Detail!);
    }

    [Fact]
    public void DifferentAuthenticatedAccountStillBlocksWhenItsUsageFails()
    {
        var other = Failure(ClaudeClient.Code, ClaudeAuthentication.Authenticated, FailureKind.Network,
            new("other-account", "other-organization"));
        Assert.Equal(ClaudeSourceResolver.MultipleAccountsMessage, Resolve(Good(ClaudeClient.Desktop), other).Result.Detail);
    }

    [Fact]
    public void MissingOrganizationOnOnlyOneSideCannotProveSameAllowance()
    {
        var code = Good(ClaudeClient.Code) with { Binding = new(Account.AccountId) };
        AssertAmbiguous(Resolve(Good(ClaudeClient.Desktop), code));
    }

    [Fact]
    public void AccountAndOrganizationIdentifiersAreNeverInterchanged()
    {
        var desktop = Good(ClaudeClient.Desktop) with { Binding = new(null, "same-looking-id") };
        var code = Good(ClaudeClient.Code) with { Binding = new("same-looking-id") };
        var result = Resolve(desktop, code);
        Assert.Same(code.Usage.Snapshot, result.Result.Snapshot);
        Assert.Equal(code.Binding, result.Binding);
        Assert.Equal(ClaudeClient.Code, result.Client);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData(" fixture-account", null)]
    [InlineData("fixture-account", "")]
    [InlineData("fixture-account", " ")]
    public void IncompleteOrWhitespaceIdentityNeverAuthorizesSnapshot(string? accountId, string? organizationId)
    {
        AssertAmbiguous(Resolve(Good(ClaudeClient.Desktop) with { Binding = new(accountId, organizationId) }));
    }

    [Fact]
    public void AuthenticatedWithoutBindingNeverAuthorizesSnapshot() =>
        AssertAmbiguous(Resolve(Good(ClaudeClient.Desktop) with { Binding = null }));

    [Theory]
    [InlineData(ClaudeClient.Desktop, FailureKind.Unsupported)]
    [InlineData(ClaudeClient.Desktop, FailureKind.Malformed)]
    [InlineData(ClaudeClient.Desktop, FailureKind.Timeout)]
    [InlineData(ClaudeClient.Desktop, FailureKind.AccessDenied)]
    [InlineData(ClaudeClient.Code, FailureKind.Unsupported)]
    [InlineData(ClaudeClient.Code, FailureKind.Malformed)]
    [InlineData(ClaudeClient.Code, FailureKind.Timeout)]
    [InlineData(ClaudeClient.Code, FailureKind.AccessDenied)]
    public void UnknownIdentityDoesNotBlockIndependentlyVerifiedSource(ClaudeClient healthyClient, FailureKind failure)
    {
        var healthy = Good(healthyClient);
        var unknown = Failure(Other(healthyClient), ClaudeAuthentication.Unknown, failure);
        var first = Resolve(healthy, unknown);
        var reversed = Resolve(unknown, healthy);
        Assert.Same(healthy.Usage.Snapshot, first.Result.Snapshot);
        Assert.Equal(Account, first.Binding);
        Assert.Equal(healthyClient, first.Client);
        Assert.Equal(first, reversed);
    }

    [Fact]
    public void NewerHistoricalDesktopNumbersCannotReplaceVerifiedCliUsage()
    {
        var historical = Good(ClaudeClient.Desktop, Snapshot(Now, [new("history-only", "Historical window", 99, null)], cached: true)) with
        {
            Authentication = ClaudeAuthentication.Unknown,
            Binding = new("historical-account", "historical-organization")
        };
        var code = Good(ClaudeClient.Code, Snapshot(Now.AddMinutes(-1)));
        var result = Resolve(historical, code);
        Assert.Same(code.Usage.Snapshot, result.Result.Snapshot);
        Assert.Equal(code.Binding, result.Binding);
        Assert.Equal("five_hour", Assert.Single(result.Result.Snapshot!.Windows).Id);
    }

    [Fact]
    public void IncompleteIdentityCandidateCannotBlockVerifiedPeer()
    {
        var unverified = Good(ClaudeClient.Desktop) with { Binding = null };
        var code = Good(ClaudeClient.Code);
        var result = Resolve(unverified, code);
        Assert.Same(code.Usage.Snapshot, result.Result.Snapshot);
        Assert.Equal(Account, result.Binding);
    }

    [Fact]
    public void UnverifiedFailureCannotReplaceKnownAccountFailureOrLoseItsBinding()
    {
        var historical = Failure(ClaudeClient.Desktop, ClaudeAuthentication.Unknown, FailureKind.Malformed);
        var code = Failure(ClaudeClient.Code, ClaudeAuthentication.Authenticated, FailureKind.Network, Account);
        var result = Resolve(historical, code);
        Assert.Equal(FailureKind.Network, result.Result.Failure);
        Assert.Null(result.Result.Snapshot);
        Assert.Equal(Account, result.Binding);
        Assert.Equal(ClaudeClient.Code, result.Client);
    }

    [Fact]
    public void UnknownAuthenticationWithGoodNumbersIsNotProof()
    {
        var unknown = Good(ClaudeClient.Desktop) with { Authentication = ClaudeAuthentication.Unknown };
        AssertAmbiguous(Resolve(unknown));
    }

    [Theory]
    [InlineData(ClaudeAuthentication.Missing)]
    [InlineData(ClaudeAuthentication.SignedOut)]
    public void ContradictoryUnauthenticatedSnapshotOrBindingIsRejected(ClaudeAuthentication authentication)
    {
        var unexpectedSnapshot = Good(ClaudeClient.Desktop) with { Authentication = authentication, Binding = null };
        var unexpectedBinding = Absent(ClaudeClient.Desktop, authentication) with { Binding = Account };
        AssertAmbiguous(Resolve(unexpectedSnapshot));
        AssertAmbiguous(Resolve(unexpectedBinding));
        var code = Good(ClaudeClient.Code);
        Assert.Same(code.Usage.Snapshot, Resolve(unexpectedSnapshot, code).Result.Snapshot);
        Assert.Same(code.Usage.Snapshot, Resolve(unexpectedBinding, code).Result.Snapshot);
    }

    [Theory]
    [InlineData(FailureKind.Malformed)]
    [InlineData(FailureKind.AccessDenied)]
    [InlineData(FailureKind.Unsupported)]
    public void UnknownIdentityWithoutAnySnapshotPreservesSafeFailureDiagnostics(FailureKind failure)
    {
        var source = Failure(ClaudeClient.Desktop, ClaudeAuthentication.Unknown, failure);
        var result = Resolve(source, Absent(ClaudeClient.Code, ClaudeAuthentication.SignedOut));
        Assert.Equal(failure, result.Result.Failure);
        Assert.Equal("Safe fixture diagnostic", result.Result.Detail);
        Assert.Null(result.Result.Snapshot);
        Assert.Null(result.Binding);
    }

    [Fact]
    public void FailureResultNeverPassesItsAttachedSnapshotThrough()
    {
        var failed = Good(ClaudeClient.Desktop) with
        {
            Usage = new(Snapshot(Now), FailureKind.Network, "Safe fixture diagnostic")
        };
        var resolution = Resolve(failed);
        Assert.Null(resolution.Result.Snapshot);
        Assert.Equal(FailureKind.Network, resolution.Result.Failure);
        Assert.Equal(Account, resolution.Binding);
    }

    [Fact]
    public void MissingSnapshotOnSuccessIsMalformed()
    {
        var source = Good(ClaudeClient.Desktop) with { Usage = new(null) };
        Assert.Equal(FailureKind.Malformed, Resolve(source).Result.Failure);
    }

    [Fact]
    public void FutureTimestampIsRejectedAndHealthySameAccountCanWin()
    {
        var future = Good(ClaudeClient.Desktop, Snapshot(Now.AddMinutes(2)));
        Assert.Equal(FailureKind.Malformed, Resolve(future).Result.Failure);
        var healthy = Good(ClaudeClient.Code);
        Assert.Same(healthy.Usage.Snapshot, Resolve(future, healthy).Result.Snapshot);
    }

    [Fact]
    public void SuccessfulEmptyOrMissingObservationRemainsNotReported()
    {
        var empty = Resolve(Good(ClaudeClient.Desktop, Snapshot(Now, []))).Result;
        Assert.Equal(FailureKind.None, empty.Failure);
        Assert.Equal(AllowanceAvailability.NotReported, new ProviderState("Claude Code", ProviderStatus.Ready, empty.Snapshot).Availability);
        var unknown = Snapshot(Now, [new("five_hour", "5 hours", null, null)]);
        Assert.Equal(FailureKind.None, Resolve(Good(ClaudeClient.Desktop, unknown)).Result.Failure);
        Assert.Null(Assert.Single(unknown.Windows).RemainingPercent);
    }

    [Fact]
    public void ResetOnlyObservationRetainsUnknownPercentage()
    {
        var snapshot = Snapshot(Now, [new("five_hour", "5 hours", null, Now.AddHours(1))]);
        var result = Resolve(Good(ClaudeClient.Desktop, snapshot));
        Assert.Same(snapshot, result.Result.Snapshot);
        Assert.Null(Assert.Single(result.Result.Snapshot!.Windows).RemainingPercent);
    }

    [Fact]
    public void NewestPartialObservationWinsWithoutBackfillingOlderPercentage()
    {
        var percentage = Good(ClaudeClient.Desktop, Snapshot(Now.AddMinutes(-1), cached: true));
        var resetOnly = Good(ClaudeClient.Code, Snapshot(Now, [new("five_hour", "5 hours", null, Now.AddHours(2))]));
        var result = Resolve(percentage, resetOnly);
        Assert.Same(resetOnly.Usage.Snapshot, result.Result.Snapshot);
        var window = Assert.Single(result.Result.Snapshot!.Windows);
        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
        Assert.Equal(Now.AddHours(2), window.ResetsAt);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void GenuineZeroAndFullPercentageRemainValid(double used, double remaining)
    {
        var snapshot = Snapshot(Now, [new("five_hour", "5 hours", used, null)]);
        Assert.Equal(remaining, Assert.Single(Resolve(Good(ClaudeClient.Desktop, snapshot)).Result.Snapshot!.Windows).RemainingPercent);
    }

    [Fact]
    public void DuplicateClientAndUnknownEnumAreRejected()
    {
        AssertAmbiguous(Resolve(Good(ClaudeClient.Desktop), Good(ClaudeClient.Desktop)));
        AssertAmbiguous(Resolve(Good((ClaudeClient)999)));
        AssertAmbiguous(Resolve(Good(ClaudeClient.Desktop) with { Authentication = (ClaudeAuthentication)999 }));
    }

    [Fact]
    public void FailureChoiceDoesNotDependOnInputOrder()
    {
        var unsupported = Failure(ClaudeClient.Desktop, ClaudeAuthentication.Authenticated, FailureKind.Unsupported, Account);
        var malformed = Failure(ClaudeClient.Code, ClaudeAuthentication.Authenticated, FailureKind.Malformed, Account);
        Assert.Equal(Resolve(unsupported, malformed), Resolve(malformed, unsupported));
        Assert.Equal(FailureKind.Malformed, Resolve(unsupported, malformed).Result.Failure);
    }

    [Fact]
    public void BindingFormattingDoesNotDiscloseIdentifiers()
    {
        Assert.DoesNotContain(Account.AccountId!, Account.ToString());
        Assert.DoesNotContain(Account.OrganizationId!, Account.ToString());
        Assert.DoesNotContain(Account.AccountId!, Good(ClaudeClient.Desktop).ToString());
        Assert.DoesNotContain(Account.OrganizationId!, Resolve(Good(ClaudeClient.Desktop)).ToString());
    }

    private static ClaudeResolution Resolve(params ClaudeSourceResult[] sources) => ClaudeSourceResolver.Resolve(sources, Now);
    private static ClaudeClient Other(ClaudeClient client) => client == ClaudeClient.Desktop ? ClaudeClient.Code : ClaudeClient.Desktop;
    private static ClaudeSourceResult Good(ClaudeClient client, UsageSnapshot? snapshot = null) =>
        new(client, ClaudeAuthentication.Authenticated, Account, new(snapshot ?? Snapshot(Now)));
    private static ClaudeSourceResult Absent(ClaudeClient client, ClaudeAuthentication authentication = ClaudeAuthentication.Missing) =>
        Failure(client, authentication, authentication == ClaudeAuthentication.SignedOut ? FailureKind.LoggedOut : FailureKind.NotInstalled);
    private static ClaudeSourceResult Failure(ClaudeClient client, ClaudeAuthentication authentication, FailureKind failure,
        ClaudeAccountBinding? binding = null) => new(client, authentication, binding, ProviderResult.Fail(failure, "Safe fixture diagnostic"));
    private static UsageSnapshot Snapshot(DateTimeOffset observedAt, IReadOnlyList<UsageWindow>? windows = null, bool cached = false) =>
        new(windows ?? [new("five_hour", "5 hours", 20, Now.AddHours(1))], observedAt, "Fixture Claude source", cached);
    private static void AssertAmbiguous(ClaudeResolution resolution)
    {
        Assert.Equal(FailureKind.Unsupported, resolution.Result.Failure);
        Assert.Equal(ClaudeSourceResolver.AmbiguousIdentityMessage, resolution.Result.Detail);
        Assert.Null(resolution.Result.Snapshot);
        Assert.Null(resolution.Binding);
    }
}
