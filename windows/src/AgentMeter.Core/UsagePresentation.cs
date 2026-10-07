namespace AgentMeter.Core;

public static class UsagePresentation
{
    public static bool IsClaude(string provider) => provider.Equals("Claude", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("Claude Code", StringComparison.OrdinalIgnoreCase);

    public static UsageWindow[] Windows(ProviderState state) => !state.Enabled ? [] :
        (state.Snapshot?.Windows ?? []).Where(w => w.IsSupported).GroupBy(w => w.Id, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.All(w => w == g.First()) ? g.First() : null).OfType<UsageWindow>()
        .OrderBy(w => w.Scope == UsageScope.General ? 0 : 1).ThenBy(Rank).ThenBy(w => w.DurationMinutes ?? long.MaxValue)
        .ThenBy(w => w.Bucket, StringComparer.Ordinal).ThenBy(w => w.Id, StringComparer.Ordinal).ToArray();

    private static int Rank(UsageWindow window) => window.DurationMinutes switch { 300 => 0, 10080 => 1, _ => 2 };
    public static UsageWindow? Primary(ProviderState state) => Windows(state).FirstOrDefault(w => w.IsUsableGeneral);
    public static AllowanceAvailability Availability(ProviderState state)
    {
        var windows = Windows(state);
        if (windows.Any(w => w.UsedPercent is not null)) return AllowanceAvailability.Reported;
        if (windows.Length > 0) return AllowanceAvailability.NotReported;
        return state.Snapshot?.Windows.Count > 0 ? AllowanceAvailability.UnsupportedFormat : AllowanceAvailability.NotReported;
    }
    public static string? ClaudeLabel(UsageWindow window) => window.IsSupported ? Label(window) : null;
    public static string Label(UsageWindow window)
    {
        if (!window.IsSupported) return "";
        var name = string.IsNullOrWhiteSpace(window.ScopeLabel) ? null : window.ScopeLabel.Trim();
        return window.Scope switch
        {
            UsageScope.Model => name ?? "Model limit",
            UsageScope.Additional => name is null ? "Additional limit" :
                name.EndsWith(" limit", StringComparison.OrdinalIgnoreCase) ? name : name + " limit",
            _ => DurationLabel(window)
        };
    }

    public static string AccessibilityLabel(UsageWindow window)
    {
        var label = Label(window);
        if (!window.IsSupported || window.Scope == UsageScope.General) return label;
        var scope = window.Scope == UsageScope.Model ? "model" : "additional";
        var period = DurationLabel(window);
        var detail = window.DurationMinutes is null ? $"{scope} allowance · Period not reported" :
            period[..^" limit".Length] + $" {scope} allowance";
        return label + " · " + detail;
    }

    private static string DurationLabel(UsageWindow window) => window.DurationMinutes switch
        {
            300 => "5-hour limit", 10080 => "Weekly limit", null => "Usage limit · Period not reported",
            var n when n % 1440 == 0 => $"{n / 1440}-day limit",
            var n when n % 60 == 0 => $"{n / 60}-hour limit", var n => $"{n}-minute limit"
        };
}
