using System.Reflection;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class MonitorInteractionTests
{
    [Theory]
    [InlineData(96, 2, 2, false)]
    [InlineData(96, 3, 4, true)]
    [InlineData(144, 5, 5, false)]
    [InlineData(144, 6, 6, true)]
    [InlineData(192, 6, 6, false)]
    [InlineData(192, 6, 8, true)]
    public void DragUsesEuclideanFiveLogicalPixelThresholdAndLatches(int dpi, int x, int y, bool moved)
    {
        var gesture = new MonitorDragGesture(new(100, -200), dpi);
        gesture.Move(new(100 + x, -200 + y)); Assert.Equal(moved, gesture.IsDragging);
        gesture.Move(new(100, -200)); Assert.Equal(moved, gesture.IsDragging);
    }

    [Fact]
    public Task PressCancelsQueuedHoverAndReturningDragDoesNotOpenUsage() => Sta(() =>
    {
        var pointer = new Point(100, 100);
        using var form = new MonitorForm(["Codex"], SystemIcons.Application, pointerPosition: () => pointer) { MotionAllowed = () => false };
        form.ShowMonitor(new(80, 80));
        var opens = 0; var commits = 0; form.OpenRequested += () => opens++; form.PositionCommitted += () => commits++;
        Invoke(form, "OnMouseEnter", EventArgs.Empty);
        Assert.True(Field<System.Windows.Forms.Timer>(form, "hoverDelay").Enabled);
        Mouse(form, "OnMouseDown");
        Assert.False(Field<System.Windows.Forms.Timer>(form, "hoverDelay").Enabled);
        Invoke(form, "OnMouseEnter", EventArgs.Empty);
        Invoke(form, "ApplyPendingHover");
        Assert.False(form.Expanded); Assert.False(Field<System.Windows.Forms.Timer>(form, "hoverDelay").Enabled);
        pointer = new(200, 100); Mouse(form, "OnMouseMove");
        Invoke(form, "ApplyPendingHover"); Assert.False(form.Expanded);
        pointer = new(100, 100); Mouse(form, "OnMouseMove"); Mouse(form, "OnMouseUp");
        Assert.Equal(0, opens); Assert.Equal(1, commits); Assert.False(form.Capture);
        Mouse(form, "OnMouseDown"); Mouse(form, "OnMouseUp"); Assert.Equal(1, opens);
    });

    [Fact]
    public Task LosingCaptureAfterPressDoesNotCommitOrOpenAndAfterDragCommitsOnce() => Sta(() =>
    {
        var pointer = new Point(100, 100);
        using var form = new MonitorForm(["Codex"], SystemIcons.Application, pointerPosition: () => pointer) { MotionAllowed = () => false };
        form.ShowMonitor(new(80, 80));
        var opens = 0; var commits = 0; form.OpenRequested += () => opens++; form.PositionCommitted += () => commits++;
        Mouse(form, "OnMouseDown"); form.Capture = false; Assert.Equal(0, commits);
        Mouse(form, "OnMouseDown"); pointer = new(200, 100); Mouse(form, "OnMouseMove");
        form.Capture = false; Mouse(form, "OnMouseUp"); Assert.Equal(1, commits); Assert.Equal(0, opens);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ExpandingAtWorkAreaEdgesNeverMovesTheRestingAnchor(bool rightEdge) => Sta(() =>
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        using var form = new MonitorForm(["Codex", "Claude"], SystemIcons.Application, () => area) { MotionAllowed = () => false };
        form.Render(States(), Now);
        var start = new Point(rightEdge ? area.Right - form.Width : area.Left, area.Bottom - form.Height);
        form.ShowMonitor(start); var anchor = form.SavedPosition;
        for (var index = 0; index < 10; index++)
        {
            form.SetExpanded(true, false); Assert.True(area.Contains(form.Bounds));
            Assert.Equal(start, form.RestingLocation); Assert.Equal(anchor, form.SavedPosition);
            form.SetExpanded(false, false); Assert.Equal(start, form.Location);
        }
    });

    [Fact]
    public Task WorkAreaChangeAndProviderCountPreserveAnchorWithoutPersistingClampedExpandedBounds() => Sta(() =>
    {
        var screen = Screen.PrimaryScreen!;
        var area = new Rectangle(screen.WorkingArea.Location, new Size(1000, 700));
        using var form = new MonitorForm(["Codex", "Claude"], SystemIcons.Application, () => area) { MotionAllowed = () => false };
        form.Render(States(), Now); form.ShowMonitor(new(area.Left + 700, area.Top + 600));
        var anchor = form.SavedPosition;
        form.SetExpanded(true, false);
        area = new(area.Location, new Size(650, 350)); form.KeepOnScreen();
        Assert.Equal(anchor, form.SavedPosition); Assert.True(area.Contains(form.Bounds));
        form.SetProviders(["Codex"]); form.Render(States(), Now); form.SetExpanded(false, false);
        var expected = MonitorPosition.Restore(anchor, anchor.Display, area, form.Size, form.DeviceDpi);
        Assert.Equal(expected, form.Location); Assert.Equal(anchor, form.SavedPosition);
        area = new(area.Location, new Size(1000, 700)); form.KeepOnScreen();
        Assert.Equal(MonitorPosition.Restore(anchor, anchor.Display, area, form.Size, form.DeviceDpi), form.Location);
    });

    [Fact]
    public Task ExpandedCalendarLabelStaysQuietWhileAccessibleAgeChangesWithoutRepainting() => Sta(() =>
    {
        using var form = new MonitorForm(["Codex", "Claude"], SystemIcons.Application) { MotionAllowed = () => false };
        var at = new DateTimeOffset(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Local));
        var observed = at.AddMinutes(-3).AddSeconds(-59);
        var states = new[] { "Codex", "Claude" }.Select(name => new ProviderState(name, ProviderStatus.Ready,
            new UsageSnapshot([new(name == "Codex" ? "codex/primary" : "five_hour", "fixture", 50, at.AddHours(2), 300)], observed, "synthetic"))).ToArray();
        form.Render(states, at); form.SetExpanded(true, false);
        Assert.Equal(new[] { "Codex", "Claude Code" }, form.ExpandedProviderNames);
        Assert.Equal(new[] { "Today", "Today" }, form.ExpandedObservations);
        Assert.Contains("Updated 3m ago", form.AccessibilityObject.GetChild(0)!.Name);
        using var before = form.CreatePreviewBitmap(); var layout = form.ExpandedLayout;
        int S(float value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        var ink = Palette.Foreground; var stale = Palette.Warning;
        Assert.True(ContainsColor(before, ink, S(layout.NameY), S(layout.NameY + 18)));
        Assert.True(ContainsColor(before, stale, S(layout.ObservationY), S(layout.ObservationY + 19)));
        var version = form.RenderVersion;
        form.Render(states, at.AddSeconds(1)); Assert.Equal(version, form.RenderVersion);
        Assert.Equal(new[] { "Today", "Today" }, form.ExpandedObservations);
        Assert.Contains("Updated 4m ago", form.AccessibilityObject.GetChild(0)!.Name);
        Assert.All(states, state => Assert.Equal(observed, state.Snapshot!.ObservedAt));
        form.Render(states, at.AddDays(1)); Assert.True(form.RenderVersion > version);
        Assert.Equal(new[] { "Yesterday", "Yesterday" }, form.ExpandedObservations);
    });

    private static bool ContainsColor(Bitmap bitmap, Color expected, int top, int bottom)
    {
        for (var y = Math.Max(0, top); y < Math.Min(bottom, bitmap.Height); y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 240 && Math.Abs(pixel.R - expected.R) < 8 && Math.Abs(pixel.G - expected.G) < 8 && Math.Abs(pixel.B - expected.B) < 8) return true;
            }
        return false;
    }
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
    private static ProviderState[] States() => new[] { "Codex", "Claude" }.Select(name => new ProviderState(name, ProviderStatus.Ready,
        new UsageSnapshot([new(name == "Codex" ? "codex/primary" : "five_hour", "fixture", 50, Now.AddHours(2), 300)], Now.AddMinutes(-3), "synthetic"))).ToArray();
    private static T Field<T>(MonitorForm form, string name) => (T)typeof(MonitorForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Invoke(MonitorForm form, string method, params object[] arguments) => typeof(MonitorForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, arguments);
    private static void Mouse(MonitorForm form, string method) => Invoke(form, method, new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0));
    private static async Task Sta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); done.SetResult(); } catch (Exception error) { done.SetException(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }
}
