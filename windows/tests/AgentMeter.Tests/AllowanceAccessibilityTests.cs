using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class AllowanceAccessibilityTests
{
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    [Theory]
    [InlineData(null, false, "Remaining allowance unavailable")]
    [InlineData(0d, false, "0 percent remaining")]
    [InlineData(40d, false, "40 percent remaining")]
    [InlineData(95d, false, "95 percent remaining")]
    [InlineData(100d, false, "100 percent remaining")]
    [InlineData(40d, true, "40 percent remaining, stale")]
    [InlineData(0d, true, "0 percent remaining, stale")]
    [InlineData(double.NaN, false, "Remaining allowance unavailable")]
    [InlineData(double.PositiveInfinity, false, "Remaining allowance unavailable")]
    [InlineData(-1d, false, "Remaining allowance unavailable")]
    [InlineData(101d, false, "Remaining allowance unavailable")]
    public Task AllowanceIndicatorExposesTheReadingAsReadOnlyText(double? remaining, bool stale, string expected) => Sta(() =>
    {
        using var host = new Form();
        using var bar = new UsageBar { Size = new(200, 5) };
        host.Controls.Add(bar); _ = bar.Handle;
        bar.UpdateValue(remaining, stale);
        var accessible = bar.AccessibilityObject;
        Assert.Equal(AccessibleRole.StaticText, accessible.Role);
        Assert.True(accessible.State.HasFlag(AccessibleStates.ReadOnly));
        Assert.False(accessible.State.HasFlag(AccessibleStates.Focusable));
        Assert.Equal(expected, accessible.Name);
        Assert.Equal(expected, accessible.Value);
        Assert.True(string.IsNullOrEmpty(accessible.DefaultAction));
        Assert.False(bar.TabStop);
    });

    [Fact]
    public Task CachedAllowanceObjectTracksChangingValuesAndCannotEditTheReading() => Sta(() =>
    {
        using var host = new Form();
        using var bar = new UsageBar { Size = new(200, 5) };
        host.Controls.Add(bar); _ = bar.Handle;
        var accessible = bar.AccessibilityObject;
        Assert.Equal("Remaining allowance unavailable", accessible.Value);
        var clicks = 0; bar.Click += (_, _) => clicks++;
        foreach (var reading in new (double? Value, bool Stale, string Expected)[]
        {
            (40, false, "40 percent remaining"),
            (40, true, "40 percent remaining, stale"),
            (0, false, "0 percent remaining"),
            (100, false, "100 percent remaining"),
            (null, false, "Remaining allowance unavailable"),
            (95, false, "95 percent remaining")
        })
        {
            bar.UpdateValue(reading.Value, reading.Stale);
            Assert.Same(accessible, bar.AccessibilityObject);
            Assert.Equal(reading.Expected, accessible.Name);
            Assert.Equal(reading.Expected, accessible.Value);
            accessible.Value = "50";
            accessible.DoDefaultAction();
            Assert.Equal(reading.Expected, accessible.Value);
            Assert.Equal(reading.Expected, accessible.Name);
            Assert.Equal(0, clicks);
        }
    });

    [Fact]
    public Task SettingsDestinationNameDoesNotInheritTheHiddenMonitoringOffMessage() => Sta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        var settings = Assert.Single(form.Controls.Cast<Control>(), control => control.Name == "settings");
        var accessible = settings.AccessibilityObject;
        var now = DateTimeOffset.UtcNow;
        ProviderState State(string provider, bool enabled) => new(provider, ProviderStatus.Ready,
            new UsageSnapshot([new("five_hour", "5 hours", 60, now.AddHours(1))], now, "fixture"),
            Enabled: enabled, Authentication: AuthenticationStatus.Verified);
        form.Show();
        foreach (var enabled in new[] { true, false, true })
        {
            form.SetPreferences(new(CodexEnabled: enabled, ClaudeEnabled: enabled));
            form.Render([State("Codex", enabled), State("Claude", enabled)], false, false);
            form.ShowUsage(); form.ShowSettings();
            Assert.Same(accessible, settings.AccessibilityObject);
            Assert.True(settings.Visible);
            Assert.Equal("Settings", settings.AccessibleName);
            Assert.Equal("Settings", accessible.Name);
        }
    });

    [Fact]
    public Task DestinationAndIdentityNamesDoNotInheritHiddenRetryStatus() => Sta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        var now = DateTimeOffset.UtcNow;
        form.Render([new("Codex", ProviderStatus.Loading),
            new("Claude", ProviderStatus.Error, Failure: FailureKind.RateLimited, RetryAt: now.AddMinutes(1))], true, false);
        form.Show(); form.ShowSettings();
        var settings = Assert.Single(form.Controls.Cast<Control>(), control => control.Name == "settings");
        Assert.Contains(settings.Controls.OfType<Label>(), label => label.Text.Contains("Claude Code: Retry in", StringComparison.Ordinal));
        var artwork = settings.Controls.OfType<ProviderArtwork>().ToArray();
        Assert.Equal(new[] { "Codex", "Claude Code" }, artwork.Select(control => control.AccessibilityObject.Name));
        var about = Assert.Single(form.Controls.OfType<AboutView>());
        var aboutObject = about.AccessibilityObject;
        var body = Assert.Single(about.Controls.OfType<FlowLayoutPanel>());
        var bodyObject = body.AccessibilityObject;
        for (var visit = 0; visit < 2; visit++)
        {
            form.ShowAbout();
            Assert.False(settings.Visible);
            Assert.Same(aboutObject, about.AccessibilityObject);
            Assert.Equal("About Llumi", aboutObject.Name);
            Assert.Same(bodyObject, body.AccessibilityObject);
            Assert.Equal("About details", bodyObject.Name);
            Assert.Equal("App identity", Assert.Single(body.Controls.OfType<Panel>()).AccessibilityObject.Name);
            form.ShowUsage();
            var usage = Assert.Single(form.Controls.OfType<Panel>(), panel => panel.Controls.OfType<ProviderCard>().Any());
            Assert.True(usage.Visible);
            Assert.Equal("Usage", usage.AccessibilityObject.Name);
            form.ShowSettings();
            Assert.Equal("Settings", settings.AccessibilityObject.Name);
        }
    });
}
