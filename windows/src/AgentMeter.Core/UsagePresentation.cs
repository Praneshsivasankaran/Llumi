namespace AgentMeter.Core;

// Keep the provider observation intact. Consumer Claude surfaces recognize only
// the standard five-hour and weekly windows, never names inferred from labels.
public static class UsagePresentation
{
    public static bool IsClaude(string provider) =>
        provider.Equals("Claude", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("Claude Code", StringComparison.OrdinalIgnoreCase);

    public static UsageWindow[] Windows(ProviderState state)
    {
        var windows = state.Snapshot?.Windows ?? [];
        if (!IsClaude(state.Name)) return windows.ToArray();
        return new[] { "five_hour", "seven_day" }.Select(id =>
        {
            var matches = windows.Where(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }).OfType<UsageWindow>().ToArray();
    }

    public static string? ClaudeLabel(UsageWindow window) => window.Id.ToLowerInvariant() switch
    {
        "five_hour" => "5 hours",
        "seven_day" => "7 days",
        _ => null
    };
}
