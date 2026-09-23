using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ProductPassTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AgentMeter-tests-" + Guid.NewGuid());
    private SetupCompletionStore Store => new(Path.Combine(root, "setup.json"));
    private static ProviderState State(params UsageWindow[] windows) => new("Claude", ProviderStatus.Ready,
        new UsageSnapshot(windows, DateTimeOffset.UtcNow, "synthetic"));
    [Theory]
    [InlineData(10, 80)] [InlineData(80, 10)] [InlineData(37, 19)]
    public void FiveHourAlwaysWinsAndDetailsRetainBoth(double shortUsed, double weeklyUsed)
    {
        var s = State(new("seven_day", "Weekly", weeklyUsed, null), new("five_hour", "5-hour", shortUsed, null));
        Assert.Equal(100 - shortUsed, MonitorSelection.Select(s)?.RemainingPercent);
        Assert.Equal(new[] { "five_hour", "seven_day" }, MonitorSelection.Details(s).Select(w => w.Id));
    }
    [Fact]
    public void MissingMalformedAndDuplicateFiveHourNeverSubstituteWeekly()
    {
        var week = new UsageWindow("seven_day", "Weekly", 20, null);
        Assert.Null(MonitorSelection.Select(State(week)));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, 101, -1 })
            Assert.Null(MonitorSelection.Select(State(week, new("five_hour", "", invalid, null)))?.RemainingPercent);
        Assert.Null(MonitorSelection.Select(State(week, new("five_hour", "", 20, null), new("five_hour", "", 20, null))));
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
    [InlineData(FailureKind.NotInstalled, "no", "unknown")]
    [InlineData(FailureKind.LoggedOut, "yes", "signed-out")]
    [InlineData(FailureKind.Timeout, "unknown", "unknown")]
    [InlineData(FailureKind.Malformed, "unknown", "unknown")]
    public void FailureReadinessNeverGuessesAuthentication(FailureKind failure, string detected, string auth)
    {
        var d = SetupDiagnostic.From(new("Codex", ProviderStatus.Error, Failure: failure));
        Assert.Equal(detected, d.Detected); Assert.Equal(auth, d.Authentication); Assert.NotEqual("Ready", d.Status);
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
    public Task CheckAgainReadsSharedProviderStateAndAllowsFailure() => Sta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "AgentMeter-setup-" + Guid.NewGuid(), "done.json");
        ProviderState[] states = [new("Codex", ProviderStatus.Loading), new("Claude", ProviderStatus.Error, Failure: FailureKind.NotInstalled)];
        var refreshes = 0;
        using var form = new SetupForm(new(new(path)), () => states, () => {
            refreshes++; states = [new("Codex", ProviderStatus.Ready, new([], DateTimeOffset.UtcNow, "fixture"))];
        }, () => new(), _ => { }, new Startup(), () => { }, () => { });
        form.Show(); Button(form, "Set Up Llumi").PerformClick(); form.RefreshStatuses();
        Assert.Contains(Descendants(form).OfType<Label>(), l => l.Text.Contains("Authentication: unknown"));
        Button(form, "Check Again").PerformClick(); form.RefreshStatuses();
        Assert.Equal(1, refreshes);
        Assert.Contains(Descendants(form).OfType<Label>(), l => l.Text.Contains("Authentication: verified"));
        states = [new("Codex", ProviderStatus.Error, Failure: FailureKind.Timeout)]; form.RefreshStatuses();
        Assert.DoesNotContain(Descendants(form).OfType<Label>(), l => l.Text.Contains("Authentication: verified"));
    });
    [Fact]
    public Task OnboardingEditsRealPreferencesAndStartupAndCompletes() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentMeter-setup-" + Guid.NewGuid());
        try
        {
            var completion = new SetupCompletionStore(Path.Combine(root, "done.json"));
            var preferences = new PreferenceStore(Path.Combine(root, "preferences.json"));
            var flow = new SetupFlow(completion) { Codex = false, Claude = false };
            var startup = new Startup(); var finished = false;
            using var form = new SetupForm(flow, () => [], () => { }, preferences.Load,
                p => { preferences.Save(p); Palette.Apply(p.Appearance); }, startup,
                () => startup.TrySet(!startup.Enabled), () => finished = true);
            form.Show(); Button(form, "Set Up Llumi").PerformClick();
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
