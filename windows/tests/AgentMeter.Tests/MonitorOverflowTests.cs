using System.Reflection;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class MonitorOverflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(96, 286, 3, 3, false)]
    [InlineData(96, 246, 20, 1, true)]
    [InlineData(144, 300, 20, 1, true)]
    [InlineData(192, 416, 20, 1, true)]
    [InlineData(192, 128, 20, 0, true)]
    [InlineData(384, 176, 20, 0, true)]
    public void LayoutReservesStatusAndUsageBeforeChoosingCompleteAllowanceRows(int dpi, int pixels, int count, int visible, bool overflow)
    {
        var layout = MonitorForm.DetailLayout(dpi, pixels, count); var height = pixels * 96f / dpi;
        Assert.Equal(visible, layout.VisibleRows); Assert.Equal(overflow, layout.Overflow);
        for (var index = 0; index < visible; index++)
            Assert.True(layout.AllowanceY + index * 54 + 51 <= layout.StatusY);
        if (!overflow) return;
        Assert.True(layout.UsageY >= 0); Assert.True(layout.UsageY + layout.FooterHeight <= height);
        if (!layout.CombinedStatus)
        {
            Assert.True(layout.StatusY + 19 <= layout.ObservationY);
            Assert.True(layout.ObservationY + 19 <= layout.UsageY);
            Assert.True(layout.NameY >= 0);
        }
        if (height < 55) Assert.False(layout.HeaderVisible);
    }

    [Theory]
    [InlineData(96, 3)] [InlineData(110, 3)] [InlineData(120, 2)] [InlineData(144, 3)]
    [InlineData(168, 2)] [InlineData(192, 3)] [InlineData(216, 3)]
    public void RoundedNormalSizeKeepsEveryAllowanceAndNoOverflowFooter(int dpi, int count)
    {
        var size = MonitorForm.SizeForDpi(dpi, expanded: true, allowanceRows: count);
        var layout = MonitorForm.DetailLayout(dpi, size.Height, count);
        Assert.False(layout.Overflow); Assert.Equal(count, layout.VisibleRows);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task DenseMonitorFitsWorkAreaKeepsAllValuesAccessibleAndOpensFullUsage(bool veryShort) => Sta(() =>
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        using var form = new MonitorForm(["Codex", "Claude"], SystemIcons.Application, () => area) { MotionAllowed = () => false };
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        area = new(area.Location, new Size(S(620), S(veryShort ? 100 : 240)));
        var states = DenseStates(); form.Render(states, Now); form.ShowMonitor(area.Location); form.SetExpanded(true, false);
        Assert.True(area.Contains(form.Bounds)); Assert.False(form.ShowInTaskbar); Assert.False(form.IsAnimating);
        var layout = form.ExpandedLayout;
        Assert.True(layout.Overflow); Assert.Equal(veryShort ? 0 : 1, layout.VisibleRows);
        Assert.Equal(new[] { layout.VisibleRows, layout.VisibleRows }, form.ExpandedAllowanceCounts);
        foreach (var index in new[] { 0, 1 })
        {
            var accessible = form.AccessibilityObject.GetChild(index)!.Name!;
            Assert.Contains("Extra 20 limit", accessible); Assert.Contains("Updated 3m ago", accessible);
            Assert.Contains("Stale", accessible); Assert.Contains($"({21 - layout.VisibleRows} more)", accessible);
            Assert.Contains("Open Usage", accessible);
        }
        var opens = 0; form.OpenRequested += () => opens++;
        Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, form.Height - 10, 0));
        Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 20, form.Height - 10, 0));
        Assert.True(Key(form, Keys.Enter)); Assert.True(Key(form, Keys.Control | Keys.D1)); Assert.Equal(3, opens);
        using var image = form.CreatePreviewBitmap(); Assert.Equal(form.ClientSize, image.Size);
        var output = Environment.GetEnvironmentVariable("AGENTMETER_TEST_RENDER_DIRECTORY");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            image.Save(Path.Combine(output, veryShort ? "monitor-overflow-short.png" : "monitor-overflow-dense.png"));
        }
        var few = states.Select(s => s with { Snapshot = s.Snapshot! with { Windows = s.Snapshot.Windows.Take(1).ToArray() } }).ToArray();
        if (!veryShort)
        {
            form.Render(few, Now); Assert.False(form.ExpandedLayout.Overflow);
            Assert.Equal(new[] { 1, 1 }, form.ExpandedAllowanceCounts);
            Assert.DoesNotContain("Open Usage for all limits", form.AccessibilityObject.GetChild(0)!.Name!);
        }
    });

    [Fact]
    public Task ExpandedWidthAlsoFitsANarrowWorkArea() => Sta(() =>
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        using var form = new MonitorForm(["Codex", "Claude"], SystemIcons.Application, () => area) { MotionAllowed = () => false };
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        area = new(area.Location, new Size(S(260), S(200)));
        form.Render(DenseStates(), Now); form.ShowMonitor(area.Location); form.SetExpanded(true, false);
        Assert.True(area.Contains(form.Bounds)); Assert.True(form.ExpandedLayout.Overflow);
        using var image = form.CreatePreviewBitmap(); Assert.Equal(form.ClientSize, image.Size);
    });

    private static ProviderState[] DenseStates() => new[] { "Codex", "Claude" }.Select(name =>
    {
        var windows = new[] { new UsageWindow(name == "Codex" ? "codex/primary" : "five_hour", "fixture", 71, Now.AddHours(3), 300) }
            .Concat(Enumerable.Range(1, 20).Select(index => new UsageWindow("extra:" + index, "fixture", index,
                Now.AddHours(index), 60, UsageScope.Additional, "Extra " + index))).ToArray();
        return new ProviderState(name, ProviderStatus.Ready, new(windows, Now.AddMinutes(-3), "synthetic overflow fixture"));
    }).ToArray();
    private static void Invoke(MonitorForm form, string method, EventArgs argument) =>
        typeof(MonitorForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [argument]);
    private static bool Key(MonitorForm form, Keys key)
    {
        object[] arguments = [new Message(), key];
        return (bool)typeof(MonitorForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, arguments)!;
    }
    private static async Task Sta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); done.SetResult(); } catch (Exception error) { done.SetException(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }
}
