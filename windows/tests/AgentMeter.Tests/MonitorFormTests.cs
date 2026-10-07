using System.Reflection;
using System.Runtime.InteropServices;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class MonitorFormTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 1, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(96, 202, 34)]
    [InlineData(120, 252, 42)]
    [InlineData(144, 303, 51)]
    [InlineData(168, 354, 60)]
    [InlineData(192, 404, 68)]
    public void OverlayDimensionsScaleWithCommonWindowsDpi(int dpi, int width, int height) =>
        Assert.Equal(new Size(width, height), MonitorForm.SizeForDpi(dpi));

    [Fact]
    public Task CodexArtworkPreservesTransparentMarginsAndClaudeKeepsMascotColor() => RunSta(() =>
    {
        foreach (var provider in new[] { "Codex", "Claude" })
        {
            using var bitmap = new Bitmap(32, 32); using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent); ProviderMark.Draw(graphics, provider, new RectangleF(0, 0, 32, 32), Color.Black);
            Assert.Equal(0, bitmap.GetPixel(0, 0).A);
            var pixels = Enumerable.Range(0, 32).SelectMany(x => Enumerable.Range(0, 32).Select(y => bitmap.GetPixel(x, y))).Where(p => p.A > 0).ToArray();
            Assert.InRange(pixels.Length, 50, 800);
            if (provider == "Claude" && !SystemInformation.HighContrast) Assert.Contains(pixels, p => p.R > p.G && p.G > p.B);
        }
    });

    [Fact]
    public Task SelectedLiveAllowancesAppearWithoutBonusOrSessionSubstitution() => RunSta(() =>
    {
        using var form = NewForm();
        form.Render(States(), Now);
        Assert.Equal(["29%", "100%"], form.RowValues);
        Assert.Contains("29% remaining", form.AccessibilityObject.GetChild(0)!.Name);
        Assert.Contains("5-hour limit", form.AccessibilityObject.GetChild(1)!.Name);
        Assert.Contains("Weekly", form.AccessibilityObject.GetChild(1)!.Name);
        Assert.Equal(2, form.AccessibilityObject.GetChildCount());
        Assert.Equal(AccessibleRole.StaticText, form.AccessibilityObject.GetChild(1)!.Role);
    });

    [Fact]
    public Task TranslucencyAffectsBackgroundWhileForegroundAndProgressRemainOpaque() => RunSta(() =>
    {
        using var form = NewForm(); form.Render(States(), Now); form.SetExpanded(true, false);
        using var image = form.CreatePreviewBitmap();
        int S(int n) => (int)Math.Round(n * form.DeviceDpi / 96d);
        Assert.Equal(MonitorForm.TransparencyAllowed ? 242 : 255, image.GetPixel(S(8), S(50)).A);
        Assert.Equal(0, image.GetPixel(0, 0).A);
        Assert.Equal(255, image.GetPixel(S(20), S(68)).A);
        Assert.Equal(MonitorForm.Accent("Codex").ToArgb(), image.GetPixel(S(20), S(68)).ToArgb());
        Assert.True(form.Expanded);
        form.SetExpanded(false, false);
        Assert.Equal(MonitorForm.SizeForDpi(form.DeviceDpi), form.ClientSize);
    });

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 3)]
    public Task ClaudeCompactAndExpandedSurfacesHideInternalWindows(bool fiveHour, bool weekly, int unknownCount) => RunSta(() =>
    {
        using var form = NewForm();
        var states = States();
        var windows = new List<UsageWindow>();
        if (fiveHour) windows.Add(new("five_hour", "raw_primary_label", 20, Now.AddHours(3)));
        if (weekly) windows.Add(new("seven_day", "raw_weekly_label", 30, Now.AddDays(2)));
        var unknown = new[] { "iguana_necktie", "future_internal", "model:five_hour" };
        windows.AddRange(unknown.Take(unknownCount).Select(id => new UsageWindow(id, id, 1, null)));
        states[1] = new("Claude", ProviderStatus.Ready, new(windows, Now, "fixture"));
        foreach (var expanded in new[] { false, true })
        {
            form.SetExpanded(expanded, false);
            form.Render(states, Now);
            Assert.Equal("29%", form.RowValues[0]); // Existing Codex fixture presentation is unchanged.
            Assert.Equal(fiveHour ? "80%" : weekly ? "70%" : "—", form.RowValues[1]);
            var text = form.AccessibilityObject.GetChild(1)!.Name!;
            foreach (var raw in unknown.Append("raw_primary_label").Append("raw_weekly_label"))
                Assert.DoesNotContain(raw, text);
            if (fiveHour) Assert.Contains("5-hour limit · 80% remaining", text);
            else Assert.DoesNotContain("5-hour ·", text);
            if (weekly) Assert.Contains("Weekly limit · 70% remaining", text);
            else Assert.DoesNotContain("Weekly ·", text);
            using var preview = form.CreatePreviewBitmap();
            Assert.Equal(form.ClientSize.Width, preview.Width);
        }
        Assert.Equal((fiveHour ? 1 : 0) + (weekly ? 1 : 0) + unknownCount, states[1].Snapshot!.Windows.Count);
    });

    [Fact]
    public Task StalePercentagesNeverLookLikeLiveMonitorNumbers() => RunSta(() =>
    {
        using var form = NewForm();
        form.Render(States(), Now.AddMinutes(3));
        Assert.Equal(["29%", "100%"], form.RowValues);
        Assert.Contains("Stale", form.AccessibilityObject.GetChild(0)!.Name);
        Assert.Contains("29% remaining", form.AccessibilityObject.GetChild(0)!.Name);
        using var image = form.CreatePreviewBitmap();
        Assert.Contains("Stale", form.AccessibilityObject.GetChild(0)!.Name!);
    });

    [Fact]
    public Task UnknownAndUnavailableRemainUnknownAndDoNotSelectGenerousBonusWindow() => RunSta(() =>
    {
        using var form = NewForm();
        var states = States();
        var coreUnknown = new UsageWindow("codex/primary", "Codex · 5 hours", null, Now.AddHours(4), 300);
        states[0] = states[0] with { Snapshot = states[0].Snapshot! with { Windows = [coreUnknown, new("spark/primary", "Spark · 5 hours", 0, Now.AddHours(4), 300, UsageScope.Model, "Spark")] } };
        states[1] = new ProviderState("Claude", ProviderStatus.Unavailable, Failure: FailureKind.LoggedOut);
        form.Render(states, Now);
        Assert.Equal(["—", "—"], form.RowValues);
        Assert.Contains("Not signed in", form.AccessibilityObject.GetChild(1)!.Name);
    });

    [Fact]
    public Task LoadingMissingResetAndPartialProviderStateAreSafe() => RunSta(() =>
    {
        using var form = NewForm();
        form.Render([new ProviderState("Codex", ProviderStatus.Loading)], Now);
        Assert.Equal(["…", "—"], form.RowValues);
        form.Render([new ProviderState("Codex", ProviderStatus.Ready,
            new UsageSnapshot([new("codex/primary", "5 hours", 40, null, 300)], Now, "fixture"))], Now);
        Assert.Equal(["60%", "—"], form.RowValues);
        Assert.Contains("Reset unavailable", form.AccessibilityObject.GetChild(0)!.Name);
    });

    [Fact]
    public Task IdenticalPixelsDoNotRepaintWhenOnlyUpdateAgeOrResetCountdownChanges() => RunSta(() =>
    {
        using var form = NewForm();
        var states = States();
        form.Render(states, Now);
        var before = form.RenderVersion;
        form.Render(states, Now.AddSeconds(45));
        Assert.Equal(before, form.RenderVersion);
        states[0] = states[0] with { Snapshot = states[0].Snapshot! with { Windows = [new("codex/primary", "5 hours", 72, Now.AddHours(5), 300)] } };
        form.Render(states, Now.AddSeconds(46));
        Assert.Equal(before + 1, form.RenderVersion);
        Assert.Equal("28%", form.RowValues[0]);
    });

    [Fact]
    public Task RealNativeLayeredWindowShowsTopmostWithoutTaskbarAndCanHideRepeatedly() => RunSta(() =>
    {
        using var form = NewForm();
        form.Render(States(), Now);
        var area = Screen.PrimaryScreen!.WorkingArea;
        for (var i = 0; i < 3; i++)
        {
            form.ShowMonitor(new Point(area.Left + 24, area.Top + 24));
            Application.DoEvents();
            Assert.True(form.Visible); Assert.True(form.TopMost); Assert.False(form.ShowInTaskbar);
            Assert.True(form.UsesPerPixelTransparency, "The supported native alpha renderer must succeed on this machine.");
            Invoke(form, "OnDeactivate", EventArgs.Empty);
            Assert.True(form.Visible);
            form.HideMonitor();
            Assert.False(form.Visible); Assert.False(form.TopMost); Assert.False(form.IsDisposed);
        }
    });

    [Fact]
    public Task UnpinOwnerSeesVisiblePositionBeforeTheWindowHides() => RunSta(() =>
    {
        using var form = NewForm();
        form.ShowMonitor(Screen.PrimaryScreen!.WorkingArea.Location + new Size(24, 24));
        var calls = 0;
        form.UnpinRequested += () =>
        {
            Assert.True(form.Visible);
            Assert.True(form.TopMost);
            calls++; form.HideMonitor();
        };
        Assert.True(Key(form, Keys.Escape));
        Assert.Equal(1, calls); Assert.False(form.Visible); Assert.False(form.IsDisposed);
    });

    [Fact]
    public Task ContextMenuAndKeyboardInvokeActualActionWiring() => RunSta(() =>
    {
        using var form = NewForm();
        var opens = 0; var refreshes = 0; var exits = 0; var unpins = 0; var resets = 0;
        form.OpenRequested += () => opens++;
        form.RefreshRequested += () => refreshes++;
        form.ExitRequested += () => exits++;
        form.UnpinRequested += () => unpins++;
        form.ResetPositionRequested += () => resets++;
        Assert.True(Key(form, Keys.Enter));
        Assert.True(Key(form, Keys.Control | Keys.R));
        foreach (var item in form.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>()) item.PerformClick();
        Assert.Equal(2, opens); Assert.Equal(2, refreshes); Assert.Equal(1, exits); Assert.Equal(1, unpins); Assert.Equal(1, resets);
    });

    [Fact]
    public Task DragReleaseClampsPositionAndCommitsOnlyOnce() => RunSta(() =>
    {
        var pointer = new Point(50, 50);
        using var form = new MonitorForm(["Codex", "Claude"], SystemIcons.Application, pointerPosition: () => pointer);
        form.ShowMonitor(Screen.PrimaryScreen!.WorkingArea.Location + new Size(24, 24));
        var commits = 0;
        form.PositionCommitted += () => commits++;
        Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 50, 18, 0));
        Assert.True(form.Capture);
        // A synthetic pointer drives the native event handlers without desktop input.
        var area = Screen.FromControl(form).WorkingArea;
        pointer = new Point(area.Right + 100, area.Bottom + 100);
        Invoke(form, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 50, 18, 0));
        Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 50, 18, 0));
        Assert.False(form.Capture); Assert.Equal(1, commits);
        Assert.True(Screen.FromControl(form).WorkingArea.Contains(form.Bounds));
    });

    [Fact]
    public Task NativeRepaintsAndPreviewDisposalDoNotLeakGdiObjects() => RunSta(() =>
    {
        using var form = NewForm(); form.MotionAllowed = () => false;
        form.Render(States(), Now);
        form.ShowMonitor(Screen.PrimaryScreen!.WorkingArea.Location + new Size(24, 24));
        void Repaint(int i)
        {
            form.SetExpanded(i % 2 == 0, false);
            using var image = form.CreatePreviewBitmap();
            var states = States();
            states[0] = states[0] with { Snapshot = states[0].Snapshot! with { Windows = [new("codex/primary", "5 hours", i, Now.AddHours(5), 300)] } };
            form.Render(states, Now);
        }
        // Warm both render paths and drain finalizers from earlier UI fixtures before
        // taking this process-wide counter. Do not collect after the measured loop:
        // relying on finalizers to release our repaint resources must still fail.
        for (var i = 0; i < 10; i++) Repaint(i);
        Application.DoEvents();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var before = GetGuiResources(process.Handle, 0);
        Assert.True(before > 0, "The process GDI counter must be available.");
        for (var i = 0; i < 50; i++) Repaint(i);
        var after = GetGuiResources(process.Handle, 0);
        Assert.True(after > 0, "The process GDI counter must remain available.");
        Assert.True((long)after - before <= 5, $"Repaints retained {(long)after - before} extra GDI objects.");
    });

    [Fact]
    public Task WindowsAnimationUsesNativeBoundedTransition() => RunSta(() =>
    {
        using var animation = new MonitorAnimation();
        animation.Start(.14);
        Assert.True(animation.Native);
        Assert.InRange(animation.Progress, 0, .3);
        Thread.Sleep(60);
        Assert.InRange(animation.Progress, .05, .99);
        Thread.Sleep(100);
        Assert.Equal(1, animation.Progress);
        animation.Stop();
        animation.Start(.14);
        Assert.True(animation.Native);
        Assert.InRange(animation.Progress, 0, .3);
    });

    [Fact]
    public Task BothColumnsRemainVisibleAtCompactAndExpandedSizes() => RunSta(() =>
    {
        using var form = NewForm(); form.Render(States(), Now);
        foreach (var expanded in new[] { false, true })
        {
            form.SetExpanded(expanded, false);
            using var image = form.CreatePreviewBitmap();
            // Distinct provider accent pixels prove that neither column is clipped.
            foreach (var name in new[] { "Codex", "Claude" })
            {
                var expected = MonitorForm.Accent(name);
                var found = false;
                for (var x = 0; x < image.Width && !found; x++)
                    for (var y = 0; y < image.Height && !found; y++)
                    {
                        var pixel = image.GetPixel(x, y);
                        found = pixel.A > 240 && Math.Abs(pixel.R - expected.R) < 8 && Math.Abs(pixel.G - expected.G) < 8 && Math.Abs(pixel.B - expected.B) < 8;
                    }
                Assert.True(found, name + " must be painted inside the window");
            }
            var output = Environment.GetEnvironmentVariable("AGENTMETER_TEST_RENDER_DIRECTORY");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                image.Save(Path.Combine(output, expanded ? "monitor-both-expanded.png" : "monitor-both-compact.png"));
            }
        }
    });

    [Fact]
    public Task SupportedModelAndAdditionalLimitsExpandForBothProvidersAndKeepAgeAccessible() => RunSta(() =>
    {
        using var form = NewForm(); var states = States();
        states[1] = states[1] with { Snapshot = new([
            new("seven_day", "raw-week", 40, Now.AddDays(2)),
            new("sonnet", "raw-model", 5, Now.AddDays(2), 10080, UsageScope.Model, "Sonnet"),
            new("extra", "raw-additional", 10, Now.AddHours(1), 60, UsageScope.Additional, "Extra usage")], Now.AddMinutes(-3), "fixture") };
        form.Render(states, Now); form.SetExpanded(true, false);
        Assert.Equal(["29%", "60%"], form.RowValues);
        Assert.Equal(MonitorForm.SizeForDpi(form.DeviceDpi, 2, true, allowanceRows: 3), form.ClientSize);
        Assert.Contains("Spark · 5-hour model allowance", form.AccessibilityObject.GetChild(0)!.Name);
        var claude = form.AccessibilityObject.GetChild(1)!.Name!;
        foreach (var expected in new[] { "Sonnet · Weekly model allowance", "Extra usage limit · 1-hour additional allowance", "Updated 3m ago", "observation older than two minutes" }) Assert.Contains(expected, claude);
        foreach (var denied in new[] { "raw-model", "raw-additional", "raw-week" }) Assert.DoesNotContain(denied, claude);
        using var image = form.CreatePreviewBitmap(); Assert.Equal(form.ClientSize.Height, image.Height);
    });

    [Fact]
    public Task DisabledProvidersDisappearAndBothOffHidesExistingMonitor() => RunSta(() =>
    {
        using var form = NewForm(); form.MotionAllowed = () => false; var states = States();
        form.Render(states, Now); form.ShowMonitor(new(30, 30));
        form.Render([states[0] with { Enabled = false }, states[1]], Now);
        Assert.Equal(["Claude"], form.ProviderNames); Assert.Single(form.RowValues); Assert.Equal(1, form.AccessibilityObject.GetChildCount());
        form.Render(states.Select(s => s with { Enabled = false }).ToArray(), Now);
        Assert.Empty(form.RowValues); Assert.False(form.Visible);
        form.Render(states, Now); Assert.Equal(2, form.RowValues.Count);
    });

    [Fact]
    public Task ReducedMotionChangesAreImmediateAndHaveNoAnimationTimer() => RunSta(() =>
    {
        using var form = NewForm(); form.MotionAllowed = () => false;
        form.Render(States(), Now);
        form.ShowMonitor(new(30, 30)); Assert.False(form.IsAnimating);
        form.SetExpanded(true); Assert.False(form.IsAnimating);
        Assert.Equal(form.ExpandedSize, form.ClientSize);
        form.HideMonitor(true); Assert.False(form.Visible); Assert.False(form.IsAnimating);
    });

    private static MonitorForm NewForm() => new(["Codex", "Claude"], SystemIcons.Application);
    private static ProviderState[] States() =>
    [
        new("Codex", ProviderStatus.Ready, new UsageSnapshot([
            new("codex/primary", "Codex · 5 hours", 71, Now.AddHours(5), 300),
            new("codex/secondary", "Codex · 7 days", 15, Now.AddDays(4), 10080),
            new("spark/primary", "Spark · 5 hours", 0, Now.AddHours(5), 300, UsageScope.Model, "Spark")
        ], Now, "fixture")),
        new("Claude", ProviderStatus.Ready, new UsageSnapshot([
            new("five_hour", "5 hours", 0, Now.AddHours(3)),
            new("seven_day", "7 days", 8, Now.AddDays(2))
        ], Now, "fixture"))
    ];
    private static void Invoke(MonitorForm form, string method, EventArgs argument) =>
        typeof(MonitorForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [argument]);
    private static bool Key(MonitorForm form, Keys key)
    {
        object[] arguments = [new Message(), key];
        return (bool)typeof(MonitorForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, arguments)!;
    }
    private static async Task RunSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception exception) { done.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
