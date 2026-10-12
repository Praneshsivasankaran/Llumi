using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class UsageAlertEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly UsageAlertSettings On = new(Enabled: true);
    private static string Pseudonym(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    private static ProviderState State(double used, DateTimeOffset? at = null, DateTimeOffset? reset = null,
        string account = "synthetic-account-a", string provider = "Codex")
    {
        var observation = at ?? Now;
        return new(provider, ProviderStatus.Ready,
            new UsageSnapshot([new("synthetic-window", "Synthetic window", used, reset ?? Now.AddHours(5), 300,
                UsageScope.General, bucket: "synthetic-bucket")], observation, "synthetic") { Binding = new(account) },
            Authentication: AuthenticationStatus.Verified);
    }
    private static AlertEvaluation Evaluate(ProviderState state, AlertLedger? ledger = null,
        UsageAlertSettings? settings = null, DateTimeOffset? now = null) =>
        UsageAlertEngine.Evaluate([state], settings ?? On, now ?? state.Snapshot!.ObservedAt,
            ledger ?? new(), Pseudonym);

    [Fact]
    public void DefaultsAreOffAndInvalidConfigurationsFailClosed()
    {
        var settings = new UsageAlertSettings();
        Assert.False(settings.Enabled);
        Assert.True(settings.IsValid);
        Assert.Equal([75, 90, 95], settings.EffectiveThresholds.Select(t => t.Percent));
        settings.EffectiveThresholds[0] = new(1);
        Assert.Equal(75, settings.EffectiveThresholds[0].Percent);
        Assert.False(Evaluate(State(99), settings: settings).Changed);
        foreach (var thresholds in new AlertThreshold[][] { [], [new(0)], [new(101)], [new(75), new(75, false)], [null!] })
        {
            var result = Evaluate(State(99), settings: On with { Thresholds = thresholds });
            Assert.Equal(AlertEvaluationFailure.InvalidSettings, result.Failure);
            Assert.Empty(result.Notices);
            Assert.False(result.Changed);
        }
    }

    [Theory]
    [InlineData(74.999, 0)]
    [InlineData(75, 75)]
    [InlineData(89.999, 75)]
    [InlineData(90, 90)]
    [InlineData(95, 95)]
    [InlineData(100, 95)]
    public void ExactBoundaryAndJumpsEmitOnlyHighestNewThreshold(double used, int expected)
    {
        var result = Evaluate(State(used));
        Assert.Equal(AlertEvaluationFailure.None, result.Failure);
        if (expected == 0) Assert.Empty(result.Notices);
        else Assert.Equal(expected, Assert.Single(result.Notices).ThresholdPercent);
        Assert.Equal(Enumerable.Range(1, expected), Assert.Single(result.Ledger.EffectiveEntries).ConsumedThresholds);
    }

    [Fact]
    public void PercentDropsRepeatedReadsAndRestartNeverReplayConsumedLevels()
    {
        var first = Evaluate(State(96));
        Assert.Equal(95, Assert.Single(first.Notices).ThresholdPercent);
        var serialized = JsonSerializer.Serialize(first.Ledger);
        Assert.DoesNotContain("synthetic-account", serialized);
        Assert.DoesNotContain("synthetic-window", serialized);
        Assert.DoesNotContain("Codex", serialized);
        Assert.DoesNotContain("UsedPercent", serialized);
        var ledger = JsonSerializer.Deserialize<AlertLedger>(serialized)!;
        Assert.True(ledger.IsValid);
        Assert.Empty(Evaluate(State(96), ledger).Notices);
        var dip = Evaluate(State(20, Now.AddMinutes(1)), ledger);
        var recovered = Evaluate(State(99, Now.AddMinutes(2)), dip.Ledger);
        Assert.Empty(recovered.Notices);
        Assert.Equal(Enumerable.Range(1, 95), Assert.Single(recovered.Ledger.EffectiveEntries).ConsumedThresholds);
    }

    [Fact]
    public void ProposalsNeverMutateTheOldLedgerBeforeSuccessfulPersistence()
    {
        var first = Evaluate(State(80));
        var before = JsonSerializer.Serialize(first.Ledger);
        var proposed = Evaluate(State(92, Now.AddSeconds(30)), first.Ledger);
        Assert.Equal(90, Assert.Single(proposed.Notices).ThresholdPercent);
        Assert.Equal(before, JsonSerializer.Serialize(first.Ledger));
        Assert.NotSame(first.Ledger.EffectiveEntries[0].ConsumedThresholds, proposed.Ledger.EffectiveEntries[0].ConsumedThresholds);
        // A caller that failed to save keeps the old ledger and must not dispatch.
        Assert.Equal(90, Assert.Single(Evaluate(State(92, Now.AddSeconds(30)), first.Ledger).Notices).ThresholdPercent);
    }

    [Fact]
    public void SnoozeConsumesObservedCrossingsWithoutBacklogAfterRestartOrExpiry()
    {
        var snooze = On with { SnoozedUntil = Now.AddHours(1) };
        var first = Evaluate(State(80), settings: snooze);
        Assert.Empty(first.Notices);
        Assert.Equal(Enumerable.Range(1, 75), Assert.Single(first.Ledger.EffectiveEntries).ConsumedThresholds);
        var ledger = JsonSerializer.Deserialize<AlertLedger>(JsonSerializer.Serialize(first.Ledger))!;
        var baseline = Evaluate(State(80, Now.AddHours(1)), ledger, snooze);
        Assert.Empty(baseline.Notices);
        Assert.False(snooze.IsSnoozed(Now.AddHours(1)));
        var next = Evaluate(State(91, Now.AddHours(1).AddSeconds(30)), baseline.Ledger, snooze);
        Assert.Equal(90, Assert.Single(next.Notices).ThresholdPercent);
        var resumed = Evaluate(State(91, Now.AddHours(1).AddMinutes(1)), next.Ledger, snooze);
        Assert.Empty(resumed.Notices);
    }

    [Fact]
    public void FirstHighObservationAfterEntirelyOfflineSnoozeBaselinesEachWindow()
    {
        var initial = Evaluate(State(60));
        var end = Now.AddHours(1);
        var snooze = On with { SnoozedUntil = end };
        // No evaluator call occurs during the entire snooze, and the app restarts.
        var ledger = JsonSerializer.Deserialize<AlertLedger>(JsonSerializer.Serialize(initial.Ledger))!;
        var after = Evaluate(State(91, end.AddSeconds(1)), ledger, snooze);
        Assert.Empty(after.Notices);
        Assert.Equal(end, Assert.Single(after.Ledger.EffectiveEntries).AppliedSnoozeUntil);
        Assert.Equal(Enumerable.Range(1, 90), Assert.Single(after.Ledger.EffectiveEntries).ConsumedThresholds);
        var subsequent = Evaluate(State(96, end.AddSeconds(31)), after.Ledger, snooze);
        Assert.Equal(95, Assert.Single(subsequent.Notices).ThresholdPercent);
        // A slower second provider gets its own baseline, not the first provider's.
        var slowProvider = Evaluate(State(96, end.AddMinutes(1), provider: "Claude Code"), subsequent.Ledger, snooze);
        Assert.Empty(slowProvider.Notices);
    }

    [Fact]
    public void EarlyResumeBaselinesEvenWithoutAnObservationWhileSnoozed()
    {
        var initial = Evaluate(State(60));
        // UI persists the explicit resume instant, not null, even if no query ran.
        var resumed = On with { SnoozedUntil = Now.AddMinutes(5) };
        var first = Evaluate(State(91, Now.AddMinutes(5).AddSeconds(1)), initial.Ledger, resumed);
        Assert.Empty(first.Notices);
        var next = Evaluate(State(96, Now.AddMinutes(6)), first.Ledger, resumed);
        Assert.Equal(95, Assert.Single(next.Notices).ThresholdPercent);
    }

    [Fact]
    public void PreExpiryObservationProcessedAfterExpiryDoesNotFinishSnoozeBaseline()
    {
        var end = Now.AddHours(1);
        var snooze = On with { SnoozedUntil = end };
        var staleAtBoundary = Evaluate(State(80, end.AddSeconds(-1)), settings: snooze, now: end.AddSeconds(1));
        Assert.Empty(staleAtBoundary.Notices);
        Assert.Null(Assert.Single(staleAtBoundary.Ledger.EffectiveEntries).AppliedSnoozeUntil);
        var firstFresh = Evaluate(State(91, end.AddSeconds(20)), staleAtBoundary.Ledger, snooze);
        Assert.Empty(firstFresh.Notices);
        Assert.Equal(end, Assert.Single(firstFresh.Ledger.EffectiveEntries).AppliedSnoozeUntil);
        var later = Evaluate(State(96, end.AddMinutes(1)), firstFresh.Ledger, snooze);
        Assert.Equal(95, Assert.Single(later.Notices).ThresholdPercent);
    }

    [Fact]
    public void ThresholdEditsCanEvaluateCurrentFreshReadingButReaddingDoesNotReplay()
    {
        var initial = On with { Thresholds = [new(75), new(90), new(95, false)] };
        var state = State(96);
        var first = Evaluate(state, settings: initial);
        Assert.Equal(90, Assert.Single(first.Notices).ThresholdPercent);
        var edited = initial with { Thresholds = [new(75), new(92), new(95, false)] };
        var changed = Evaluate(state, first.Ledger, edited);
        Assert.Equal(92, Assert.Single(changed.Notices).ThresholdPercent);
        var restored = Evaluate(state, changed.Ledger, initial);
        Assert.Empty(restored.Notices);
        var enabled = Evaluate(state, restored.Ledger, On);
        Assert.Equal(95, Assert.Single(enabled.Notices).ThresholdPercent);
        Assert.Empty(Evaluate(state, enabled.Ledger, On).Notices);
    }

    [Fact]
    public void EnablingOrAddingLowerThresholdAfterHigherWarningDoesNotReplay()
    {
        var highOnly = On with { Thresholds = [new(75, false), new(90, false), new(95)] };
        var first = Evaluate(State(96), settings: highOnly);
        Assert.Equal(95, Assert.Single(first.Notices).ThresholdPercent);
        Assert.Empty(Evaluate(State(96), first.Ledger, On).Notices);
        var editedLower = On with { Thresholds = [new(72), new(85), new(99)] };
        var current = Evaluate(State(96), first.Ledger, editedLower);
        Assert.Empty(current.Notices);
        var later = Evaluate(State(99, Now.AddSeconds(30)), current.Ledger, editedLower);
        Assert.Equal(99, Assert.Single(later.Notices).ThresholdPercent);
    }

    [Fact]
    public void MasterProviderAndMonitoringTogglesDoNotEraseDeduplication()
    {
        var first = Evaluate(State(96));
        foreach (var settings in new[] { On with { Enabled = false }, On with { CodexEnabled = false } })
        {
            var off = Evaluate(State(99, Now.AddSeconds(30)), first.Ledger, settings);
            Assert.Same(first.Ledger, off.Ledger);
            Assert.Empty(off.Notices);
        }
        Assert.Empty(Evaluate(State(99) with { Enabled = false }, first.Ledger).Notices);
        Assert.Empty(Evaluate(State(99, Now.AddMinutes(1)), first.Ledger, On).Notices);
        Assert.False(On.IsEnabledFor("Unknown provider"));
        Assert.True(On.IsEnabledFor("Claude Code"));
    }

    [Fact]
    public void EachAccountWindowAndProviderRetainsItsOwnReservation()
    {
        var a = Evaluate(State(80));
        var b = Evaluate(State(80, Now.AddSeconds(30), account: "synthetic-account-b"), a.Ledger);
        Assert.Single(b.Notices);
        Assert.Empty(Evaluate(State(80, Now.AddMinutes(1)), b.Ledger).Notices);
        var claude = Evaluate(State(80, Now.AddMinutes(1), provider: "Claude Code"), b.Ledger);
        Assert.Single(claude.Notices);
        Assert.Equal(3, claude.Ledger.EffectiveEntries.Length);
        var state = State(96);
        state = state with { Snapshot = state.Snapshot! with { Windows = [
            state.Snapshot!.Windows[0], new("synthetic-weekly", "Synthetic weekly", 91, Now.AddDays(7), 10080, UsageScope.General)] } };
        var windows = Evaluate(state);
        Assert.Equal(2, windows.Notices.Length);
        Assert.Contains(windows.Notices, n => n.WindowLabel == "5-hour limit" && n.ThresholdPercent == 95);
        Assert.Contains(windows.Notices, n => n.WindowLabel == "Weekly limit" && n.ThresholdPercent == 90);
    }

    [Fact]
    public void FutureResetCorrectionsNeverRearmAndLaterVerifiedWindowDoes()
    {
        var end = Now.AddHours(5);
        var first = Evaluate(State(96));
        var correction = Evaluate(State(96, Now.AddMinutes(1), end.AddMinutes(10)), first.Ledger);
        Assert.Empty(correction.Notices);
        Assert.Equal(end.AddMinutes(10), Assert.Single(correction.Ledger.EffectiveEntries).RearmAfter);
        // Passing the old timestamp still cannot rearm a corrected, unexpired window.
        var stillSame = Evaluate(State(96, end.AddMinutes(1), end.AddMinutes(20)), correction.Ledger);
        Assert.Empty(stillSame.Notices);
        var later = end.AddMinutes(21);
        var renewed = Evaluate(State(96, later, end.AddHours(5)), stillSame.Ledger);
        Assert.Equal(95, Assert.Single(renewed.Notices).ThresholdPercent);
        Assert.Empty(Evaluate(State(99, later.AddSeconds(30), end.AddHours(5)), renewed.Ledger).Notices);
    }

    [Fact]
    public void DurationMetadataCorrectionDoesNotCreateANewLedgerScope()
    {
        var initial = Evaluate(State(96));
        var corrected = State(96, Now.AddSeconds(30));
        corrected = corrected with { Snapshot = corrected.Snapshot! with { Windows = [
            new("synthetic-window", "Synthetic window", 96, Now.AddHours(5), 301,
                UsageScope.General, bucket: "synthetic-bucket")] } };
        var result = Evaluate(corrected, initial.Ledger);
        Assert.Empty(result.Notices);
        Assert.Single(result.Ledger.EffectiveEntries);
    }

    [Fact]
    public void CodexMapAvailabilityDoesNotReplayTheSameLegacyMainWindow()
    {
        var reset = Now.AddHours(5).ToUnixTimeSeconds();
        var window = new { usedPercent = 96, resetsAt = reset, windowDurationMins = 300 };
        ProviderState Parsed(object payload, DateTimeOffset at)
        {
            var parsed = CodexParser.Parse(JsonSerializer.SerializeToElement(payload), at);
            Assert.Equal(FailureKind.None, parsed.Failure);
            return new("Codex", ProviderStatus.Ready, parsed.Snapshot! with { Binding = new("synthetic-account-a") },
                Authentication: AuthenticationStatus.Verified);
        }
        var legacyOnly = Parsed(new { rateLimits = new { primary = window } }, Now);
        Assert.Equal("codex/primary", Assert.Single(legacyOnly.Snapshot!.Windows).Id);
        var initial = Evaluate(legacyOnly);
        Assert.Single(initial.Notices);
        var gainedMap = Parsed(new {
            rateLimits = new { primary = window },
            rateLimitsByLimitId = new { spark = new { primary = new { usedPercent = 0, resetsAt = reset, windowDurationMins = 300 } } }
        }, Now.AddSeconds(30));
        Assert.Contains(gainedMap.Snapshot!.Windows, w => w.Id == "codex/legacy/primary");
        var changed = Evaluate(gainedMap, initial.Ledger);
        Assert.Empty(changed.Notices);
        var lostMap = Parsed(new { rateLimits = new { primary = window } }, Now.AddMinutes(1));
        Assert.Empty(Evaluate(lostMap, changed.Ledger).Notices);
    }

    [Fact]
    public void EarlierCorrectionsOutOfOrderAndClockRollbackStayConservative()
    {
        var first = Evaluate(State(80));
        var earlierReset = Evaluate(State(96, Now.AddMinutes(1), Now.AddHours(4)), first.Ledger);
        Assert.Empty(earlierReset.Notices);
        Assert.Equal(Now.AddHours(5), Assert.Single(earlierReset.Ledger.EffectiveEntries).ResetAt);
        var lateOld = Evaluate(State(96, Now.AddSeconds(30)), earlierReset.Ledger, now: Now.AddMinutes(1));
        Assert.False(lateOld.Changed);
        Assert.Empty(lateOld.Notices);
        var rollback = Evaluate(State(96, Now.AddSeconds(10)), earlierReset.Ledger, now: Now.AddSeconds(10));
        Assert.False(rollback.Changed);
        var contradictory = Evaluate(State(96, Now.AddMinutes(1), Now.AddHours(6)), earlierReset.Ledger);
        Assert.False(contradictory.Changed);
    }

    [Fact]
    public void MissingInvalidStaleCachedAndUnverifiedDataNeverAlert()
    {
        var good = State(96);
        var invalid = new[] {
            good with { Status = ProviderStatus.Loading },
            good with { Failure = FailureKind.Network },
            good with { Authentication = AuthenticationStatus.Unknown },
            good with { Snapshot = good.Snapshot! with { Binding = null } },
            good with { Snapshot = good.Snapshot! with { IsCached = true } },
            good with { Snapshot = good.Snapshot! with { ObservedAt = Now.AddMinutes(-3) } },
            good with { Snapshot = good.Snapshot! with { ObservedAt = Now.AddSeconds(1) } },
            good with { Snapshot = good.Snapshot! with { Windows = [new("missing", "Missing", 96, null, 300, UsageScope.General)] } },
            good with { Snapshot = good.Snapshot! with { Windows = [new("expired", "Expired", 96, Now, 300, UsageScope.General)] } },
            good with { Snapshot = good.Snapshot! with { Windows = [new("invalid", "Invalid", double.NaN, Now.AddHours(5), 300, UsageScope.General)] } },
            good with { Snapshot = good.Snapshot! with { Windows = [new("unsupported", "Unsupported", 96, Now.AddHours(5))] } }
        };
        foreach (var state in invalid)
        {
            var result = Evaluate(state, now: Now);
            Assert.Empty(result.Notices);
            Assert.False(result.Changed);
        }
    }

    [Fact]
    public void ExpiredSiblingWindowDoesNotSuppressFreshVerifiedWeeklyAllowance()
    {
        var state = State(96);
        state = state with { Snapshot = state.Snapshot! with { Windows = [
            new("five-hour", "5-hour limit", 96, Now.AddSeconds(-1), 300, UsageScope.General),
            new("weekly", "Weekly limit", 91, Now.AddDays(7), 10080, UsageScope.General)] } };
        Assert.True(state.IsStale(Now)); // The UI's aggregate status includes the expired sibling.
        var result = Evaluate(state);
        var notice = Assert.Single(result.Notices);
        Assert.Equal("Weekly limit", notice.WindowLabel);
        Assert.Equal(90, notice.ThresholdPercent);
        Assert.Single(result.Ledger.EffectiveEntries);
        var staleObservation = state with { Snapshot = state.Snapshot! with { ObservedAt = Now.AddMinutes(-3) } };
        Assert.Empty(Evaluate(staleObservation, now: Now).Notices);
    }

    [Fact]
    public void CorruptLedgerOrBadPseudonymFailsWithoutClearingReservations()
    {
        var first = Evaluate(State(96));
        var entry = first.Ledger.EffectiveEntries[0];
        foreach (var ledger in new[] {
            new AlertLedger(Version: 2),
            new AlertLedger(Entries: [entry, entry]),
            new AlertLedger(Entries: [entry with { Key = "raw-account" }]),
            new AlertLedger(Entries: [entry with { ConsumedThresholds = [75, 75] }]),
            new AlertLedger(Entries: [entry with { ObservedAt = entry.ResetAt }]) })
        {
            var result = Evaluate(State(99), ledger);
            Assert.Equal(AlertEvaluationFailure.InvalidLedger, result.Failure);
            Assert.Same(ledger, result.Ledger);
            Assert.Empty(result.Notices);
        }
        var badHash = UsageAlertEngine.Evaluate([State(96)], On, Now, new(), _ => "not-a-protected-pseudonym");
        Assert.Equal(AlertEvaluationFailure.InvalidPseudonym, badHash.Failure);
        Assert.False(badHash.Changed);
    }

    [Fact]
    public void CapacityFailureRollsBackWholeProposalInsteadOfEvictingReservations()
    {
        var entries = Enumerable.Range(0, AlertLedger.MaximumEntries - 1).Select(i => new AlertLedgerEntry(
            Pseudonym("synthetic-existing-" + i), Now.AddHours(5), Now.AddHours(5), Now, [75])).ToArray();
        var ledger = new AlertLedger(Entries: entries);
        var before = JsonSerializer.Serialize(ledger);
        var result = UsageAlertEngine.Evaluate([State(96), State(96, provider: "Claude Code")], On, Now, ledger, Pseudonym);
        Assert.Equal(AlertEvaluationFailure.Capacity, result.Failure);
        Assert.Empty(result.Notices);
        Assert.False(result.Changed);
        Assert.Same(ledger, result.Ledger);
        Assert.Equal(before, JsonSerializer.Serialize(ledger));
    }
}
