using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMeter.Core;

// This is AgentMeter's deliberately small bridge protocol, not a parser for raw
// SDK responses. Identity fields never enter a UsageSnapshot or diagnostic text.
public static class ClaudeUsageParser
{
    private static readonly (string Key, string Name)[] Windows =
    [
        ("five_hour", "5 hours"), ("seven_day", "7 days"),
        ("seven_day_oauth_apps", "7 days · OAuth apps"), ("seven_day_opus", "7 days · Opus"),
        ("seven_day_sonnet", "7 days · Sonnet")
    ];
    private static readonly HashSet<string> DetailCodes = new(StringComparer.Ordinal)
    {
        "usageAvailable", "quotaUnavailable", "privacyOptOut", "authMalformed", "authenticationModeUnsupported",
        "subscriptionUnsupported", "identityUnavailable", "identityChanged", "sdkAccountMismatch", "usageMalformed",
        "usageQueryFailed", "queryTimedOut", "cliMissing", "cliExited", "accessDenied", "bridgeUnavailable"
    };

    public static ClaudeSourceResult Parse(string json, int exitCode, DateTimeOffset observedAt,
        ClaudeAccountBinding? expectedBinding = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            return Parse(document.RootElement, exitCode, observedAt, expectedBinding);
        }
        catch (JsonException) { return Malformed(); }
    }

    public static ClaudeSourceResult Parse(JsonElement root, int exitCode, DateTimeOffset observedAt,
        ClaudeAccountBinding? expectedBinding = null)
    {
        if (!Object(root, "schemaVersion", "authentication", "failure", "detailCode", "binding", "usage") ||
            !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32Safe(out var version) || version != 1 ||
            !Text(root, "authentication", out var authenticationText) || !Text(root, "failure", out var failureText) ||
            !Text(root, "detailCode", out var detailCode) || !DetailCodes.Contains(detailCode) ||
            !root.TryGetProperty("binding", out var bindingElement) || !root.TryGetProperty("usage", out var usage))
            return Malformed();

        var authentication = authenticationText switch
        {
            "authenticated" => ClaudeAuthentication.Authenticated, "signedOut" => ClaudeAuthentication.SignedOut,
            "missing" => ClaudeAuthentication.Missing, "unknown" => ClaudeAuthentication.Unknown, _ => (ClaudeAuthentication)(-1)
        };
        var failure = failureText switch
        {
            "none" => FailureKind.None, "unsupported" => FailureKind.Unsupported, "malformed" => FailureKind.Malformed,
            "loggedOut" => FailureKind.LoggedOut, "notInstalled" => FailureKind.NotInstalled, "timeout" => FailureKind.Timeout,
            "processExited" => FailureKind.ProcessExited, "accessDenied" => FailureKind.AccessDenied,
            "network" => FailureKind.Network, "unexpected" => FailureKind.Unexpected, _ => (FailureKind)(-1)
        };
        if (!Enum.IsDefined(authentication) || !Enum.IsDefined(failure) || exitCode != (failure == FailureKind.None ? 0 : 1))
            return Malformed();

        ClaudeAccountBinding? binding = null;
        if (bindingElement.ValueKind != JsonValueKind.Null)
        {
            if (authentication != ClaudeAuthentication.Authenticated || !Object(bindingElement, "accountId", "organizationId") ||
                !Text(bindingElement, "accountId", out var accountId) ||
                !Regex.IsMatch(accountId, "^claude-email-sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
                !Text(bindingElement, "organizationId", out var organizationId) || !Guid.TryParseExact(organizationId, "D", out var organization))
                return Malformed();
            binding = new(accountId, organization.ToString("D"));
            if (expectedBinding is not null && binding != expectedBinding)
                return new(ClaudeClient.Code, ClaudeAuthentication.Unknown, null,
                    ProviderResult.Fail(FailureKind.AccountChanged));
        }

        if (failure != FailureKind.None)
        {
            if (usage.ValueKind != JsonValueKind.Null || detailCode == "usageAvailable" ||
                authentication == ClaudeAuthentication.SignedOut && failure != FailureKind.LoggedOut ||
                authentication == ClaudeAuthentication.Missing && failure != FailureKind.NotInstalled ||
                failure == FailureKind.LoggedOut && authentication != ClaudeAuthentication.SignedOut ||
                failure == FailureKind.NotInstalled && authentication != ClaudeAuthentication.Missing ||
                binding is not null && (detailCode is "privacyOptOut" or "identityChanged" or "sdkAccountMismatch" or "identityUnavailable"))
                return Malformed();
            return new(ClaudeClient.Code, authentication, binding, ProviderResult.Fail(failure, Detail(detailCode)));
        }
        if (authentication != ClaudeAuthentication.Authenticated || binding is null || detailCode != "usageAvailable" ||
            !Object(usage, [.. Windows.Select(window => window.Key), "model_scoped", "extra_usage"]))
            return Malformed();

        var windows = new List<UsageWindow>();
        var invalidField = false;
        var unsupported = CodexParser.HasMetadata(ClaudeControlTransport.Get(usage, "extra_usage"));
        foreach (var (key, name) in Windows)
        {
            if (!usage.TryGetProperty(key, out var window) || window.ValueKind == JsonValueKind.Null) continue;
            // The old bridge can expose opaque OAuth buckets. The verified 1.1.3 time schema
            // establishes the general and Sonnet/Opus windows only.
            if (key == "seven_day_oauth_apps") { unsupported = true; continue; }
            if (!ReadWindow(window, key, name, false, out var parsed, ref invalidField)) return Malformed();
            if (!Merge(parsed!, windows)) return Malformed();
        }
        if (usage.TryGetProperty("model_scoped", out var models) && models.ValueKind != JsonValueKind.Null)
        {
            if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 32) return Malformed();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || !Text(model, "display_name", out var name) ||
                    string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl) || !names.Add(name))
                    return Malformed();
                if (!ReadWindow(model, "model:" + name, name, true, out var parsed, ref invalidField)) return Malformed();
                if (!Merge(parsed!, windows)) return Malformed();
            }
        }
        if (invalidField && !windows.Any(window => window.UsedPercent is not null))
            return new(ClaudeClient.Code, authentication, binding, ProviderResult.Fail(FailureKind.Malformed));
        var availability = windows.Any(window => window.UsedPercent is not null) ? AllowanceAvailability.Reported :
            windows.Count != 0 ? AllowanceAvailability.NotReported : unsupported ? AllowanceAvailability.UnsupportedFormat : AllowanceAvailability.NotReported;
        return new(ClaudeClient.Code, authentication, binding, new ProviderResult(
            new UsageSnapshot(windows, observedAt, "Claude Code · experimental SDK usage", Availability: availability),
            Detail: invalidField ? "Some usage fields were unavailable." : null));
    }

    private static bool Merge(UsageWindow window, List<UsageWindow> windows)
    {
        var index = windows.FindIndex(w => w.Scope == window.Scope && w.ScopeLabel == window.ScopeLabel && w.DurationMinutes == window.DurationMinutes);
        if (index < 0) { windows.Add(window); return true; }
        var old = windows[index];
        if (old.UsedPercent is not null && window.UsedPercent is not null && old.UsedPercent != window.UsedPercent ||
            old.ResetsAt is not null && window.ResetsAt is not null && old.ResetsAt != window.ResetsAt) return false;
        windows[index] = new(old.Id, old.Name, old.UsedPercent ?? window.UsedPercent, old.ResetsAt ?? window.ResetsAt,
            old.DurationMinutes, old.Scope, old.ScopeLabel, old.Bucket);
        return true;
    }

    private static bool ReadWindow(JsonElement value, string id, string name, bool model,
        out UsageWindow? window, ref bool invalidField)
    {
        window = null;
        if (!Object(value, model ? ["display_name", "utilization", "resets_at"] : ["utilization", "resets_at"])) return false;
        double? used = null;
        if (value.TryGetProperty("utilization", out var utilization) && utilization.ValueKind != JsonValueKind.Null)
        {
            if (utilization.ValueKind == JsonValueKind.Number && utilization.TryGetDouble(out var number)) used = UsageWindow.ValidPercent(number);
            invalidField |= used is null;
        }
        DateTimeOffset? reset = null;
        if (value.TryGetProperty("resets_at", out var resetValue) && resetValue.ValueKind != JsonValueKind.Null)
        {
            if (resetValue.ValueKind == JsonValueKind.String)
            {
                var text = resetValue.GetString()!;
                if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) &&
                    DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) reset = parsed;
            }
            invalidField |= reset is null;
        }
        var modelLabel = model ? name : id switch { "seven_day_opus" => "Opus", "seven_day_sonnet" => "Sonnet", _ => null };
        window = new(id, name, used, reset, id == "five_hour" ? 300 : 10080,
            modelLabel is null ? UsageScope.General : UsageScope.Model, modelLabel, "claude");
        return true;
    }

    private static bool Object(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal) && names.Add(property.Name));
    }
    private static bool Text(JsonElement parent, string name, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return true;
    }
    private static ClaudeSourceResult Malformed() => new(ClaudeClient.Code, ClaudeAuthentication.Unknown, null,
        ProviderResult.Fail(FailureKind.Malformed, "Claude Code returned an unrecognized usage format."));
    private static string Detail(string code) => code switch
    {
        "privacyOptOut" => "Claude Code privacy settings disable this usage query.",
        "identityChanged" or "sdkAccountMismatch" or "identityUnavailable" => "Claude Code account could not be verified.",
        "bridgeUnavailable" => "Claude Code usage helper is unavailable.",
        "queryTimedOut" => "Claude Code usage query timed out.",
        "cliMissing" => "Claude Code CLI was not found.",
        "accessDenied" => "Claude Code usage could not be accessed.",
        "authMalformed" or "usageMalformed" => "Claude Code returned an unrecognized usage format.",
        "authenticationModeUnsupported" or "subscriptionUnsupported" => "This Claude authentication mode does not expose subscription usage.",
        _ => "Claude Code subscription usage is unavailable."
    };
}
