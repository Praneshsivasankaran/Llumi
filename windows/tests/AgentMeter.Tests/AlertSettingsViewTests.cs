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
