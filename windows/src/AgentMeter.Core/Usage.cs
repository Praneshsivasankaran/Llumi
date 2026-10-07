using System.Text.Json.Serialization;

namespace AgentMeter.Core;

public enum ProviderStatus { Loading, Ready, Unavailable, Error }
public enum FailureKind { None, NotInstalled, LoggedOut, Unsupported, Timeout, Network, Malformed, ProcessExited, AccessDenied, Unexpected, UnsupportedBilling, RateLimited, AccountChanged }
public enum UsageScope { General, Model, Additional, Unknown }
public enum AllowanceAvailability { Auto, Reported, NotReported, UnsupportedFormat, UnsupportedBilling }
public enum AuthenticationStatus { Unknown, Verified, SignedOut, Missing, UnsupportedBilling }

// Only digests belong here. Never render or serialize account continuity evidence.
public sealed record AccountBinding([property: JsonIgnore] string Digest)
{
    public override string ToString() => "Verified account binding (omitted)";
}

public sealed record UsageWindow
{
    public UsageWindow(string id, string name, double? usedPercent, DateTimeOffset? resetsAt, long? durationMinutes = null,
        UsageScope? scope = null, string? scopeLabel = null, string? bucket = null)
    {
        Id = id; Name = name; UsedPercent = ValidPercent(usedPercent); ResetsAt = resetsAt;
        DurationMinutes = durationMinutes is > 0 ? durationMinutes : id switch
        { "five_hour" => 300, "seven_day" or "seven_day_sonnet" or "seven_day_opus" => 10080, _ => null };
        Scope = scope ?? (id.Equals("codex/primary", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("codex/secondary", StringComparison.OrdinalIgnoreCase) || id is "five_hour" or "seven_day"
            ? UsageScope.General : UsageScope.Unknown);
        ScopeLabel = scopeLabel;
        Bucket = bucket ?? (id is "five_hour" or "seven_day" ? "claude" : "");
    }
    public long? DurationMinutes { get; }
    public string Id { get; }
    public string Name { get; }
    public UsageScope Scope { get; }
    public string? ScopeLabel { get; }
    public string Bucket { get; }
    public bool IsSupported => Scope != UsageScope.Unknown;
    public bool IsUsableGeneral => Scope == UsageScope.General && UsedPercent is not null && DurationMinutes is > 0;
    public double? UsedPercent { get; }
    public DateTimeOffset? ResetsAt { get; }
    public double? RemainingPercent => UsedPercent is >= 0 and <= 100 && double.IsFinite(UsedPercent.Value) ? 100 - UsedPercent : null;
    public bool ResetPassed(DateTimeOffset now) => ResetsAt is { } reset && reset <= now;
    public static double? ValidPercent(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value) ? value : null;
}

public sealed record UsageSnapshot(IReadOnlyList<UsageWindow> Windows, DateTimeOffset ObservedAt, string Source,
    bool IsCached = false, AllowanceAvailability Availability = AllowanceAvailability.Auto)
{
    [JsonIgnore] public AccountBinding? Binding { get; init; }
}
public sealed record ProviderResult(UsageSnapshot? Snapshot, FailureKind Failure = FailureKind.None, string? Detail = null)
{
    [JsonIgnore] public AccountBinding? VerifiedBinding { get; init; }
    public AuthenticationStatus Authentication { get; init; } = AuthenticationStatus.Unknown;
    [JsonIgnore] public TimeSpan? RetryAfter { get; init; }
    public static ProviderResult Fail(FailureKind kind, string? safeDetail = null) => new(null, kind, safeDetail);
    public override string ToString() => $"Provider result: {Failure}; {Authentication}";
}

public interface IUsageProvider
{
    string Name { get; }
    Task<ProviderResult> QueryAsync(CancellationToken cancellationToken);
}

public sealed record ProviderState(string Name, ProviderStatus Status, UsageSnapshot? Snapshot = null,
    FailureKind Failure = FailureKind.None, string? Detail = null, DateTimeOffset? LastRefresh = null,
    bool Enabled = true, AuthenticationStatus Authentication = AuthenticationStatus.Unknown, DateTimeOffset? RetryAt = null)
{
    public AllowanceAvailability Availability
    {
        get
        {
            var derived = UsagePresentation.Availability(this);
            if (derived == AllowanceAvailability.Reported) return derived;
            return Snapshot?.Availability is { } availability && availability is not (AllowanceAvailability.Auto or AllowanceAvailability.Reported)
                ? availability : derived;
        }
    }
    public bool IsStale(DateTimeOffset now) => Snapshot is { } s && UsagePresentation.Windows(this).Any(w => w.UsedPercent is not null) &&
        (s.IsCached || Failure != FailureKind.None || now - s.ObservedAt > TimeSpan.FromMinutes(2) || s.ObservedAt > now.AddMinutes(1)
         || UsagePresentation.Windows(this).Any(w => w.ResetPassed(now)));
}

public static class FailureText
{
    public static string For(FailureKind failure) => failure switch
    {
        FailureKind.NotInstalled => "Not installed or executable not found",
        FailureKind.LoggedOut => "Signed out — sign in through the provider",
        FailureKind.Unsupported => "Usage is unavailable through this provider interface",
        FailureKind.UnsupportedBilling => "Detected billing mode does not support subscription monitoring",
        FailureKind.AccountChanged => "Account could not be verified; previous allowance was cleared",
        FailureKind.RateLimited => "Provider rate limit; waiting before retry",
        FailureKind.Timeout => "Provider query timed out",
        FailureKind.Network => "Provider service or network is unavailable",
        FailureKind.Malformed => "Provider returned an unrecognized usage format",
        FailureKind.ProcessExited => "Provider exited before returning usage",
        FailureKind.AccessDenied => "Provider data could not be accessed",
        FailureKind.Unexpected => "Provider query failed",
        _ => ""
    };
}
