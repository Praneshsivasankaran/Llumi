using System.Drawing.Imaging;
using AgentMeter;
using AgentMeter.Core;
using AppAppearance = AgentMeter.Appearance;

// Development-only harness. The default/capture path contains fixed synthetic values;
// the explicit first-run path uses real providers with isolated Llumi storage and startup.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--first-run-live") return LiveFirstRunReview.Run(args[1]);
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--capture"))
        {
            MessageBox.Show("Choose the synthetic preview, --capture <directory>, or --first-run-live <new absolute review directory>.",
                "Llumi local review", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        using var review = new Review();
        if (args.Length == 2 && args[0] == "--capture") { review.CapturePreviews(Path.GetFullPath(args[1])); return 0; }
        Application.Run(review);
        return 0;
    }
}
internal sealed class Review : Form
{
    private readonly Icon icon = AppIcon.Load();
    private readonly UsageForm usage;
    private readonly MonitorForm monitor;
    private readonly ComboBox scenario = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205 };
    private readonly ComboBox theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private Preferences preferences = new(Appearance: AppAppearance.Light);
    private ProviderState[] states = [];
    private SetupForm? setup;
    private SetupFlow? setupFlow;
    private bool startupEnabled;
    internal Review()
    {
        Text = "Llumi local review — synthetic examples"; Icon = icon;
        StartPosition = FormStartPosition.Manual; Location = new(24, 24); ClientSize = new(650, 115);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(10) }; Controls.Add(panel);
        panel.Controls.Add(new Label { Text = "Synthetic data · installed app and accounts are untouched", AutoSize = true, Width = 600, Margin = new(0, 0, 0, 8) });
        scenario.Items.AddRange(["All supported allowances", "Weekly only", "Not reported", "Unsupported format", "Unsupported billing", "Stale", "Unavailable", "Both providers off"]);
        theme.Items.AddRange(["Light", "Dark"]); panel.Controls.Add(scenario); panel.Controls.Add(theme);
        void Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => action(); panel.Controls.Add(b); }
        usage = new UsageForm(["Codex", "Claude Code"], icon) { AllowExit = false, Text = "Llumi — local synthetic review" };
        monitor = new MonitorForm(["Codex", "Claude Code"], icon) { AllowExit = true, MotionAllowed = () => false };
        Button("Usage", () => { usage.Show(); usage.ShowUsage(); usage.Activate(); });
        Button("Settings", () => { usage.Show(); usage.ShowSettings(); usage.Activate(); });
        Button("Setup", ShowSetup);
        usage.PreferencesChanged += value => { preferences = value; Apply(); };
        usage.RefreshRequested += Apply;
        usage.StartupToggleRequested += () => { startupEnabled = !startupEnabled; usage.SetStartupState(startupEnabled, true); };
        usage.ResetPositionRequested += ResetPosition;
        usage.SetupRequested += ShowSetup;
        usage.ExitRequested += Close;
        monitor.OpenRequested += () => { usage.Show(); usage.ShowUsage(); usage.Activate(); };
        monitor.ResetPositionRequested += ResetPosition;
        monitor.UnpinRequested += () => monitor.HideMonitor();
        monitor.RefreshRequested += Apply;
        monitor.ExitRequested += Close;
        scenario.SelectedIndexChanged += (_, _) => { preferences = preferences with { CodexEnabled = scenario.SelectedIndex != 7, ClaudeEnabled = scenario.SelectedIndex != 7 }; Apply(); };
        theme.SelectedIndexChanged += (_, _) => { preferences = preferences with { Appearance = theme.SelectedIndex == 0 ? AppAppearance.Light : AppAppearance.Dark }; Apply(); };
        theme.SelectedIndex = 0; scenario.SelectedIndex = 0;
        Shown += (_, _) => { usage.Location = new(24, Bottom + 12); usage.Show(); Apply(); };
        FormClosed += (_, _) => { setup?.Close(); usage.AllowExit = true; monitor.AllowExit = true; usage.Close(); monitor.Close(); };
    }
    private void ResetPosition()
    {
        var screen = monitor.Visible ? Screen.FromControl(monitor) : Screen.PrimaryScreen!;
        monitor.SetExpanded(false, false);
        monitor.Location = MonitorPosition.Restore(new(1, screen.DeviceName, 20, 20), screen.DeviceName,
            screen.WorkingArea, monitor.Size, monitor.DeviceDpi);
        monitor.KeepOnScreen(); monitor.UpdateSurface();
    }
    private void Apply()
    {
        Palette.Apply(preferences.Appearance);
        var now = DateTimeOffset.UtcNow;
        ProviderState Normal(string name, UsageWindow[] windows) => new(name, ProviderStatus.Ready, new(windows, now, "Synthetic review fixture"), Authentication: AuthenticationStatus.Verified);
        states = [Normal("Codex", [new("codex/primary", "Codex", 23, now.AddHours(3), 300),
            new("codex/secondary", "Codex", 62, now.AddDays(4), 10080),
            new("spark/primary", "Spark", 0, now.AddHours(2), 300, UsageScope.Additional, "Spark", "spark")]),
            Normal("Claude Code", [new("five_hour", "Claude", 37, now.AddHours(2)), new("seven_day", "Claude", 49, now.AddDays(5)),
            new("seven_day_sonnet", "Sonnet", 20, now.AddDays(3), 10080, UsageScope.Model, "Sonnet", "claude"),
            new("seven_day_opus", "Opus", 100, now.AddDays(3), 10080, UsageScope.Model, "Opus", "claude")])];
        if (scenario.SelectedIndex == 1) states = [Normal("Codex", [new("codex/secondary", "Codex", 62, now.AddDays(4), 10080)]), Normal("Claude Code", [new("seven_day", "Claude", 49, now.AddDays(5))])];
        if (scenario.SelectedIndex is 2 or 3)
        {
            var availability = scenario.SelectedIndex == 2 ? AllowanceAvailability.NotReported : AllowanceAvailability.UnsupportedFormat;
            states = states.Select(s => s with { Snapshot = new([], now, "Synthetic review fixture", Availability: availability) }).ToArray();
        }
        if (scenario.SelectedIndex == 4) states = states.Select(s => s with { Snapshot = null, Status = ProviderStatus.Unavailable, Failure = FailureKind.UnsupportedBilling, Authentication = AuthenticationStatus.UnsupportedBilling }).ToArray();
        if (scenario.SelectedIndex == 5) states = states.Select(s => s with { Snapshot = s.Snapshot! with { ObservedAt = now.AddMinutes(-5) }, Status = ProviderStatus.Error, Failure = FailureKind.Network }).ToArray();
        if (scenario.SelectedIndex == 6) states = states.Select(s => s with { Snapshot = null, Status = ProviderStatus.Error, Failure = FailureKind.Network, Authentication = AuthenticationStatus.Verified }).ToArray();
        states = states.Select(s => (s.Name == "Codex" ? preferences.CodexEnabled : preferences.ClaudeEnabled) ? s : s with { Enabled = false, Snapshot = null, Authentication = AuthenticationStatus.Unknown }).ToArray();
        usage.SetPreferences(preferences); usage.SetStartupState(startupEnabled, true); usage.Render(states, false, false);
        monitor.SetProviders(states.Where(s => s.Enabled).Select(s => s.Name)); monitor.Render(states);
        if (preferences.CompactMonitor && states.Any(s => s.Enabled))
        { if (!monitor.Visible) { ResetPosition(); monitor.ShowMonitor(monitor.Location); } monitor.UpdateSurface(); }
        else monitor.HideMonitor();
        setup?.RefreshStatuses();
    }
    private void ShowSetup()
    {
        if (setup is { IsDisposed: false }) { setup.Show(); setup.Activate(); return; }
        // This completion flag is isolated from production storage and contains no provider data.
        var path = Path.Combine(Path.GetTempPath(), "Llumi-local-review-" + Environment.ProcessId, "setup-completed.json");
        setupFlow = new SetupFlow(new(path));
        setup = new(setupFlow, () => states, _ => Apply(), () => preferences,
            value => { preferences = value; Apply(); }, new MemoryStartup(() => startupEnabled, enabled => startupEnabled = enabled),
            () => { startupEnabled = !startupEnabled; }, () => usage.Show()) { Text = "Llumi setup — synthetic review" };
        setup.Show();
    }
    internal void CapturePreviews(string directory)
    {
        Directory.CreateDirectory(directory); usage.Show();
        foreach (var index in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            scenario.SelectedIndex = index; Apply(); usage.ClientSize = new(820, 660); usage.ShowUsage(); Application.DoEvents();
            Save(usage, Path.Combine(directory, $"usage-{index}.png"));
            if (index == 0)
            {
                monitor.SetExpanded(true, false); using var expanded = monitor.CreatePreviewBitmap(); expanded.Save(Path.Combine(directory, "monitor-expanded.png"), ImageFormat.Png);
                monitor.SetExpanded(false, false); using var compact = monitor.CreatePreviewBitmap(); compact.Save(Path.Combine(directory, "monitor-compact.png"), ImageFormat.Png);
                CaptureOverflowMonitors(directory);
                usage.ShowSettings(); Application.DoEvents(); Save(usage, Path.Combine(directory, "settings.png"));
                CaptureSetupSteps(directory);
                theme.SelectedIndex = 1; Apply(); usage.ShowUsage(); Application.DoEvents(); Save(usage, Path.Combine(directory, "usage-dark.png")); theme.SelectedIndex = 0;
            }
        }
        monitor.HideMonitor(); usage.Hide();
    }

    private void CaptureOverflowMonitors(string directory)
    {
        // Explicitly synthetic native renders, not live account or physical acceptance.
        var now = DateTimeOffset.UtcNow;
        var dense = new[] { "Codex", "Claude Code" }.Select(name =>
        {
            var windows = new[] { new UsageWindow(name == "Codex" ? "codex/primary" : "five_hour", "fixture", 37, now.AddHours(3), 300) }
                .Concat(Enumerable.Range(1, 20).Select(index => new UsageWindow("extra:" + index, "fixture", index,
                    now.AddHours(index), 60, UsageScope.Additional, "Extra " + index))).ToArray();
            return new ProviderState(name, ProviderStatus.Ready, new(windows, now.AddMinutes(-3), "synthetic overflow fixture"));
        }).ToArray();
        foreach (var shortArea in new[] { false, true })
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            using var overflow = new MonitorForm(["Codex", "Claude Code"], icon, () => area) { AllowExit = true, MotionAllowed = () => false };
            int S(int value) => (int)Math.Round(value * overflow.DeviceDpi / 96d);
            area = new(area.Location, new Size(S(620), S(shortArea ? 100 : 240)));
            overflow.Render(dense, now); overflow.ShowMonitor(area.Location); overflow.SetExpanded(true, false);
            using var image = overflow.CreatePreviewBitmap();
            image.Save(Path.Combine(directory, shortArea ? "monitor-overflow-short.png" : "monitor-overflow-dense.png"), ImageFormat.Png);
        }
    }

    private void CaptureSetupSteps(string directory)
    {
        // Every image uses the same native setup surface, memory-only preferences and
        // startup adapter, and the explicitly synthetic provider states above.
        preferences = preferences with { CodexEnabled = true, ClaudeEnabled = true };
        Apply();
        setup?.Close(); ShowSetup();
        var form = setup!; var flow = setupFlow!;
        var names = new[] { "welcome", "providers", "codex", "claude", "verify", "preferences", "done" };
        for (var index = 0; index < names.Length; index++)
        {
            if (flow.Step != (SetupStep)index) throw new InvalidOperationException("Synthetic setup did not reach the requested step.");
            form.RefreshStatuses(); Application.DoEvents();
            Save(form, Path.Combine(directory, $"setup-{index + 1:00}-{names[index]}.png"));
            if (index > 0)
            {
                var originalSize = form.ClientSize; var originalMinimum = form.MinimumSize;
                int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
                form.MinimumSize = Size.Empty; form.ClientSize = new Size(S(420), S(280)); Application.DoEvents();
                Save(form, Path.Combine(directory, $"setup-{names[index]}-constrained.png"));
                form.MinimumSize = originalMinimum; form.ClientSize = originalSize; Application.DoEvents();
                if (index == 2)
                {
                    Palette.Apply(AppAppearance.Dark); form.ApplyTheme(); Application.DoEvents();
                    Save(form, Path.Combine(directory, "setup-codex-dark.png"));
                    Palette.Apply(preferences.Appearance); form.ApplyTheme(); Application.DoEvents();
                }
            }
            if (index == 0)
            {
                Save(form, Path.Combine(directory, "setup-welcome.png"));
                var originalSize = form.ClientSize;
                Palette.Apply(AppAppearance.Dark); form.ApplyTheme(); Application.DoEvents();
                Save(form, Path.Combine(directory, "setup-welcome-dark.png"));
                form.ClientSize = new Size(544, 442); Application.DoEvents();
                Save(form, Path.Combine(directory, "setup-welcome-minimum-dark.png"));
                form.ClientSize = originalSize; Palette.Apply(preferences.Appearance); form.ApplyTheme(); Application.DoEvents();
            }
            if (flow.Step == SetupStep.Verify)
            {
                var originalStates = states; var originalPreferences = preferences; var now = DateTimeOffset.UtcNow;
                states = states.Select(s => s.Name == "Codex" ? s with { Snapshot = null, Status = ProviderStatus.Error,
                    Failure = FailureKind.Network, RetryAt = now.AddSeconds(10), AutomaticRetryAt = now.AddSeconds(60) } : s).ToArray();
                form.RefreshStatuses(); Application.DoEvents();
                Save(form, Path.Combine(directory, "setup-verify-backoff.png"));
                preferences = preferences with { CodexEnabled = false, ClaudeEnabled = false };
                states = originalStates.Select(s => s with { Enabled = false, Snapshot = null, Authentication = AuthenticationStatus.Unknown }).ToArray();
                form.RefreshStatuses(); Application.DoEvents();
                Save(form, Path.Combine(directory, "setup-verify-both-off.png"));
                states = originalStates; preferences = originalPreferences;
                form.RefreshStatuses(); Application.DoEvents();
            }
            if (index + 1 < names.Length)
            {
                var next = (Button)form.AcceptButton!;
                next.PerformClick(); Application.DoEvents();
            }
        }
        form.Close();
    }
    private static void Save(Form form, string path)
    { form.Refresh(); Application.DoEvents(); using var full = new Bitmap(form.Width, form.Height); form.DrawToBitmap(full, new Rectangle(Point.Empty, form.Size)); full.Save(path, ImageFormat.Png); }
    protected override void Dispose(bool disposing)
    { if (disposing) { setup?.Dispose(); usage.Dispose(); monitor.Dispose(); icon.Dispose(); } base.Dispose(disposing); }
    private sealed class MemoryStartup(Func<bool> read, Action<bool> write) : IStartupRegistration
    {
        public bool TryRead(out bool enabled) { enabled = read(); return true; }
        public bool TrySet(bool enabled) { write(enabled); return true; }
    }
}
