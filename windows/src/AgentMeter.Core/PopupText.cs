using System.Globalization;

namespace AgentMeter.Core;

// Presentation consumes normalized values; no provider response parsing belongs here.
public static class PopupText
{
    public static string Status(ProviderState state, DateTimeOffset now) =>
        !state.Enabled ? "Monitoring off" :
        state.Status == ProviderStatus.Loading ? state.Snapshot is null ? "Checking…" : "Refreshing…" :
        state.Snapshot is not null && state.IsStale(now) ? "Stale" :
        state.Failure switch
        {
            FailureKind.NotInstalled => "Not installed", FailureKind.LoggedOut => "Not signed in",
            FailureKind.UnsupportedBilling => "Unsupported billing", FailureKind.Unsupported or FailureKind.Malformed => "Unsupported format",
            FailureKind.None when state.Snapshot is not null => state.Availability switch
            {
                AllowanceAvailability.Reported => "Live", AllowanceAvailability.NotReported => "Not reported",
                AllowanceAvailability.UnsupportedBilling => "Unsupported billing", _ => "Unsupported format"
            },
            _ => "Unavailable"
        };

    public static string Retry(ProviderState state, DateTimeOffset now) => !state.Enabled ? "Monitoring off" :
        state.Status == ProviderStatus.Loading ? "Checking…" : state.RetryAt is { } retry && retry > now
        ? $"Retry in {Math.Max(1, (int)Math.Ceiling((retry - now).TotalSeconds))}s" : "Retry";

    public static string Summary(ProviderState state, DateTimeOffset now)
    {
        if (!state.Enabled) return "Enable in Settings to monitor allowance";
        if (state.Snapshot is { } snapshot)
            return $"Updated {Age(snapshot.ObservedAt, now)}" + (state.Detail is null ? "" : " · provider notice");
        if (state.Status == ProviderStatus.Loading) return "Checking local provider";
        return state.Failure switch
        {
            FailureKind.NotInstalled => "Provider was not detected", FailureKind.LoggedOut => "Sign in through the provider",
            FailureKind.Timeout => "Provider did not respond in time", FailureKind.Network => "Check provider connection",
            FailureKind.Malformed => "Usage format was not recognized", FailureKind.UnsupportedBilling => "Detected billing mode cannot be monitored",
            FailureKind.Unsupported => "Usage format is unsupported", FailureKind.RateLimited => "Waiting for provider rate limit",
            FailureKind.AccountChanged => "Account verification failed; allowance cleared", _ => "Try Retry"
        };
    }

    public static string Age(DateTimeOffset observed, DateTimeOffset now)
    {
        if (observed > now.AddMinutes(1)) return "at an invalid future time";
        var age = now - observed;
        if (age.TotalSeconds < 60) return $"{Math.Max(0, (int)age.TotalSeconds)}s ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    public static string Remaining(UsageWindow window) => window.RemainingPercent is { } value
        ? value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";

    public static string Reset(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } reset) return "Reset unavailable";
        if (reset <= now) return "Reset passed · awaiting update";
        var mins = (long)Math.Ceiling((reset - now).TotalMinutes);
        return "Reset in " + (mins >= 1440 ? $"{mins / 1440}d {mins % 1440 / 60}h" :
            mins >= 60 ? $"{mins / 60}h {mins % 60}m" : $"{mins}m");
    }

    public static string WindowName(string provider, UsageWindow window) => UsagePresentation.Label(window);
}
