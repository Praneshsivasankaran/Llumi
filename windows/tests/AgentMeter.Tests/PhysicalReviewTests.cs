using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class PhysicalReviewTests
{
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static IEnumerable<Control> Controls(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Controls(child)));
    private static bool LightPixel(MonitorForm form)
    {
        using var bitmap = form.CreatePreviewBitmap();
        return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height - Math.Max(4, form.DeviceDpi / 24)).GetBrightness() > .65;
    }
    [Theory]
    [InlineData(1, false, true)] [InlineData(2, true, false)]
    [InlineData(0, true, true)] [InlineData(0, false, false)]
    public Task MonitorUsesEffectiveAppearance(int preference, bool osLight, bool expectedLight) => Sta(() =>
    {
        try
        {
            Palette.Apply((Appearance)preference, osLight);
            using var icon = AppIcon.Load(); using var form = new MonitorForm(["Codex", "Claude"], icon) { MotionAllowed = () => false };
            form.Render([new("Codex", ProviderStatus.Loading)]);
            Assert.Equal(expectedLight, LightPixel(form));
            form.SetExpanded(true, false);
            Assert.Equal(expectedLight, LightPixel(form));
        }
        finally { Palette.Apply(Appearance.System); }
    });
    [Theory]
    [InlineData(1, 2, false)] [InlineData(2, 1, true)]
    [InlineData(0, 0, true)]
    public Task VisibleMonitorRedrawsWithoutGeometryOrProviderReset(int before, int after, bool osLight) => Sta(() =>
    {
        try
        {
            Palette.Apply((Appearance)before, !osLight);
            using var icon = AppIcon.Load(); using var form = new MonitorForm(["Codex"], icon) { MotionAllowed = () => false };
            form.Render([new("Codex", ProviderStatus.Loading)]); form.ShowMonitor(new(100, 100));
            form.SetExpanded(true, false);
            var bounds = form.Bounds; var oldLight = LightPixel(form); var version = form.RenderVersion;
            Palette.Apply((Appearance)after, osLight); form.UpdateSurface();
            Assert.True(form.Visible); Assert.True(form.Expanded); Assert.Equal(bounds, form.Bounds);
            Assert.True(form.RenderVersion > version); Assert.NotEqual(oldLight, LightPixel(form));
            form.Render([new("Codex", ProviderStatus.Error, Failure: FailureKind.Timeout)]);
            Assert.NotEqual(oldLight, LightPixel(form)); Assert.Equal(bounds, form.Bounds);
        }
        finally { Palette.Apply(Appearance.System); }
    });
    [Fact]
    public Task SettingsContainsLiveChecksAndSanitizedDiagnostics() => Sta(() =>
    {
        using var icon = AppIcon.Load(); using var form = new UsageForm(["Codex", "Claude Code"], icon);
        form.Show(); form.ShowSettings();
        var refreshes = 0; form.RefreshRequested += () => refreshes++;
        form.Render([new("Codex", ProviderStatus.Ready, new([], DateTimeOffset.UtcNow, "private@example.test"), Authentication: AuthenticationStatus.Verified),
            new("Claude Code", ProviderStatus.Error, Detail: "secret-token", Failure: FailureKind.LoggedOut, Authentication: AuthenticationStatus.SignedOut)], false, false);
        var controls = Controls(form).ToArray();
        Assert.Contains(controls.OfType<Label>(), c => c.Visible && c.Text.Contains("Signed in") && c.Text.Contains("Allowances not reported"));
        Assert.Contains(controls.OfType<Label>(), c => c.Visible && c.Text.Contains("Sign in required"));
        controls.OfType<Button>().Single(b => b.Text == "Retry").PerformClick(); Assert.Equal(1, refreshes);
        Assert.Contains(controls.OfType<Button>(), b => b.Text == "Copy Diagnostics" && b.Parent?.Name == "settings");
        Assert.DoesNotContain("private", form.DiagnosticReport()); Assert.DoesNotContain("secret", form.DiagnosticReport());
        form.Render([new("Codex", ProviderStatus.Error, Failure: FailureKind.Timeout)], false, false);
        Assert.DoesNotContain(controls.OfType<Label>(), c => c.Text.Contains("Signed in"));
        form.ShowUsage(); Assert.False(controls.OfType<Button>().Single(b => b.Text == "Copy Diagnostics").Visible);
    });
    [Fact]
    public Task SettingsActionsRemainReachableOnShortAndNarrowWindows() => Sta(() =>
    {
        using var icon = AppIcon.Load(); using var form = new UsageForm(["Codex"], icon);
        form.Show(); form.ShowSettings();
        foreach (var size in new[] { new Size(380, 320), new Size(640, 440) })
        {
            form.ClientSize = size;
            var panel = Controls(form).OfType<Panel>().Single(p => p.Name == "settings");
            foreach (var button in panel.Controls.OfType<Button>())
            {
                panel.ScrollControlIntoView(button);
                Assert.True(panel.ClientRectangle.Contains(button.Bounds), $"Unreachable action: {button.Text}");
            }
        }
    });
    [Fact]
    public void FreshMigrationMarkerDoesNotSkipWelcomeAndPreferenceEditsDoNotCompleteSetup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Llumi-first-run-" + Guid.NewGuid());
        try
        {
            var current = Path.Combine(root, "current"); LegacyPreferences.Migrate(Path.Combine(root, "old"), current);
            Assert.True(File.Exists(Path.Combine(current, "llumi-migration.json")));
            var completion = new SetupCompletionStore(Path.Combine(current, "setup-completed.json"));
            var preferences = new PreferenceStore(Path.Combine(current, "v2-preferences.json"));
            Assert.False(completion.RecognizeExisting(preferences.HasValidExistingPreferences()));
            preferences.Save(new(Appearance: Appearance.Light));
            Assert.False(completion.RecognizeExisting(preferences.HasValidExistingPreferences()));
            Assert.Equal(SetupStep.Welcome, new SetupFlow(completion).Step);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
