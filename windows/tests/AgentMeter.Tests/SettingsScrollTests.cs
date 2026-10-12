using System.Runtime.ExceptionServices;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class SettingsScrollTests
{
    [Theory]
    [InlineData(380)]
    [InlineData(640)]
    public void RepeatedProviderRendersDoNotMoveScrolledAlertWindow(int width) => RunSta(() =>
    {
        using var form = CreateForm(width);
        var panel = Settings(form);
        var alerts = Assert.Single(panel.Controls.OfType<AlertSettingsView>());
        panel.AutoScrollPosition = new Point(0, int.MaxValue);
        Application.DoEvents();
        var scroll = panel.AutoScrollPosition;
        var bounds = alerts.Bounds;
        var moves = 0;
        alerts.LocationChanged += (_, _) => moves++;
        for (var index = 0; index < 8; index++)
        {
            form.Render(States(), false, false);
            Application.DoEvents();
            Assert.Equal(scroll, panel.AutoScrollPosition);
            Assert.Equal(bounds, alerts.Bounds);
        }
        Assert.Equal(0, moves);
    });

    [Theory]
    [InlineData(380)]
    [InlineData(640)]
    public void ProviderLoadingAndRecoveryKeepSettingsBottomVisible(int width) => RunSta(() =>
    {
        using var form = CreateForm(width);
        var panel = Settings(form);
        panel.AutoScrollPosition = new Point(0, int.MaxValue);
        Application.DoEvents();
        var originalExtent = panel.DisplayRectangle.Height;
        foreach (var loading in new[] { true, false, true, false })
        {
            form.Render(States(loading), loading, false);
            Application.DoEvents();
            var belowViewport = panel.DisplayRectangle.Height + panel.AutoScrollPosition.Y - panel.ClientSize.Height;
            Assert.InRange(belowViewport, 0, 1);
            Assert.False(panel.HorizontalScroll.Visible);
            if (loading) Assert.True(panel.DisplayRectangle.Height > originalExtent);
        }
    });

    [Theory]
    [InlineData(380)]
    [InlineData(640)]
    public void ProviderLoadingAndRecoveryKeepVisibleAlertControlAtItsPosition(int width) => RunSta(() =>
    {
        using var form = CreateForm(width);
        var panel = Settings(form);
        var alerts = Assert.Single(panel.Controls.OfType<AlertSettingsView>());
        var anchor = alerts.Controls.Cast<Control>().Single(control => control.AccessibleName == "Claude Code alerts");
        var logicalTop = alerts.Top - panel.AutoScrollPosition.Y + anchor.Top;
        panel.AutoScrollPosition = new Point(0, logicalTop);
        Application.DoEvents();
        var initialTop = panel.PointToClient(anchor.PointToScreen(Point.Empty)).Y;
        Assert.InRange(initialTop, 0, 1);
        Assert.True(panel.DisplayRectangle.Height + panel.AutoScrollPosition.Y > panel.ClientSize.Height);
        foreach (var loading in new[] { true, false, true, false })
        {
            form.Render(States(loading), loading, false);
            Application.DoEvents();
            Assert.Equal(initialTop, panel.PointToClient(anchor.PointToScreen(Point.Empty)).Y);
            Assert.True(panel.ClientRectangle.Contains(panel.RectangleToClient(anchor.RectangleToScreen(anchor.ClientRectangle))));
            Assert.False(panel.HorizontalScroll.Visible);
        }
    });

    private static UsageForm CreateForm(int width)
    {
        var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application) { AllowExit = true };
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        form.ClientSize = new Size(S(width), S(440));
        form.Render(States(), false, false);
        form.Show(); form.ShowSettings(); Application.DoEvents();
        return form;
    }

    private static Panel Settings(UsageForm form) => (Panel)form.Controls.Find("settings", true).Single();

    private static ProviderState[] States(bool loading = false)
    {
        var now = DateTimeOffset.UtcNow;
        return [State("Codex", "codex/primary"), State("Claude Code", "five_hour")];
        ProviderState State(string name, string window) => loading
            ? new(name, ProviderStatus.Loading)
            : new(name, ProviderStatus.Ready, new UsageSnapshot([new(window, name, 25, now.AddHours(2), 300)], now, "Synthetic settings layout"),
                Authentication: AuthenticationStatus.Verified);
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); failure?.Throw();
    }
}
