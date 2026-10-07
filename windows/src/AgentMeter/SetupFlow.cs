using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter;

internal enum SetupStep { Welcome, Providers, Codex, Claude, Verify, Preferences, Done }
internal sealed class SetupFlow(SetupCompletionStore store)
{
    internal bool Codex { get; set; } = true;
    internal bool Claude { get; set; } = true;
    internal SetupStep Step { get; private set; } = SetupStep.Welcome;
    internal SetupStep[] Steps => new[] { SetupStep.Welcome, SetupStep.Providers }
        .Concat(Codex ? new[] { SetupStep.Codex } : []).Concat(Claude ? new[] { SetupStep.Claude } : [])
        .Concat([SetupStep.Verify, SetupStep.Preferences, SetupStep.Done]).ToArray();
    internal void Next() { var next = Steps.FirstOrDefault(s => (int)s > (int)Step, Step); Step = next; }
    internal void Back() { var previous = Steps.LastOrDefault(s => (int)s < (int)Step, Step); Step = previous; }
    internal void Reopen() => Step = SetupStep.Welcome;
    internal bool Complete() => store.Save(true);
}
internal sealed class SetupCompletionStore(string path)
{
    internal static SetupCompletionStore Default() => new(Path.Combine(PackagedEnvironment.DataDirectory, "setup-completed.json"));
    internal bool RecognizeExisting(bool existingPreferences)
    {
        if (File.Exists(path)) return IsComplete();
        Save(existingPreferences); // Persist fresh false before the user changes preferences.
        return existingPreferences;
    }
    internal bool IsComplete()
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 32) return false;
            using var value = JsonDocument.Parse(input);
            return value.RootElement.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    internal bool Save(bool completed)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, completed ? "true" : "false");
            File.Move(temporary, path, true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

internal sealed record SetupDiagnostic(string Detected, string Authentication, string Usage, string Failure, string Result)
{
    internal static SetupDiagnostic From(ProviderState state)
    {
        var failure = state.Failure switch {
            FailureKind.None => "none", FailureKind.NotInstalled => "notInstalled", FailureKind.LoggedOut => "signedOut",
            FailureKind.Unsupported => "incompatible", FailureKind.Timeout => "timeout", FailureKind.Malformed => "malformed",
            FailureKind.ProcessExited => "processExited", FailureKind.UnsupportedBilling => "unsupportedBilling",
            FailureKind.RateLimited => "rateLimited", FailureKind.AccountChanged => "accountChanged", _ => "unavailable" };
        if (!state.Enabled) return new("unknown", "unknown", "off", "none", "off");
        var authentication = state.Authentication switch {
            AuthenticationStatus.Verified => "verified", AuthenticationStatus.SignedOut => "signed-out",
            AuthenticationStatus.UnsupportedBilling => "mode-detected", _ => "unknown" };
        var detected = state.Authentication == AuthenticationStatus.Missing || state.Failure == FailureKind.NotInstalled ? "no" :
            state.Authentication is AuthenticationStatus.Verified or AuthenticationStatus.SignedOut or AuthenticationStatus.UnsupportedBilling || state.Snapshot is not null ? "yes" : "unknown";
        if (state.Status == ProviderStatus.Loading) return new(detected, authentication, "checking", failure, "pending");
        if (state.IsStale(DateTimeOffset.UtcNow)) return new(detected, authentication, "stale", failure, "failure");
        var usage = state.Failure == FailureKind.UnsupportedBilling ? "unsupported-billing" : state.Snapshot is null ? "unavailable" : state.Availability switch {
            AllowanceAvailability.Reported => "available", AllowanceAvailability.NotReported => "not-reported",
            AllowanceAvailability.UnsupportedFormat => "unsupported-format", AllowanceAvailability.UnsupportedBilling => "unsupported-billing", _ => "unavailable" };
        return new(detected, authentication, usage, failure, state.Snapshot is not null && state.Failure == FailureKind.None ? "success" : "failure");
    }
    internal string Status => Usage == "off" ? "Monitoring off" : Usage == "checking" ? "Checking…" :
        Authentication == "verified" ? "Signed in" : Detected == "no" ? "Not installed" :
        Authentication == "signed-out" ? "Sign in required" : Authentication == "mode-detected" ? "Other billing mode detected" : "Sign-in not verified";
    internal string Monitoring => Usage switch {
        "available" => "Available", "not-reported" => "Allowances not reported", "unsupported-format" => "Allowance format not supported",
        "unsupported-billing" => "This billing mode can’t be monitored", "stale" => "Last known allowances · stale",
        "checking" => "Checking allowances…", "off" => "Off", _ => "Allowances unavailable" };
    internal string Summary => $"{Status}\nMonitoring: {Monitoring}";
}

internal static class SetupRetryPresentation
{
    internal static bool CanRetry(ProviderState state, DateTimeOffset now) => state.Enabled && state.Status != ProviderStatus.Loading &&
        (state.RetryAt is null || state.RetryAt <= now);
    internal static string? Message(ProviderState state, DateTimeOffset now)
    {
        if (!state.Enabled) return null;
        if (state.Status == ProviderStatus.Loading) return "Checking…";
        if (state.RetryAt is not { } retry || retry <= now) return null;
        var duration = retry - now;
        var after = duration.TotalSeconds > 900 ? $"after {retry.ToLocalTime():MMM d HH:mm}" :
            "in " + (duration.TotalSeconds < 60 ? $"{Math.Ceiling(duration.TotalSeconds):0}s" : $"{Math.Ceiling(duration.TotalMinutes):0}m");
        return state.Failure == FailureKind.RateLimited ? "Rate limited. Retry " + after + "." :
            duration.TotalSeconds > 900 ? "Retry " + after + "." : PopupText.Retry(state, now);
    }
}

internal static class UsageAccessibility
{
    internal static string Observation(ProviderState state, DateTimeOffset now)
    {
        var text = PopupText.Summary(state, now);
        if (!state.IsStale(now) || state.Snapshot is not { } snapshot) return text;
        var reasons = new List<string>();
        if (snapshot.IsCached) reasons.Add("cached reading");
        if (state.Failure != FailureKind.None) reasons.Add("last retrieval failed");
        if (snapshot.ObservedAt > now.AddMinutes(1)) reasons.Add("observation time is in the future");
        else if (now - snapshot.ObservedAt > TimeSpan.FromMinutes(2)) reasons.Add("observation older than two minutes");
        if (UsagePresentation.Windows(state).Any(w => w.ResetPassed(now))) reasons.Add("reset passed, awaiting provider update");
        return text + "; Stale: " + string.Join(", ", reasons);
    }
}
internal static class SetupDiagnostics
{
    internal static string Numeric(string? value) => value is { Length: > 0 and <= 32 } &&
        value.Split('.').All(p => p.Length > 0 && p.All(c => c is >= '0' and <= '9')) ? value : "unknown";
    internal static string Report(IReadOnlyList<ProviderState> states, string? version, string? build)
    {
        var lines = new List<string> { "Llumi diagnostics schema: 1", $"App version: {Numeric(version)}",
            $"App build: {Numeric(build)}", $"OS: Windows {Environment.OSVersion.Version}",
            $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}" };
        foreach (var provider in new[] { "Codex", "Claude" })
        {
            var state = states.FirstOrDefault(s => s.Name == provider || (provider == "Claude" && s.Name == "Claude Code"))
                ?? new ProviderState(provider, ProviderStatus.Loading);
            var value = SetupDiagnostic.From(state);
            lines.AddRange([$"[{provider.ToLowerInvariant()}]", $"Detected: {value.Detected}",
                $"Authentication: {value.Authentication}", $"Usage: {value.Usage}",
                $"Failure: {value.Failure}", $"Last result: {value.Result}"]);
        }
        return string.Join("\n", lines);
    }
}
