using System.Globalization;
using System.Text.Json;

namespace AgentMeter.Core;

public static class CodexParser
{
    public static ProviderResult Parse(JsonElement result, DateTimeOffset observedAt)
    {
        try
        {
            ClaudeControlTransport.RequireUnique(result);
            if (result.ValueKind != JsonValueKind.Object) return ProviderResult.Fail(FailureKind.Malformed);
            var windows = new List<UsageWindow>();
            var notices = new HashSet<string>(StringComparer.Ordinal);
            var malformed = false;
            var unsupported = HasMetadata(Get(result, "credits")) || HasMetadata(Get(result, "spend"));
            ReadHealth(result, notices);
            var map = Get(result, "rateLimitsByLimitId");
            if (Present(map) && (map.ValueKind != JsonValueKind.Object || map.EnumerateObject().Count() > 32))
                return ProviderResult.Fail(FailureKind.Malformed);
            var legacy = Get(result, "rateLimits");
            if (Present(legacy) && legacy.ValueKind != JsonValueKind.Object) return ProviderResult.Fail(FailureKind.Malformed);
            var legacyId = Present(legacy) ? SafeText(Get(legacy, "limitId"), 120) ?? "codex" : null;
            if (Present(Get(legacy, "limitId")) && SafeText(Get(legacy, "limitId"), 120) is null)
                return ProviderResult.Fail(FailureKind.Malformed);
            var buckets = map.ValueKind == JsonValueKind.Object ? map.EnumerateObject().ToArray() : [];

            bool ReadBucket(string id, JsonElement bucket, bool mirror)
            {
                if (SafeText(JsonSerializer.SerializeToElement(id), 120) is null || bucket.ValueKind != JsonValueKind.Object ||
                    Present(Get(bucket, "limitId")) && SafeText(Get(bucket, "limitId"), 120) != id) return false;
                var mapped = buckets.FirstOrDefault(b => b.Name == id).Value;
                var mapName = SafeText(Get(mapped, "limitName"), 100);
                var legacyName = id == legacyId ? SafeText(Get(legacy, "limitName"), 100) : null;
                if (mapName is not null && legacyName is not null && mapName != legacyName)
                    throw new ProviderQueryException(FailureKind.Unsupported);
                var label = id == "codex" ? "Codex" : mapName ?? legacyName ?? "Additional";
                var scope = id == "codex" ? UsageScope.General : UsageScope.Additional;
                unsupported |= HasMetadata(Get(bucket, "credits")) || HasMetadata(Get(bucket, "spend"));
                ReadHealth(bucket, notices);
                foreach (var slot in new[] { "primary", "secondary" })
                {
                    var window = Get(bucket, slot);
                    if (!Present(window)) continue;
                    if (window.ValueKind != JsonValueKind.Object) { malformed = true; continue; }
                    if (window.EnumerateObject().Any() && !new[] { "usedPercent", "resetsAt", "windowDurationMins" }.Any(k => window.TryGetProperty(k, out _)))
                    { unsupported = true; continue; }
                    var used = Percent(Get(window, "usedPercent"), ref malformed);
                    var duration = Duration(Get(window, "windowDurationMins"), ref malformed);
                    var reset = Unix(Get(window, "resetsAt"), ref malformed);
                    var identity = Escape(id) + (mirror ? "/legacy/" : "/") + slot;
                    var parsed = new UsageWindow(identity, label + " · " + DurationName(duration), used, reset, duration,
                        scope, scope == UsageScope.General || label == "Additional" ? null : label, id);
                    if (!mirror) windows.Add(parsed);
                    else Merge(parsed, windows, slot);
                }
                return true;
            }

            foreach (var bucket in buckets.OrderBy(b => b.Name == "codex" ? 0 : 1).ThenBy(b => b.Name, StringComparer.Ordinal))
                if (!ReadBucket(bucket.Name, bucket.Value, false)) return ProviderResult.Fail(FailureKind.Malformed);
            if (legacyId is not null && !ReadBucket(legacyId, legacy, buckets.Length != 0)) return ProviderResult.Fail(FailureKind.Malformed);
            if (malformed && !windows.Any(w => w.UsedPercent is not null)) return ProviderResult.Fail(FailureKind.Malformed);
            var availability = windows.Any(w => w.UsedPercent is not null) ? AllowanceAvailability.Reported :
                windows.Count != 0 ? AllowanceAvailability.NotReported : unsupported ? AllowanceAvailability.UnsupportedFormat : AllowanceAvailability.NotReported;
            return new(new UsageSnapshot(windows, observedAt, "Codex app-server (live)", Availability: availability),
                Detail: notices.Count == 0 ? null : string.Join(" ", notices));
        }
        catch (ProviderQueryException e) { return ProviderResult.Fail(e.Failure); }
    }

    private static void Merge(UsageWindow window, List<UsageWindow> windows, string slot)
    {
        var matches = windows.Select((w, i) => (Window: w, Index: i)).Where(p => p.Window.Bucket == window.Bucket &&
            p.Window.Scope == window.Scope && !p.Window.Id.Contains("/legacy/", StringComparison.Ordinal) &&
            (p.Window.DurationMinutes is not null && p.Window.DurationMinutes == window.DurationMinutes ||
             p.Window.Id.EndsWith("/" + slot, StringComparison.Ordinal) && (p.Window.DurationMinutes is null || window.DurationMinutes is null))).ToArray();
        var match = matches.Length > 1 ? matches.FirstOrDefault(p => p.Window.Id.EndsWith("/" + slot, StringComparison.Ordinal)) : matches.FirstOrDefault();
        if (matches.Length > 1 && match.Window is null) throw new ProviderQueryException(FailureKind.Unsupported);
        if (match.Window is not { } old) { windows.Add(window); return; }
        if (old.UsedPercent is not null && window.UsedPercent is not null && old.UsedPercent != window.UsedPercent ||
            old.ResetsAt is not null && window.ResetsAt is not null && old.ResetsAt != window.ResetsAt)
            throw new ProviderQueryException(FailureKind.Unsupported);
        var minutes = old.DurationMinutes ?? window.DurationMinutes;
        windows[match.Index] = new(old.Id, (old.Scope == UsageScope.General ? "Codex" : old.ScopeLabel ?? "Additional") + " · " + DurationName(minutes),
            old.UsedPercent ?? window.UsedPercent, old.ResetsAt ?? window.ResetsAt, minutes, old.Scope, old.ScopeLabel, old.Bucket);
    }

    internal static JsonElement Get(JsonElement value, string name) => ClaudeControlTransport.Get(value, name);
    internal static bool Present(JsonElement value) => value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    internal static bool HasMetadata(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Array => value.GetArrayLength() != 0,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => true
    };
    internal static string? SafeText(JsonElement value, int max) => value.ValueKind == JsonValueKind.String && value.GetString() is { } s &&
        !string.IsNullOrWhiteSpace(s) && s == s.Trim() && s.Length <= max && !s.Any(char.IsControl) ? s : null;
    internal static double? Percent(JsonElement value, ref bool malformed)
    {
        if (!Present(value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && UsageWindow.ValidPercent(n) is { } valid) return valid;
        malformed = true; return null;
    }
    private static long? Duration(JsonElement value, ref bool malformed)
    {
        if (!Present(value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n > 0 && n < 5_256_000) return n;
        malformed = true; return null;
    }
    private static DateTimeOffset? Unix(JsonElement value, ref bool malformed)
    {
        if (!Present(value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n > 0 && n < 253_402_300_800)
            return DateTimeOffset.FromUnixTimeSeconds(n);
        malformed = true; return null;
    }
    private static string Escape(string id) => id.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal);
    private static void ReadHealth(JsonElement bucket, HashSet<string> notices)
    {
        if (Get(bucket, "spendControlReached").ValueKind == JsonValueKind.True) notices.Add("Provider reports a spend limit reached.");
        if (Get(bucket, "ordinaryUsageAllowed").ValueKind == JsonValueKind.False) notices.Add("Provider reports ordinary usage is unavailable.");
        if (Get(bucket, "rateLimitReachedType").ValueKind == JsonValueKind.String && Get(bucket, "rateLimitReachedType").GetString() is { Length: > 0 } type &&
            !string.Equals(type, "none", StringComparison.OrdinalIgnoreCase)) notices.Add("Provider reports a reached limit.");
    }
    private static string DurationName(long? minutes) => minutes switch
    {
        null => "Window (duration unavailable)", 1 => "1 minute", 60 => "1 hour", 1440 => "1 day",
        var value when value % 1440 == 0 => $"{(value / 1440).Value.ToString(CultureInfo.InvariantCulture)} days",
        var value when value % 60 == 0 => $"{(value / 60).Value.ToString(CultureInfo.InvariantCulture)} hours",
        var value => $"{value.Value.ToString(CultureInfo.InvariantCulture)} minutes"
    };
}
