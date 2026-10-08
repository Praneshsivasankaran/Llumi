using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ProductPassTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AgentMeter-tests-" + Guid.NewGuid());
    private SetupCompletionStore Store => new(Path.Combine(root, "setup.json"));
    private static ProviderState State(params UsageWindow[] windows) => new("Claude", ProviderStatus.Ready,
        new UsageSnapshot(windows, DateTimeOffset.UtcNow, "synthetic"), Authentication: AuthenticationStatus.Verified);
    [Theory]
    [InlineData(10, 80)] [InlineData(80, 10)] [InlineData(37, 19)]
    public void FiveHourAlwaysWinsAndDetailsRetainBoth(double shortUsed, double weeklyUsed)
    {
        var s = State(new("seven_day", "Weekly", weeklyUsed, null), new("five_hour", "5-hour", shortUsed, null));
        Assert.Equal(100 - shortUsed, MonitorSelection.Select(s)?.RemainingPercent);
        Assert.Equal(new[] { "five_hour", "seven_day" }, MonitorSelection.Details(s).Select(w => w.Id));
    }
    [Fact]
    public void MissingMalformedAndDuplicateFiveHourAllowValidGeneralWeeklyFallback()
    {
        var week = new UsageWindow("seven_day", "Weekly", 20, null);
        Assert.Equal(week, MonitorSelection.Select(State(week)));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, 101, -1 })
            Assert.Equal(week, MonitorSelection.Select(State(week, new("five_hour", "", invalid, null))));
        Assert.Equal("five_hour", MonitorSelection.Select(State(week, new("five_hour", "", 20, null), new("five_hour", "", 20, null)))?.Id);
    }
    [Fact]
    public void ExpiredResetDoesNotRefill()
    {
        var w = new UsageWindow("five_hour", "5-hour", 80, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(20, w.RemainingPercent); Assert.True(w.ResetPassed(DateTimeOffset.UtcNow));
        Assert.Equal("Reset passed · awaiting update", PopupText.Reset(w, DateTimeOffset.UtcNow));
    }
    [Fact]
    public void FreshCompletionAndManualReopen()
    {
        Assert.False(Store.RecognizeExisting(false)); Assert.False(Store.IsComplete());
        var flow = new SetupFlow(Store); flow.Next(); Assert.Equal(SetupStep.Providers, flow.Step);
        Assert.True(flow.Complete()); Assert.True(Store.IsComplete());
        flow.Reopen(); Assert.Equal(SetupStep.Welcome, flow.Step); Assert.True(Store.IsComplete());
    }
    [Fact]
    public void ExistingValidPreferencesExemptOnlyBeforeFirstRun()
    {
        Assert.True(Store.RecognizeExisting(true)); Assert.True(Store.IsComplete());
        Store.Save(false); Assert.False(Store.RecognizeExisting(true));
    }
    [Theory]
    [InlineData("\"true\"")] [InlineData("1")] [InlineData("{}")] [InlineData("not-json")]
    public void MalformedCompletionDoesNotSuppressSetup(string value)
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "setup.json"), value);
        Assert.False(Store.RecognizeExisting(true)); Assert.False(Store.IsComplete());
    }
    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)] [InlineData(false, false)]
    public void SelectedProviderRoutesAlwaysReachDone(bool codex, bool claude)
    {
        var flow = new SetupFlow(Store) { Codex = codex, Claude = claude };
        Assert.Equal(codex, flow.Steps.Contains(SetupStep.Codex));
        Assert.Equal(claude, flow.Steps.Contains(SetupStep.Claude));
        foreach (var unused in flow.Steps.Skip(1)) flow.Next();
        Assert.Equal(SetupStep.Done, flow.Step);
    }
    [Fact]
    public void ExistingPreferencesMustBeRecognizableAndTyped()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "preferences.json"); var store = new PreferenceStore(path);
        foreach (var invalid in new[] { "{}", "null", "{\"Appearance\":\"Dark\"}", "{\"TrayIcon\":1}", "{\"Appearance\":99}" })
        { File.WriteAllText(path, invalid); Assert.False(store.HasValidExistingPreferences()); }
        foreach (var appearance in Enum.GetValues<Appearance>())
        { Assert.True(store.Save(new(false, false, appearance))); Assert.True(store.HasValidExistingPreferences()); Assert.Equal(appearance, store.Load().Appearance); }
    }
    [Fact]
    public void DiagnosticCopyDoesNotLeakAnyUntrustedProviderFields()
    {
        const string secret = "secret-token private@example.test C:\\Users\\example\\project";
        var s = State(new UsageWindow("five_hour", secret, 20, null)) with { Detail = secret, Snapshot = new([], DateTimeOffset.UtcNow, secret) };
        var text = SetupDiagnostics.Report([s], secret, secret);
        foreach (var denied in new[] { "secret-token", "private", "@", "C:\\", "20%" }) Assert.DoesNotContain(denied, text);
        Assert.Contains("App version: unknown", text); Assert.Contains("Authentication: verified", text);
    }
    [Theory]
    [InlineData(FailureKind.NotInstalled, AuthenticationStatus.Missing, "no", "unknown")]
    [InlineData(FailureKind.LoggedOut, AuthenticationStatus.SignedOut, "yes", "signed-out")]
    [InlineData(FailureKind.Timeout, AuthenticationStatus.Unknown, "unknown", "unknown")]
    [InlineData(FailureKind.Malformed, AuthenticationStatus.Unknown, "unknown", "unknown")]
    public void FailureReadinessNeverGuessesAuthentication(FailureKind failure, AuthenticationStatus authentication, string detected, string auth)
    {
        var d = SetupDiagnostic.From(new("Codex", ProviderStatus.Error, Failure: failure, Authentication: authentication));
        Assert.Equal(detected, d.Detected); Assert.Equal(auth, d.Authentication); Assert.NotEqual("Signed in", d.Status);
    }
    [Theory]
    [InlineData(AllowanceAvailability.NotReported, "Allowances not reported")]
    [InlineData(AllowanceAvailability.UnsupportedFormat, "Allowance format not supported")]
    public void SignedInReadinessDoesNotPromiseAllowance(AllowanceAvailability availability, string monitoring)
    {
        var diagnostic = SetupDiagnostic.From(new("Claude", ProviderStatus.Ready,
            new([], DateTimeOffset.UtcNow, "fixture", Availability: availability), Authentication: AuthenticationStatus.Verified));
        Assert.Equal("Signed in", diagnostic.Status); Assert.Equal(monitoring, diagnostic.Monitoring);
        Assert.Equal("verified", diagnostic.Authentication); Assert.NotEqual("available", diagnostic.Usage);
        Assert.NotEqual("Signed in", SetupDiagnostic.From(new("Claude", ProviderStatus.Ready, new([], DateTimeOffset.UtcNow, "fixture"))).Status);
    }
    [Theory]
    [InlineData(FailureKind.Malformed, "unsupported-format")]
    [InlineData(FailureKind.Unsupported, "unsupported-format")]
    [InlineData(FailureKind.UnsupportedBilling, "unsupported-billing")]
    [InlineData(FailureKind.Timeout, "unavailable")]
    public void FailedChecksPreserveAllowanceStateWithoutGuessingSignIn(FailureKind failure, string usage)
    {
        var state = new ProviderState("Codex", ProviderStatus.Error, Failure: failure);
        var diagnostic = SetupDiagnostic.From(state);
        Assert.Equal(usage, diagnostic.Usage); Assert.Equal("unknown", diagnostic.Authentication);
        Assert.NotEqual("Signed in", diagnostic.Status);
        Assert.Equal("checking", SetupDiagnostic.From(state with { Status = ProviderStatus.Loading }).Usage);
        Assert.Equal("off", SetupDiagnostic.From(state with { Enabled = false }).Usage);
        var cached = State(new UsageWindow("five_hour", "fixture", 30, DateTimeOffset.UtcNow.AddHours(3))) with { Failure = failure };
        Assert.Equal("stale", SetupDiagnostic.From(cached).Usage);
    }
    [Fact]
    public void RetryCountdownUsesCollectorDeadlineAndDisabledProvidersCannotRetry()
    {
        var now = DateTimeOffset.UtcNow; var state = new ProviderState("Codex", ProviderStatus.Error, RetryAt: now.AddSeconds(10));
        Assert.False(SetupRetryPresentation.CanRetry(state, now)); Assert.Contains("10s", SetupRetryPresentation.Message(state, now));
        Assert.True(SetupRetryPresentation.CanRetry(state, now.AddSeconds(10)));
        Assert.False(SetupRetryPresentation.CanRetry(state with { Enabled = false }, now.AddMinutes(1)));
        Assert.Equal("Monitoring off", SetupDiagnostic.From(state with { Enabled = false }).Status);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

[Collection("Windows UI")]
public sealed class SetupFormTests
{
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static Button Button(Form form, string title) => Descendants(form).OfType<Button>().Single(b => b.Text == title);
    private sealed class Startup : IStartupRegistration
    {
        internal bool Enabled;
        public bool TryRead(out bool enabled) { enabled = Enabled; return true; }
        public bool TrySet(bool enabled) { Enabled = enabled; return true; }
    }
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    [Fact]
    public Task CheckSetupRetryReadsSharedProviderStateAndAllowsFailure() => Sta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "AgentMeter-setup-" + Guid.NewGuid(), "done.json");
        var flow = new SetupFlow(new(path)); while (flow.Step != SetupStep.Verify) flow.Next();
        ProviderState[] states = [new("Codex", ProviderStatus.Loading), new("Claude", ProviderStatus.Error, Failure: FailureKind.NotInstalled)];
        var refreshes = 0;
        using var form = new SetupForm(flow, () => states, _ => {
            refreshes++; states = [new("Codex", ProviderStatus.Ready, new([], DateTimeOffset.UtcNow, "fixture"), Authentication: AuthenticationStatus.Verified)];
        }, () => new(), _ => { }, new Startup(), () => { }, () => { });
        form.Show(); form.RefreshStatuses();
        Assert.Contains(Descendants(form).OfType<Label>(), l => l.AccessibleName == "Codex setup status" && l.Text == "Checking…");
        Button(form, "Retry").PerformClick(); form.RefreshStatuses();
        Assert.Equal(1, refreshes);
        Assert.Contains(Descendants(form).OfType<Label>(), l => l.Text.Contains("Signed in") && l.Text.Contains("Allowances not reported"));
        states = [new("Codex", ProviderStatus.Error, Failure: FailureKind.Timeout)]; form.RefreshStatuses();
        Assert.DoesNotContain(Descendants(form).OfType<Label>(), l => l.Text.Contains("Signed in"));
    });
    [Fact]
    public Task VerificationCanContinueWithSignedInProviderWhoseAllowanceIsNotReported() => Sta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "AgentMeter-setup-" + Guid.NewGuid(), "done.json");
        var flow = new SetupFlow(new(path)); while (flow.Step != SetupStep.Verify) flow.Next();
        ProviderState[] states = [new("Codex", ProviderStatus.Ready, new([], DateTimeOffset.UtcNow, "fixture"), Authentication: AuthenticationStatus.Verified)];
        using var form = new SetupForm(flow, () => states, _ => { }, () => new(), _ => { }, new Startup(), () => { }, () => { });
        form.Show(); Assert.True(Button(form, "Continue").Visible);
        Assert.Contains(Descendants(form).OfType<Label>(), l => l.Text.Contains("Signed in") && l.Text.Contains("Allowances not reported"));
        states = [states[0] with { Authentication = AuthenticationStatus.Unknown }]; form.RefreshStatuses();
        Assert.True(Button(form, "Finish Anyway").Visible);
    });

    [Fact]
    public Task ProviderChoicesReadAndWriteSharedPreferencesAndChangeRouting() => Sta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "AgentMeter-setup-" + Guid.NewGuid(), "done.json");
        var p = new Preferences(CodexEnabled: false, ClaudeEnabled: true); var flow = new SetupFlow(new(path));
        ProviderState[] states = [new("Codex", ProviderStatus.Unavailable, Enabled: false), new("Claude", ProviderStatus.Unavailable)];
        using var form = new SetupForm(flow, () => states, _ => { }, () => p, value => p = value,
            new Startup(), () => { }, () => { });
        form.Show(); Button(form, "Get Started").PerformClick();
        var choices = Descendants(form).OfType<CheckBox>().ToArray(); Assert.False(choices.Single(c => c.AccessibleName == "Monitor Codex").Checked);
        choices.Single(c => c.AccessibleName == "Monitor Claude Code").Checked = false;
        Assert.False(p.ClaudeEnabled); Assert.DoesNotContain(SetupStep.Claude, flow.Steps);
        Button(form, "Continue").PerformClick(); Assert.Equal(SetupStep.Verify, flow.Step);
        Assert.False(Button(form, "Retry").Enabled);
    });

    [Fact]
    public Task OnboardingEditsRealPreferencesAndStartupAndCompletes() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentMeter-setup-" + Guid.NewGuid());
        try
        {
            var completion = new SetupCompletionStore(Path.Combine(root, "done.json"));
            var preferences = new PreferenceStore(Path.Combine(root, "preferences.json"));
            Assert.True(preferences.Save(new(CodexEnabled: false, ClaudeEnabled: false)));
            var flow = new SetupFlow(completion) { Codex = false, Claude = false };
            var startup = new Startup(); var finished = false;
            using var form = new SetupForm(flow, () => [], _ => { }, preferences.Load,
                p => { preferences.Save(p); Palette.Apply(p.Appearance); }, startup,
                () => startup.TrySet(!startup.Enabled), () => finished = true);
            form.Show(); Button(form, "Get Started").PerformClick();
            Button(form, "Continue").PerformClick(); Assert.Equal(SetupStep.Verify, flow.Step);
            Button(form, "Finish Anyway").PerformClick(); Assert.Equal(SetupStep.Preferences, flow.Step);
            var controls = Descendants(form).OfType<CheckBox>().ToArray();
            controls.Single(c => c.Text == "Compact Monitor").Checked = false;
            controls.Single(c => c.Text == "Tray Icon").Checked = false;
            Assert.False(preferences.Load().CompactMonitor); Assert.False(preferences.Load().TrayIcon);
            var appearance = Descendants(form).OfType<ComboBox>().Single();
            foreach (var value in Enum.GetValues<Appearance>()) { appearance.SelectedIndex = (int)value; Assert.Equal(value, preferences.Load().Appearance); }
            var launch = controls.Single(c => c.Text == "Launch at Startup");
            typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(launch, [EventArgs.Empty]);
            Assert.True(startup.Enabled); Assert.True(launch.Checked);
            Button(form, "Continue").PerformClick(); Button(form, "Start Llumi").PerformClick();
            Assert.True(completion.IsComplete()); Assert.True(finished);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
