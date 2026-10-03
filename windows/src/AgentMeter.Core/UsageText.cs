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
        var status = state.Status == ProviderStatus.Loading ? "Loading…" : state.Failure != FailureKind.None
            ? FailureText.For(state.Failure) : cached ? "Cached observation" : stale ? "Stale" : "Live";
        text.AppendLine(status);
        if (state.Detail is not null) text.AppendLine(state.Detail);
        if (state.Snapshot is not { } snapshot) return text.Append("Usage and reset times unavailable.").ToString();
        foreach (var window in UsagePresentation.Windows(state))
        {
            var expired = window.ResetPassed(now);
            var remaining = window.RemainingPercent?.ToString("0.#", CultureInfo.InvariantCulture);
            text.AppendLine();
            text.Append(UsagePresentation.IsClaude(state.Name) ? PopupText.WindowName(state.Name, window) : window.Name)
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
        var parts = states.Select(s =>
        {
            var qualifier = s.Status == ProviderStatus.Loading ? "loading" : s.Failure != FailureKind.None ? "unavailable" : s.IsStale(now) ? "cached/stale" : "live";
            return $"{s.Name}: {qualifier}";
        });
        var tooltip = "Llumi | " + string.Join(" | ", parts);
        return tooltip[..Math.Min(63, tooltip.Length)];
    }
}
