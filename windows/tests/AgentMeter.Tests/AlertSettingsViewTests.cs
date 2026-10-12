using System.Reflection;
using System.Runtime.ExceptionServices;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class AlertSettingsViewTests
{
    [Fact]
    public void DuplicateDraftIsNotSavedAndCorrectionKeepsProviderAndThresholdChoices() => RunSta(() =>
    {
        using var view = new AlertSettingsView();
        view.SetSettings(new(true, false, true, [new(75), new(90, false), new(95)]));
        var saved = new List<UsageAlertSettings>(); view.SettingsChanged += saved.Add;
        var percentages = view.Controls.OfType<NumericUpDown>().OrderBy(c => c.TabIndex).ToArray();
        percentages[0].Value = 90;
        Assert.Empty(saved);
        Assert.Contains("not been saved", Find<Label>(view, "Alert threshold validation").Text);
        percentages[0].Value = 80;
        var result = Assert.Single(saved);
        Assert.Equal(new[] { 80, 90, 95 }, result.EffectiveThresholds.Select(t => t.Percent));
        Assert.False(result.EffectiveThresholds[1].Enabled); Assert.False(result.CodexEnabled); Assert.True(result.ClaudeEnabled);
        Assert.DoesNotContain("not been saved", Find<Label>(view, "Alert threshold validation").Text);
    });

    [Theory]
    [InlineData("Usage alerts")]
    [InlineData("Codex alerts")]
    [InlineData("Claude Code alerts")]
    [InlineData("Enable alert threshold 2")]
    [InlineData("Snooze alerts for 1 hour")]
    [InlineData("Snooze alerts until tomorrow at 09:00 local time")]
    [InlineData("Resume alerts")]
    public void InvalidThresholdDraftDoesNotBlockIndependentActionsOrLeakIntoSavedSettings(string action) => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application);
        var original = new Preferences(false, false, Appearance.Dark, false, true, true,
            new(true, SnoozedUntil: DateTimeOffset.UtcNow.AddHours(2)));
        form.SetPreferences(original);
        var saved = new List<Preferences>();
        // The production owner sends accepted settings straight back to the view.
        form.PreferencesChanged += value => { saved.Add(value); form.SetPreferences(value); };
        var draft = Find<NumericUpDown>(form, "Alert threshold 2, percent used");
        draft.Value = 75;
        Assert.Empty(saved);
        var before = DateTimeOffset.UtcNow;
        var control = Find<Control>(form, action);
        if (control is ToggleSwitch toggle) toggle.Checked = false;
        else Click(Assert.IsAssignableFrom<Button>(control));

        var accepted = Assert.Single(saved);
        Assert.Equal(original with { Alerts = null }, accepted with { Alerts = null });
        var settings = accepted.EffectiveAlerts;
        Assert.True(settings.IsValid);
        Assert.Equal(new[] { 75, 90, 95 }, settings.EffectiveThresholds.Select(threshold => threshold.Percent));
        Assert.Equal(action != "Usage alerts", settings.Enabled);
        Assert.Equal(action != "Codex alerts", settings.CodexEnabled);
        Assert.Equal(action != "Claude Code alerts", settings.ClaudeEnabled);
        Assert.Equal(action != "Enable alert threshold 2", settings.EffectiveThresholds[1].Enabled);
        Assert.True(settings.EffectiveThresholds[0].Enabled); Assert.True(settings.EffectiveThresholds[2].Enabled);
        if (action == "Resume alerts")
        {
            Assert.InRange(settings.SnoozedUntil!.Value, before, DateTimeOffset.UtcNow);
            Assert.False(settings.IsSnoozed(DateTimeOffset.UtcNow));
            Assert.Equal("Alerts are not snoozed.", Find<Label>(form, "Alert snooze status").Text);
        }
        else if (action == "Snooze alerts for 1 hour")
            Assert.InRange(settings.SnoozedUntil!.Value, before.AddHours(1), DateTimeOffset.UtcNow.AddHours(1));
        else if (action == "Snooze alerts until tomorrow at 09:00 local time")
            Assert.Equal(AlertSettingsView.TomorrowMorning(before, TimeZoneInfo.Local), settings.SnoozedUntil);
        else Assert.Equal(original.EffectiveAlerts.SnoozedUntil, settings.SnoozedUntil);
        Assert.Equal(75m, draft.Value);
        Assert.Contains("not been saved", Find<Label>(form, "Alert threshold validation").Text);

        draft.Value = 85;
        Assert.Equal(2, saved.Count);
        var corrected = saved[1].EffectiveAlerts;
        Assert.True(corrected.IsValid);
        Assert.Equal(new[] { 75, 85, 95 }, corrected.EffectiveThresholds.Select(threshold => threshold.Percent));
        Assert.Equal(settings with { Thresholds = null }, corrected with { Thresholds = null });
        Assert.Equal(settings.EffectiveThresholds.Select(threshold => threshold.Enabled), corrected.EffectiveThresholds.Select(threshold => threshold.Enabled));
        Assert.DoesNotContain("not been saved", Find<Label>(form, "Alert threshold validation").Text);
    });

    [Fact]
    public void FailedIndependentSaveRestoresAcceptedSwitchWhileKeepingInvalidDraft() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application);
        var original = new Preferences(Alerts: new(true));
        form.SetPreferences(original);
        var attempted = new List<Preferences>();
        form.PreferencesChanged += value => { attempted.Add(value); form.SetPreferences(original); form.PreferenceSaveFailed(); };
        var draft = Find<NumericUpDown>(form, "Alert threshold 2, percent used");
        draft.Value = 75;
        var enabled = Find<ToggleSwitch>(form, "Usage alerts");
        enabled.Checked = false;
        var rejected = Assert.Single(attempted);
        Assert.False(rejected.EffectiveAlerts.Enabled); Assert.True(rejected.EffectiveAlerts.IsValid);
        Assert.Equal(new[] { 75, 90, 95 }, rejected.EffectiveAlerts.EffectiveThresholds.Select(threshold => threshold.Percent));
        Assert.True(enabled.Checked); Assert.Equal(75m, draft.Value);
        Assert.Contains("not been saved", Find<Label>(form, "Alert threshold validation").Text);
        Assert.Contains(Descendants(form).OfType<Label>(), label => label.Text.Contains("previous preferences remain active", StringComparison.Ordinal));
    });

    [Fact]
    public void SnoozeAndResumePersistDeadlineAndTestDoesNotChangeSettings() => RunSta(() =>
    {
        using var view = new AlertSettingsView();
        var until = DateTimeOffset.UtcNow.AddHours(2);
        view.SetSettings(new(true, SnoozedUntil: until));
        var saved = new List<UsageAlertSettings>(); view.SettingsChanged += saved.Add;
        var tests = 0; view.TestNotificationRequested += () => tests++;
        Click(Find<Button>(view, "Send test notification"));
        Assert.Equal(1, tests); Assert.Empty(saved);
        Assert.Contains(until.ToLocalTime().ToString("HH:mm"), Find<Label>(view, "Alert snooze status").Text);
        var before = DateTimeOffset.UtcNow;
        Click(Find<Button>(view, "Resume alerts"));
        var resumed = Assert.Single(saved);
        Assert.NotNull(resumed.SnoozedUntil);
        Assert.InRange(resumed.SnoozedUntil.Value, before, DateTimeOffset.UtcNow);
        Assert.False(resumed.IsSnoozed(DateTimeOffset.UtcNow));
        Assert.Equal("Alerts are not snoozed.", Find<Label>(view, "Alert snooze status").Text);
        Click(Find<Button>(view, "Snooze alerts for 1 hour"));
        Assert.Equal(2, saved.Count);
        Assert.InRange(saved[1].SnoozedUntil!.Value, before.AddHours(1), DateTimeOffset.UtcNow.AddHours(1));
    });

    [Fact]
    public void TomorrowSnoozeTargetsLocalCalendarMorningAcrossOffsetChanges()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("FixtureIndia", TimeSpan.FromHours(5.5), "FixtureIndia", "FixtureIndia");
        var now = new DateTimeOffset(2026, 10, 12, 22, 30, 0, TimeSpan.Zero);
        var deadline = AlertSettingsView.TomorrowMorning(now, india);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 3, 30, 0, TimeSpan.Zero), deadline);
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var dstEve = new DateTimeOffset(2026, 3, 7, 23, 0, 0, TimeSpan.FromHours(-8));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 16, 0, 0, TimeSpan.Zero), AlertSettingsView.TomorrowMorning(dstEve, pacific));
    }

    [Theory]
    [InlineData(252, 1f)]
    [InlineData(504, 2f)]
    [InlineData(824, 1.5f)]
    public void AlertControlsRemainInsideScrollableSettingsAtNarrowWidthsAndScaling(int width, float scale) => RunSta(() =>
    {
        using var view = new AlertSettingsView();
        view.Arrange(width, scale);
        foreach (Control control in view.Controls)
        {
            Assert.True(control.Left >= 0 && control.Right <= view.Width, control.AccessibleName ?? control.Text);
            Assert.True(control.Top >= 0 && control.Bottom <= view.Height, control.AccessibleName ?? control.Text);
        }
        foreach (var percent in view.Controls.OfType<NumericUpDown>())
        {
            var number = percent.TabIndex - 1;
            var toggle = view.Controls.OfType<ToggleSwitch>().Single(t => t.TabIndex == number);
            Assert.True(toggle.Right < percent.Left);
        }
    });

    [Fact]
    public void UsageFormWiresSettingsWithoutLosingExistingPreferencesAndExposesNotificationFeedback() => RunSta(() =>
    {
        using var icon = AppIcon.Load(); using var form = new UsageForm(["Codex", "Claude Code"], icon);
        var original = new Preferences(false, false, Appearance.Dark, false, true, false,
            new(true, false, true, [new(70), new(85), new(99)], DateTimeOffset.UtcNow.AddHours(1)));
        form.SetPreferences(original);
        Preferences? saved = null; form.PreferencesChanged += value => saved = value;
        Find<ToggleSwitch>(form, "Start in background").Checked = true;
        Assert.NotNull(saved); Assert.True(saved.StartInBackground); Assert.False(saved.TrayIcon);
        Assert.False(saved.CompactMonitor); Assert.False(saved.CodexEnabled); Assert.Equal(Appearance.Dark, saved.Appearance);
        Assert.Same(original.Alerts, saved.Alerts);
        var tests = 0; form.TestNotificationRequested += () => tests++;
        Click(Find<Button>(form, "Send test notification")); Assert.Equal(1, tests);
        form.SetAlertStatus("Windows notifications are disabled. Check Windows Settings.");
        Assert.Contains("disabled", Find<Label>(form, "Notification status").Text);
        form.Render([], false, false, new Rectangle(0, 0, 380, 640)); form.ShowSettings();
        var settings = Descendants(form).Single(c => c.Name == "settings") as ScrollableControl;
        Assert.NotNull(settings); Assert.True(settings.AutoScrollMinSize.Height > settings.ClientSize.Height);
        Assert.False(form.Visible);
    });

    [Fact]
    public void BackgroundEntryKeepsTrayDisabledAppReachableAndExplicitOpenRestoresIt() => RunSta(() =>
    {
        using var icon = AppIcon.Load(); using var form = new UsageForm([], icon);
        form.SetPreferences(new(TrayIcon: true, StartInBackground: true));
        form.ShowBackgroundEntry(); Assert.False(form.Visible);
        form.SetPreferences(new(TrayIcon: false, StartInBackground: true));
        form.ShowBackgroundEntry(); Assert.True(form.Visible); Assert.True(form.ShowInTaskbar);
        Assert.Equal(FormWindowState.Minimized, form.WindowState);
        form.ShowPanel(new Point(100, 100)); Assert.Equal(FormWindowState.Normal, form.WindowState);
        form.Hide();
    });

    private static T Find<T>(Control root, string name) where T : Control => Descendants(root).OfType<T>().Single(c => c.AccessibleName == name);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); failure?.Throw();
    }
}
