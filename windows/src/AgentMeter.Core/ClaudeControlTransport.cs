using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentMeter.Core;

/// <summary>Usage-only Claude Code control protocol. No SDK, prompt, or model request.</summary>
public sealed class ClaudeControlTransport : IClaudeUsageSource
{
    public ClaudeClient Client => ClaudeClient.Code;
    public static readonly string[] Arguments = ["--print", "--input-format", "stream-json", "--output-format", "stream-json",
        "--verbose", "--no-session-persistence", "--safe-mode", "--setting-sources=", "--strict-mcp-config",
        "--mcp-config", "{\"mcpServers\":{}}"];
    private readonly Func<string?> locate;
    private readonly Func<string, CancellationToken, Task<(JsonElement Output, int ExitCode)>> auth;
    private readonly Func<string, Identity, CancellationToken, Task<JsonElement>> usage;
    private readonly TimeSpan timeout;

    public ClaudeControlTransport(Func<string?>? locate = null,
        Func<string, CancellationToken, Task<(JsonElement Output, int ExitCode)>>? auth = null,
        Func<string, Identity, CancellationToken, Task<JsonElement>>? usage = null, TimeSpan? timeout = null)
    {
        this.locate = locate ?? ClaudeCliLocator.Find;
        this.auth = auth ?? ReadAuth;
        this.usage = usage ?? ReadUsage;
        this.timeout = timeout ?? TimeSpan.FromSeconds(12);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(12)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<ClaudeSourceResult> QueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        TimeSpan? rateEmbargo = null;
        ClaudeSourceResult Failed(FailureKind kind, ClaudeAccountBinding? binding = null, TimeSpan? retryAfter = null)
            => Fail(kind, binding, retryAfter ?? rateEmbargo);
        try
        {
            var executable = locate();
            if (executable is null) return Failed(FailureKind.NotInstalled);
            if (PrivacyOptOut()) return Failed(FailureKind.Unsupported);
            var beforeResult = await auth(executable, token).ConfigureAwait(false);
            var before = ParseIdentity(beforeResult.Output, beforeResult.ExitCode);
            JsonElement response = default;
            ProviderQueryException? queryFailure = null;
            try { response = await usage(executable, before, token).ConfigureAwait(false); }
            catch (ProviderQueryException e) { queryFailure = e; if (e.Failure == FailureKind.RateLimited) rateEmbargo = e.RetryAfter; }
            catch (JsonException) { queryFailure = new(FailureKind.Malformed); }
            var afterResult = await auth(executable, token).ConfigureAwait(false);
            var after = ParseIdentity(afterResult.Output, afterResult.ExitCode);
            token.ThrowIfCancellationRequested();
            if (before != after) return Failed(FailureKind.AccountChanged);
            var binding = after.Binding;
            if (queryFailure is not null) return Failed(queryFailure.Failure,
                queryFailure.Failure == FailureKind.AccountChanged ? null : binding, queryFailure.RetryAfter);
            try
            {
                var (windows, availability) = ParseAllowance(response, after.Plan);
                return new(Client, ClaudeAuthentication.Authenticated, binding,
                    new(new UsageSnapshot(windows, DateTimeOffset.UtcNow, "Claude Code control protocol (live)", Availability: availability)));
            }
            catch (ProviderQueryException e) { return Failed(e.Failure, e.Failure == FailureKind.AccountChanged ? null : binding, e.RetryAfter); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed(FailureKind.Timeout); }
        catch (ProviderQueryException e) { return Failed(e.Failure, retryAfter: e.RetryAfter); }
        catch (JsonException) { return Failed(FailureKind.Malformed); }
        catch (InvalidOperationException) { return Failed(FailureKind.Malformed); }
        catch (KeyNotFoundException) { return Failed(FailureKind.Malformed); }
        catch (UnauthorizedAccessException) { return Failed(FailureKind.AccessDenied); }
        catch (System.ComponentModel.Win32Exception e) { return Failed(e.NativeErrorCode is 2 or 3 ? FailureKind.NotInstalled : FailureKind.AccessDenied); }
        catch (IOException) { return Failed(FailureKind.ProcessExited); }
    }

    public sealed record Identity(string Email, string Organization, string OrganizationName, string Plan)
    {
        public ClaudeAccountBinding Binding => new("claude-context-sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Email + "\n" + Organization + "\n" + Plan))).ToLowerInvariant(), Organization);
        public override string ToString() => "Claude identity (omitted)";
    }

    public static Identity ParseIdentity(JsonElement value, int exitCode)
    {
        RequireUnique(value);
        var logged = Get(value, "loggedIn");
        if (logged.ValueKind == JsonValueKind.False && exitCode is 0 or 1) throw new ProviderQueryException(FailureKind.LoggedOut);
        if (logged.ValueKind != JsonValueKind.True || exitCode != 0) throw new ProviderQueryException(FailureKind.Malformed);
        var email = Text(value, "email");
        var organization = Text(value, "orgId");
        var name = Text(value, "orgName");
        var plan = Text(value, "subscriptionType");
        if (Text(value, "authMethod") is "apiKey" or "api_key" || Text(value, "apiProvider") is "bedrock" or "vertex" || plan == "payg")
            throw new ProviderQueryException(FailureKind.UnsupportedBilling);
        if (Text(value, "authMethod") != "claude.ai" || Text(value, "apiProvider") != "firstParty" ||
            plan is not ("pro" or "max" or "team" or "enterprise") || Get(value, "analyticsDisabled").ValueKind != JsonValueKind.False ||
            email is null || !email.Contains('@') || email.Any(char.IsWhiteSpace) || !Guid.TryParseExact(organization, "D", out var id) || name is null)
            throw new ProviderQueryException(FailureKind.Unsupported);
        return new(email.ToLowerInvariant(), id.ToString("D"), name, plan);
    }

    public static void VerifySession(JsonElement account, Identity identity)
    {
        RequireUnique(account);
        var key = Text(account, "apiKeySource");
        var token = Text(account, "tokenSource");
        foreach (var name in new[] { "apiKeySource", "tokenSource" })
            if (Get(account, name).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                throw new ProviderQueryException(FailureKind.Malformed);
        if (!string.Equals(Text(account, "email"), identity.Email, StringComparison.OrdinalIgnoreCase) ||
            Text(account, "apiProvider") != "firstParty" ||
            Text(account, "organization") is not { } org || (org != identity.Organization && org != identity.OrganizationName) ||
            key is not (null or "none") || token is not (null or "claude.ai" or "oauth" or "claudeAiOauth" or "CLAUDE_CODE_OAUTH_TOKEN"))
            throw new ProviderQueryException(FailureKind.AccountChanged);
    }

    public static IReadOnlyList<UsageWindow> ParseUsage(JsonElement value, string plan)
        => ParseAllowance(value, plan).Windows;

    internal static (IReadOnlyList<UsageWindow> Windows, AllowanceAvailability Availability) ParseAllowance(JsonElement value, string plan)
    {
        RequireUnique(value);
        var available = Get(value, "rate_limits_available");
        if (available.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !value.TryGetProperty("behaviors", out var behaviors) || behaviors.ValueKind != JsonValueKind.Null)
            throw new ProviderQueryException(FailureKind.Unsupported);
        if (Text(value, "subscription_type") != plan) throw new ProviderQueryException(FailureKind.AccountChanged);
        var session = Get(value, "session");
        if (!Zero(Get(session, "total_cost_usd")) || !Zero(Get(session, "total_api_duration_ms")) ||
            Get(session, "model_usage") is not { ValueKind: JsonValueKind.Object } models || models.EnumerateObject().Any())
            throw new ProviderQueryException(FailureKind.Unsupported);
        var limits = Get(value, "rate_limits");
        if (available.ValueKind == JsonValueKind.False || limits.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return ([], AllowanceAvailability.NotReported);
        if (limits.ValueKind != JsonValueKind.Object || limits.EnumerateObject().Count() > 64) throw new ProviderQueryException(FailureKind.Malformed);
        var windows = new List<UsageWindow>();
        var malformed = false;
        var unsupported = CodexParser.HasMetadata(Get(limits, "extra_usage"));
        foreach (var property in limits.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.Name is "model_scoped" or "extra_usage" || property.Value.ValueKind == JsonValueKind.Null) continue;
            var known = property.Name is "five_hour" or "seven_day" or "seven_day_sonnet" or "seven_day_opus";
            if (property.Name.Length > 100 || property.Name.Any(char.IsControl)) { unsupported = true; continue; }
            if (!known) { unsupported = true; continue; }
            if (property.Value.ValueKind != JsonValueKind.Object) { malformed = true; continue; }
            var model = property.Name switch { "seven_day_sonnet" => "Sonnet", "seven_day_opus" => "Opus", _ => null };
            var label = property.Name switch { "five_hour" => "5 hours", "seven_day" => "7 days", _ => model! };
            var window = ReadWindow(property.Value, property.Name, label, property.Name == "five_hour" ? 300 : 10080,
                model is null ? UsageScope.General : UsageScope.Model, model, ref malformed);
            MergeWindow(window, windows);
        }
        var scoped = Get(limits, "model_scoped");
        if (scoped.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (scoped.ValueKind != JsonValueKind.Array || scoped.GetArrayLength() > 32) throw new ProviderQueryException(FailureKind.Malformed);
            var seen = new HashSet<string>();
            foreach (var model in scoped.EnumerateArray())
            {
                var label = Text(model, "display_name");
                if (label is null || label.Length > 100 || !seen.Add(label)) throw new ProviderQueryException(FailureKind.Malformed);
                MergeWindow(ReadWindow(model, "model:" + label, label, 10080, UsageScope.Model, label, ref malformed), windows);
            }
        }
        if (malformed && !windows.Any(w => w.UsedPercent is not null)) throw new ProviderQueryException(FailureKind.Malformed);
        return (windows, windows.Any(w => w.UsedPercent is not null) ? AllowanceAvailability.Reported : windows.Count != 0 ?
            AllowanceAvailability.NotReported : unsupported ? AllowanceAvailability.UnsupportedFormat : AllowanceAvailability.NotReported);
    }

    private static void MergeWindow(UsageWindow window, List<UsageWindow> windows)
    {
        var index = windows.FindIndex(w => w.Scope == window.Scope && w.ScopeLabel == window.ScopeLabel && w.DurationMinutes == window.DurationMinutes);
        if (index < 0) { windows.Add(window); return; }
        var old = windows[index];
        if (old.UsedPercent is not null && window.UsedPercent is not null && old.UsedPercent != window.UsedPercent ||
            old.ResetsAt is not null && window.ResetsAt is not null && old.ResetsAt != window.ResetsAt)
            throw new ProviderQueryException(FailureKind.Unsupported);
        windows[index] = new(old.Id, old.Name, old.UsedPercent ?? window.UsedPercent, old.ResetsAt ?? window.ResetsAt,
            old.DurationMinutes, old.Scope, old.ScopeLabel, old.Bucket);
    }

    private static UsageWindow ReadWindow(JsonElement value, string id, string name, long minutes, UsageScope scope, string? scopeLabel, ref bool malformed)
    {
        var used = CodexParser.Percent(Get(value, "utilization"), ref malformed);
        var reset = Iso(Get(value, "resets_at"), ref malformed);
        return new(id, name, used, reset, minutes, scope, scopeLabel, "claude");
    }

    internal static DateTimeOffset? Iso(JsonElement stamp, ref bool malformed)
    {
        if (stamp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            var text = CodexParser.SafeText(stamp, 64);
            if (text is not null && Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
            malformed = true;
        }
        return null;
    }

    private static async Task<(JsonElement, int)> ReadAuth(string executable, CancellationToken token)
    {
        await using var process = ProviderProcess.Start(executable, ["auth", "status"]);
        return await process.ReadJsonOutputAsync(token).ConfigureAwait(false);
    }
    private static async Task<JsonElement> ReadUsage(string executable, Identity identity, CancellationToken token)
    {
        await using var process = ProviderProcess.Start(executable, Arguments);
        await process.SendAsync(new { type = "control_request", request_id = "init", request = new { subtype = "initialize", hooks = new { } } }, token).ConfigureAwait(false);
        var initialized = await process.ReadControlResultAsync("init", token).ConfigureAwait(false);
        VerifySession(Get(initialized, "account"), identity);
        await process.SendAsync(new { type = "control_request", request_id = "usage", request = new { subtype = "get_usage", skip_behaviors = true } }, token).ConfigureAwait(false);
        return await process.ReadControlResultAsync("usage", token).ConfigureAwait(false);
    }
    public static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new ProviderQueryException(FailureKind.Malformed); RequireUnique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireUnique(item);
    }
    public static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : default;
    public static string? Text(JsonElement value, string key)
    {
        var element = Get(value, key);
        if (element.ValueKind != JsonValueKind.String) return null;
        var text = element.GetString();
        return !string.IsNullOrWhiteSpace(text) && text == text.Trim() && text.Length <= 320 && !text.Any(char.IsControl) ? text : null;
    }
    private static bool Zero(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && n == 0;
    private static bool PrivacyOptOut() => new[] { "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "DISABLE_TELEMETRY" }
        .Any(key => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) ||
        Environment.GetEnvironmentVariable("DO_NOT_TRACK") is "1" or "true";
    private static ClaudeSourceResult Fail(FailureKind kind, ClaudeAccountBinding? binding = null, TimeSpan? retryAfter = null) => new(ClaudeClient.Code,
        kind == FailureKind.LoggedOut ? ClaudeAuthentication.SignedOut : kind == FailureKind.NotInstalled ? ClaudeAuthentication.Missing :
        binding is null ? ClaudeAuthentication.Unknown : ClaudeAuthentication.Authenticated, binding, ProviderResult.Fail(kind) with { RetryAfter = retryAfter });
}
