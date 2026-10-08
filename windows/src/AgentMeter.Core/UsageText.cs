using System.Globalization;
using System.Text;

namespace AgentMeter.Core;

public static class UsageText
{
    public static string Provider(ProviderState state, DateTimeOffset now)
    {
        var text = new StringBuilder();
        var cached = state.Snapshot?.IsCached == true;
        var stale = state.IsStale(now);
        var status = PopupText.Status(state, now);
        text.AppendLine(status);
        if (state.Detail is not null) text.AppendLine(state.Detail);
        else if (state.Failure != FailureKind.None) text.AppendLine(FailureText.For(state.Failure));
        if (state.Snapshot is not { } snapshot) return text.Append("Usage and reset times unavailable.").ToString();
        foreach (var window in UsagePresentation.Windows(state))
        {
            var expired = window.ResetPassed(now);
            var remaining = window.RemainingPercent?.ToString("0.#", CultureInfo.InvariantCulture);
            text.AppendLine();
            text.Append(PopupText.WindowName(state.Name, window))
                .Append("   ").Append(remaining is null ? "Unavailable" : remaining + "% remaining");
            if (stale || expired) text.Append(" (stale)");
            text.AppendLine();
            text.Append("Reset: ").AppendLine(Reset(window.ResetsAt, now));
        }
        text.AppendLine();
        text.Append("Observed: ").AppendLine(snapshot.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        text.Append("Source: ").Append(snapshot.Source);
        return text.ToString();
    }

    public static string Reset(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "Unavailable";
        var local = reset.Value.ToLocalTime().ToString("ddd dd MMM HH:mm zzz", CultureInfo.InvariantCulture);
        if (reset <= now) return local + " — reset passed; awaiting provider update";
        var minutes = (long)Math.Ceiling((reset.Value - now).TotalMinutes);
        var duration = minutes >= 1440 ? $"{minutes / 1440}d {minutes % 1440 / 60}h" : minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";
        return $"{local} (in {duration})";
    }

    public static string Tooltip(IReadOnlyList<ProviderState> states, DateTimeOffset now)
    {
        var parts = states.Where(s => s.Enabled).Select(s =>
        {
            var qualifier = PopupText.Status(s, now).ToLowerInvariant();
            return $"{s.Name}: {qualifier}";
        });
        var tooltip = states.All(s => !s.Enabled) ? "Llumi | Monitoring off" : "Llumi | " + string.Join(" | ", parts);
        return tooltip[..Math.Min(63, tooltip.Length)];
    }
}
