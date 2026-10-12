using System.Globalization;
using System.Text.Json.Serialization;

namespace AgentMeter.Core;

public sealed record AlertThreshold(int Percent, bool Enabled = true);

public sealed record UsageAlertSettings(bool Enabled = false, bool CodexEnabled = true, bool ClaudeEnabled = true,
    AlertThreshold[]? Thresholds = null, DateTimeOffset? SnoozedUntil = null)
{
    [JsonIgnore] public AlertThreshold[] EffectiveThresholds => Thresholds?.ToArray() ?? [new(75), new(90), new(95)];
    [JsonIgnore] public bool IsValid => Thresholds is null || Thresholds.Length is >= 1 and <= 16 &&
        Thresholds.All(t => t is not null && t.Percent is >= 1 and <= 100) &&
        Thresholds.Select(t => t.Percent).Distinct().Count() == Thresholds.Length;
    public bool IsSnoozed(DateTimeOffset now) => SnoozedUntil is { } until && now < until;
    public bool IsEnabledFor(string provider) => Enabled && (provider switch
    {
        "Codex" => CodexEnabled,
        "Claude Code" or "Claude" => ClaudeEnabled,
        _ => false
    });
}

// These keys are produced by the protected store's keyed pseudonymizer. Neither
// provider/account identifiers nor window labels belong in this persistence model.
// Consumed thresholds are also delivery reservations: the caller saves the entire
// result atomically before submitting any of its notices to Windows.
public sealed record AlertLedgerEntry(string Key, DateTimeOffset ResetAt, DateTimeOffset RearmAfter,
    DateTimeOffset ObservedAt, int[] ConsumedThresholds, DateTimeOffset? AppliedSnoozeUntil = null);

public sealed record AlertLedger(int Version = 1, AlertLedgerEntry[]? Entries = null)
{
    public const int MaximumEntries = 256;
    [JsonIgnore] public AlertLedgerEntry[] EffectiveEntries => Entries?.ToArray() ?? [];
    [JsonIgnore] public bool IsValid => Version == 1 && (Entries is null || Entries.Length <= MaximumEntries &&
        Entries.All(ValidEntry) && Entries.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Entries.Length);

    internal static bool IsPseudonym(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool ValidEntry(AlertLedgerEntry? entry) => entry is not null && IsPseudonym(entry.Key) &&
        entry.ObservedAt < entry.ResetAt && entry.ResetAt <= entry.RearmAfter &&
        (entry.AppliedSnoozeUntil is null || entry.AppliedSnoozeUntil <= entry.ObservedAt) &&
        entry.ConsumedThresholds is { Length: <= 100 } thresholds &&
        thresholds.All(t => t is >= 1 and <= 100) && thresholds.Distinct().Count() == thresholds.Length;
}

public sealed record UsageAlertNotice(string Provider, string WindowLabel, int ThresholdPercent,
    double UsedPercent, DateTimeOffset ResetsAt, string ReservationKey);

public enum AlertEvaluationFailure { None, InvalidSettings, InvalidLedger, Capacity, InvalidPseudonym }
public sealed record AlertEvaluation(AlertLedger Ledger, UsageAlertNotice[] Notices, bool Changed,
    AlertEvaluationFailure Failure = AlertEvaluationFailure.None);

public static class UsageAlertEngine
{
    public static AlertEvaluation Evaluate(IEnumerable<ProviderState> states, UsageAlertSettings settings,
        DateTimeOffset now, AlertLedger ledger, Func<string, string> pseudonymize)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(pseudonymize);
        AlertEvaluation Fail(AlertEvaluationFailure failure) => new(ledger, [], false, failure);
        if (!settings.IsValid) return Fail(AlertEvaluationFailure.InvalidSettings);
        if (!ledger.IsValid) return Fail(AlertEvaluationFailure.InvalidLedger);
        if (!settings.Enabled) return new(ledger, [], false);

        var entries = ledger.EffectiveEntries.ToDictionary(e => e.Key, e => e, StringComparer.OrdinalIgnoreCase);
        var notices = new List<UsageAlertNotice>();
        var changed = false;
        var thresholds = settings.EffectiveThresholds.Where(t => t.Enabled).Select(t => t.Percent).Order().ToArray();
        // Ambiguous duplicate provider state is not a second observation.
        foreach (var group in states.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            if (group.Count() != 1) continue;
            var state = group.First();
            if (!settings.IsEnabledFor(state.Name) || !state.Enabled || state.Status != ProviderStatus.Ready ||
                state.Failure != FailureKind.None || state.Authentication != AuthenticationStatus.Verified ||
                state.Snapshot is not { IsCached: false, Binding.Digest.Length: > 0 } snapshot ||
                snapshot.ObservedAt > now || now - snapshot.ObservedAt > TimeSpan.FromMinutes(2)) continue;

            foreach (var window in UsagePresentation.Windows(state))
            {
                // Reset validity belongs to this window. An expired sibling does
                // not invalidate another allowance in a fresh verified observation.
                if (window.UsedPercent is not { } used || window.ResetsAt is not { } reset || reset <= now ||
                    string.IsNullOrEmpty(window.Id)) continue;
                var provider = UsagePresentation.IsClaude(state.Name) ? "Claude Code" : state.Name;
                // The Codex parser retains unmatched legacy mirrors for display.
                // Their /legacy/ identity can appear solely because a map gained
                // another bucket, or represent a distinct unmatched limit. Without
                // proof of continuity neither aliasing nor treating one as a new
                // alert window is safe. Continue showing it in Usage, but do not
                // automatically alert from that ambiguous identity.
                if (provider == "Codex" && window.Id.Contains("/legacy/", StringComparison.Ordinal)) continue;
                // A duration correction does not change the account/window identity.
                var identity = Frame(provider, snapshot.Binding.Digest, window.Id, window.Bucket,
                    ((int)window.Scope).ToString(CultureInfo.InvariantCulture));
                var key = pseudonymize(identity);
                if (!AlertLedger.IsPseudonym(key)) return Fail(AlertEvaluationFailure.InvalidPseudonym);
                key = key.ToUpperInvariant();

                entries.TryGetValue(key, out var previous);
                // Clock rollback, late completion and same-time contradictory reset
                // metadata cannot establish a new quota window.
                if (previous is not null && (snapshot.ObservedAt < previous.ObservedAt || now < previous.ObservedAt ||
                    snapshot.ObservedAt == previous.ObservedAt && reset != previous.ResetAt)) continue;
                if (previous is null && entries.Count == AlertLedger.MaximumEntries)
                    return Fail(AlertEvaluationFailure.Capacity);

                if (previous is not null && reset < previous.ResetAt)
                {
                    // An earlier reset correction is uncertain. Preserve its previous
                    // reset high-water mark and consumed thresholds, suppress alerts,
                    // but fence older observations that might arrive subsequently.
                    if (snapshot.ObservedAt > previous.ObservedAt)
                    {
                        entries[key] = previous with { ObservedAt = snapshot.ObservedAt };
                        changed = true;
                    }
                    continue;
                }

                var renewed = previous is not null && snapshot.ObservedAt > previous.ObservedAt &&
                    snapshot.ObservedAt >= previous.RearmAfter && reset > previous.RearmAfter;
                var handledThrough = previous is null || renewed ? 0 : previous.ConsumedThresholds.DefaultIfEmpty(0).Max();
                var crossed = thresholds.Where(t => used >= t && t > handledThrough).ToArray();
                // An alert at 95% also handles every lesser possible threshold,
                // including disabled or later-added settings. Editing thresholds
                // must not produce a less urgent warning for the same window.
                if (crossed.Length != 0) handledThrough = crossed[^1];
                var rearm = previous is null || renewed || reset > previous.RearmAfter ? reset : previous.RearmAfter;
                var appliedSnooze = previous?.AppliedSnoozeUntil;
                var needsSnoozeBaseline = settings.SnoozedUntil is { } deadline && appliedSnooze != deadline;
                // Crossing times while asleep/offline are unknowable. The first new
                // observation after a snooze conservatively establishes a baseline;
                // pre-expiry observations processed late cannot complete that step.
                // Resume retains its end instant in settings rather than clearing it.
                if (needsSnoozeBaseline && settings.SnoozedUntil is { } ended && now >= ended && snapshot.ObservedAt >= ended)
                    appliedSnooze = ended.ToUniversalTime();
                var next = new AlertLedgerEntry(key, reset.ToUniversalTime(), rearm.ToUniversalTime(),
                    snapshot.ObservedAt.ToUniversalTime(), Enumerable.Range(1, handledThrough).ToArray(), appliedSnooze);
                if (previous is null || previous.ResetAt != next.ResetAt || previous.RearmAfter != next.RearmAfter ||
                    previous.ObservedAt != next.ObservedAt || previous.AppliedSnoozeUntil != next.AppliedSnoozeUntil ||
                    !previous.ConsumedThresholds.SequenceEqual(next.ConsumedThresholds))
                {
                    entries[key] = next;
                    changed = true;
                }
                if (crossed.Length == 0 || settings.IsSnoozed(now) || needsSnoozeBaseline) continue;

                var highest = crossed[^1];
                var reservation = pseudonymize(Frame(key, next.ResetAt.ToString("O", CultureInfo.InvariantCulture),
                    highest.ToString(CultureInfo.InvariantCulture)));
                if (!AlertLedger.IsPseudonym(reservation)) return Fail(AlertEvaluationFailure.InvalidPseudonym);
                notices.Add(new(provider, UsagePresentation.Label(window), highest, used, reset, reservation.ToUpperInvariant()));
            }
        }
        if (!changed) return new(ledger, [], false);
        // Clone all mutable arrays so neither evaluating nor subsequently saving a
        // proposal can mutate the old ledger if persistence fails.
        return new(new(Entries: entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e with { ConsumedThresholds = e.ConsumedThresholds.ToArray() }).ToArray()), notices.ToArray(), true);
    }

    private static string Frame(params string[] parts) => string.Concat(parts.Select(p =>
        p.Length.ToString(CultureInfo.InvariantCulture) + ":" + p));
}
