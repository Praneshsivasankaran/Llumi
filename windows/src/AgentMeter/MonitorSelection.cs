using AgentMeter.Core;

namespace AgentMeter;

// These are existing normalized IDs, not display names or parsed provider output.
// A missing core value stays unknown; a generous bonus window must not replace it.
internal static class MonitorSelection
{
    internal static UsageWindow[] Details(ProviderState state)
    {
        if (!UsagePresentation.IsClaude(state.Name)) return Select(state) is { } main ? [main] : [];
        return UsagePresentation.Windows(state);
    }
    internal static UsageWindow? Select(ProviderState state)
    {
        var windows = state.Snapshot?.Windows;
        if (windows is null) return null;
        if (state.Name.Equals("Codex", StringComparison.OrdinalIgnoreCase))
        {
            var main = windows.Where(w => w.Id.Equals("codex/primary", StringComparison.OrdinalIgnoreCase) || w.Id.Equals("codex/secondary", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (main.Select(w => w.Id.ToLowerInvariant()).Distinct().Count() != main.Length) return null;
            if (main.Any(w => w.DurationMinutes is not null)) return main.OrderByDescending(w => w.DurationMinutes ?? 0).First();
        }
        string[] priority = state.Name.Equals("Codex", StringComparison.OrdinalIgnoreCase)
            ? ["codex/primary", "codex/secondary"]
            : state.Name.Equals("Claude", StringComparison.OrdinalIgnoreCase) ||
              state.Name.Equals("Claude Code", StringComparison.OrdinalIgnoreCase)
                ? ["five_hour"] : [];
        foreach (var id in priority)
        {
            // The supported legacy Codex envelope emits "Codex/primary".
            var matches = windows.Where(window => string.Equals(window.Id, id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length > 1) return null;
            if (matches.Length == 1) return matches[0];
        }
        return null; // New/unknown classifications require an explicit policy, not a guess.
    }
}
