using System.Reflection;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class VisualPolishTests
{
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static IEnumerable<Control> Controls(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Controls(child)));

    [Theory]
    [InlineData(380)] [InlineData(470)] [InlineData(640)] [InlineData(900)]
    public Task NavigationTracksCurrentPageAndSettingsSwitchLabelsFit(int logicalWidth) => Sta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        int S(int n) => (int)Math.Round(n * form.DeviceDpi / 96f);
        form.ClientSize = new(S(logicalWidth), S(540)); form.Show();
        var tabs = Controls(form).OfType<RoundedButton>().Where(c => c.Navigation).ToArray();
        Assert.Equal(3, tabs.Length); Assert.Equal("Usage", tabs.Single(c => c.Selected).Text);
        tabs.Single(c => c.Text == "Settings").PerformClick(); Assert.Equal("Settings", tabs.Single(c => c.Selected).Text);
        Assert.Equal("Current page", tabs.Single(c => c.Selected).AccessibleDescription);
        foreach (var toggle in Controls(form).OfType<ToggleSwitch>())
        {
            using var graphics = toggle.CreateGraphics();
            var measured = TextRenderer.MeasureText(graphics, toggle.Text, toggle.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine);
            Assert.True(measured.Width <= toggle.Width - S(62), $"{toggle.Text} needs {measured.Width}px; text area is {toggle.Width - S(62)}px.");
        }
        tabs.Single(c => c.Text == "About").PerformClick(); Assert.Equal("About", tabs.Single(c => c.Selected).Text);
        Assert.All(tabs, tab => Assert.True(tab.Parent!.ClientRectangle.Contains(tab.Bounds)));
    });

    [Theory]
    [InlineData(380)] [InlineData(470)] [InlineData(640)]
    public Task SettingsProviderCardsExpandForWrappedStatusesWithoutKeepingEmptyPadding(int logicalWidth) => Sta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        int S(int n) => (int)Math.Round(n * form.DeviceDpi / 96f);
        form.ClientSize = new(S(logicalWidth), S(540)); form.Show(); form.ShowSettings();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new UsageSnapshot([new("five_hour", "fixture", 25, now.AddHours(5))], now, "fixture");
        form.Render([new("Codex", ProviderStatus.Ready, snapshot, Authentication: AuthenticationStatus.Verified),
            new("Claude", ProviderStatus.Ready, snapshot, Authentication: AuthenticationStatus.Verified)], false, false);
        var statuses = Controls(form).OfType<Label>().Where(c => c.AccessibleName?.EndsWith("setup status") == true).ToArray();
        var retry = Controls(form).OfType<Button>().Single(c => c.AccessibleName == "Retry provider checks");
        var compactRetryTop = retry.Top;
        var lastStatus = statuses.Max(c => c.Bottom);
        Assert.InRange(retry.Top - lastStatus, S(28), S(36));
        using var largeStatusFont = new Font(form.Font.FontFamily, 17);
        foreach (var status in statuses) status.Font = largeStatusFont;
        foreach (var failure in new[] { FailureKind.UnsupportedBilling, FailureKind.Malformed, FailureKind.Timeout })
        {
            form.Render([new("Codex", ProviderStatus.Error, Failure: failure, Authentication: AuthenticationStatus.UnsupportedBilling),
                new("Claude", ProviderStatus.Error, Failure: failure, Authentication: AuthenticationStatus.Unknown)], false, false);
            Assert.True(retry.Top > compactRetryTop, "Larger wrapped status text must grow the cards and move Retry down.");
            foreach (var status in statuses)
            {
                using var graphics = status.CreateGraphics();
                var text = TextRenderer.MeasureText(graphics, status.Text, status.Font, new Size(status.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                Assert.True(status.Height >= text.Height, $"{status.Text} needs {text.Height}px but has {status.Height}px.");
                Assert.True(status.Bottom + S(14) <= retry.Top, "Status text must stay inside its card and clear the Retry action.");
            }
        }
        foreach (var status in statuses) status.Font = form.Font;
        form.Render([new("Codex", ProviderStatus.Ready, snapshot, Authentication: AuthenticationStatus.Verified),
            new("Claude", ProviderStatus.Ready, snapshot, Authentication: AuthenticationStatus.Verified)], false, false);
        Assert.Equal(compactRetryTop, retry.Top);
    });

    [Fact]
    public Task ButtonAndSwitchMotionIsFiniteAndReducedMotionIsImmediate() => Sta(() =>
    {
        using var host = new Form();
        using var panel = new Panel { Dock = DockStyle.Fill };
        using var button = new RoundedButton { Text = "Retry", Size = new(120, 40), MotionAllowed = () => false };
        using var toggle = new ToggleSwitch { Text = "Show monitor", Size = new(280, 40), Top = 50, MotionAllowed = () => false };
        panel.Controls.AddRange([button, toggle]); host.Controls.Add(panel); host.Show();
        void Hover() => typeof(RoundedButton).GetMethod("OnMouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
        var clicks = 0; button.Click += (_, _) => clicks++;
        button.PerformClick(); Assert.Equal(1, clicks);
        Hover(); toggle.Checked = true; Assert.False(button.IsAnimating); Assert.False(toggle.IsAnimating);
        button.MotionAllowed = toggle.MotionAllowed = () => true;
        Hover(); toggle.Checked = false; Assert.True(button.IsAnimating); Assert.True(toggle.IsAnimating);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while ((button.IsAnimating || toggle.IsAnimating) && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
        Assert.False(button.IsAnimating); Assert.False(toggle.IsAnimating);
        Hover(); toggle.Checked = true; host.Hide();
        Assert.False(button.IsAnimating, "A hidden button must stop its hover timer.");
        Assert.False(toggle.IsAnimating, "A hidden switch must stop its thumb timer.");
        button.Enabled = false; button.PerformClick(); Assert.Equal(1, clicks);
    });

    [Fact]
    public Task SetupLightOverrideKeepsSwitchSurfaceLightWithinDarkApplication() => Sta(() =>
    {
        try
        {
            Palette.Apply(Appearance.Dark);
            using var host = new Form();
            using var toggle = new ToggleSwitch { Size = new(280, 40), Text = "Show monitor", BackColor = Color.White,
                ForeColor = Color.Black, LightAppearanceOverride = true, MotionAllowed = () => false };
            host.Controls.Add(toggle); host.Show();
            using var light = new Bitmap(toggle.Width, toggle.Height); toggle.DrawToBitmap(light, toggle.ClientRectangle);
            toggle.LightAppearanceOverride = null;
            using var dark = new Bitmap(toggle.Width, toggle.Height); toggle.DrawToBitmap(dark, toggle.ClientRectangle);
            int S(int n) => (int)Math.Round(n * toggle.DeviceDpi / 96f);
            if (!SystemInformation.HighContrast)
            {
                Assert.True(light.GetPixel(toggle.Width - S(17), toggle.Height / 2).GetBrightness() > .6);
                Assert.True(dark.GetPixel(toggle.Width - S(17), toggle.Height / 2).GetBrightness() < .4);
            }
        }
        finally { Palette.Apply(Appearance.System); }
    });
}
