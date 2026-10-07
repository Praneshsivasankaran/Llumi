using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class SetupDemoTests
{
    [Theory]
    [InlineData("Codex", "CodexSetup")]
    [InlineData("Claude", "ClaudeSetup")]
    public Task BundledGuidesDecodeEveryFrameAndIncludeATenSecondLoopAndStaticOverview(string provider, string resource) => Sta(() =>
    {
        var assembly = typeof(SetupDemoView).Assembly;
        using var gifStream = assembly.GetManifestResourceStream("AgentMeter.Assets.SetupDemos." + resource + ".gif");
        using var pngStream = assembly.GetManifestResourceStream("AgentMeter.Assets.SetupDemos." + resource + ".png");
        Assert.NotNull(gifStream); Assert.NotNull(pngStream);
        using var gif = Image.FromStream(gifStream); using var png = Image.FromStream(pngStream);
        Assert.Equal(new Size(1280, 480), gif.Size); Assert.Equal(gif.Size, png.Size);
        Assert.Equal(ImageFormat.Gif.Guid, gif.RawFormat.Guid); Assert.Equal(ImageFormat.Png.Guid, png.RawFormat.Guid);
        var frames = gif.GetFrameCount(FrameDimension.Time); Assert.Equal(20, frames);
        var delays = Assert.IsType<byte[]>(gif.GetPropertyItem(0x5100)!.Value);
        Assert.Equal(frames * sizeof(int), delays.Length);
        Assert.All(Enumerable.Range(0, frames), index => Assert.Equal(50, BitConverter.ToInt32(delays, index * sizeof(int))));
        var loops = Assert.IsType<byte[]>(gif.GetPropertyItem(0x5101)!.Value); Assert.Equal(0, BitConverter.ToUInt16(loops));
        var rendered = new HashSet<string>();
        using var target = new Bitmap(gif.Width, gif.Height); using var graphics = Graphics.FromImage(target);
        for (var frame = 0; frame < frames; frame++)
        {
            gif.SelectActiveFrame(FrameDimension.Time, frame); graphics.DrawImageUnscaled(gif, 0, 0);
            rendered.Add(Fingerprint(target));
        }
        Assert.True(rendered.Count >= 4, "The demonstration should show distinct instruction stages.");
        graphics.DrawImageUnscaled(png, 0, 0);
        Assert.DoesNotContain(Fingerprint(target), rendered);
        using var demo = new SetupDemoView(provider);
        Assert.Equal(frames, demo.FrameCount); Assert.Equal(10_000, demo.DurationMilliseconds);
        Assert.False(demo.IsAnimating); Assert.True(demo.ShowingPoster);
    });

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public Task PlaybackAdvancesAndLoopsOnlyWhenMotionIsAllowedInTheActiveWindow(string provider) => Sta(() =>
    {
        var motion = false;
        using var form = new Form { ClientSize = new Size(580, 270) };
        using var demo = new SetupDemoView(provider) { Bounds = new Rectangle(20, 20, 520, 195), MotionAllowed = () => motion };
        form.Controls.Add(demo); ShowActive(form); demo.RefreshPlayback();
        Assert.False(demo.IsAnimating); Assert.True(demo.ShowingPoster);
        var overview = Render(demo); demo.AdvanceFrame(); Assert.Equal(0, demo.FrameIndex); Assert.Equal(overview, Render(demo));
        motion = true; demo.RefreshPlayback();
        Assert.True(demo.IsAnimating); Assert.False(demo.ShowingPoster); Assert.NotEqual(overview, Render(demo));
        for (var frame = 1; frame <= demo.FrameCount; frame++)
        {
            demo.AdvanceFrame(); Assert.Equal(frame % demo.FrameCount, demo.FrameIndex);
            _ = Render(demo);
        }
        demo.AdvanceFrame(); Assert.Equal(1, demo.FrameIndex);
        motion = false; demo.RefreshPlayback();
        Assert.False(demo.IsAnimating); Assert.True(demo.ShowingPoster); Assert.Equal(0, demo.FrameIndex);
        Assert.Equal(overview, Render(demo)); demo.AdvanceFrame(); Assert.Equal(0, demo.FrameIndex);
    });

    [Fact]
    public Task PlaybackStopsWhenHiddenMinimizedInactiveOrOutsideTheViewportAndResumesAtTheStart() => Sta(() =>
    {
        using var form = new Form { ClientSize = new Size(580, 270) };
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, AutoScrollMinSize = new Size(0, 850) };
        form.Controls.Add(viewport);
        using var demo = new SetupDemoView("Codex") { Bounds = new Rectangle(20, 20, 520, 195), MotionAllowed = () => true };
        viewport.Controls.Add(demo); ShowActive(form); demo.RefreshPlayback(); Assert.True(demo.IsAnimating);
        demo.AdvanceFrame(); Assert.Equal(1, demo.FrameIndex);
        demo.Hide(); Assert.False(demo.IsAnimating); Assert.True(demo.ShowingPoster);
        demo.Show(); demo.RefreshPlayback(); Assert.True(demo.IsAnimating); Assert.Equal(0, demo.FrameIndex);
        form.Hide(); Assert.False(demo.IsAnimating);
        ShowActive(form); demo.RefreshPlayback(); Assert.True(demo.IsAnimating); Assert.Equal(0, demo.FrameIndex);
        form.WindowState = FormWindowState.Minimized; Application.DoEvents();
        Assert.False(demo.IsAnimating); Assert.True(demo.ShowingPoster);
        form.WindowState = FormWindowState.Normal; ShowActive(form); demo.RefreshPlayback(); Assert.True(demo.IsAnimating);
        using (var other = new Form { ClientSize = new Size(100, 100) })
        {
            RaiseActivation(form, false); ShowActive(other); Assert.False(demo.IsAnimating); Assert.True(demo.ShowingPoster);
            demo.AdvanceFrame(); Assert.False(demo.IsAnimating); Assert.Equal(0, demo.FrameIndex);
        }
        ShowActive(form); demo.RefreshPlayback(); Assert.True(demo.IsAnimating);
        viewport.AutoScrollPosition = new Point(0, 600); demo.AdvanceFrame();
        Assert.False(demo.IsAnimating); Assert.True(demo.ShowingPoster); Assert.Equal(0, demo.FrameIndex);
        viewport.AutoScrollPosition = Point.Empty; demo.RefreshPlayback();
        Assert.True(demo.IsAnimating); Assert.Equal(0, demo.FrameIndex);
        demo.Dispose(); Assert.True(demo.IsDisposed); Assert.False(demo.IsAnimating);
        form.Hide(); ShowActive(form); demo.RefreshPlayback(); demo.AdvanceFrame();
        Assert.False(demo.IsAnimating);
    });

    [Fact]
    public Task SetupViewportPausesAndResumesTheGuideOnProgrammaticScroll()
        => Sta(() =>
    {
        using var form = NewForm(SetupStep.Codex); ShowActive(form);
        form.MinimumSize = Size.Empty; form.ClientSize = new Size(420, 180); form.ActiveControl = null;
        var viewport = Descendants(form).OfType<FlowLayoutPanel>().Single(panel => panel.Name == "setupViewport");
        var demo = Assert.Single(Descendants(form).OfType<SetupDemoView>());
        demo.MotionAllowed = () => true; demo.RefreshPlayback(); Assert.True(demo.IsAnimating);
        demo.AdvanceFrame(); Assert.Equal(1, demo.FrameIndex);
        viewport.AutoScrollPosition = new Point(0, 10000);
        Assert.True(viewport.RectangleToClient(demo.RectangleToScreen(demo.ClientRectangle)).Bottom <= 0);
        Assert.False(demo.IsAnimating);
        viewport.AutoScrollPosition = Point.Empty;
        Assert.True(demo.IsAnimating); Assert.Equal(0, demo.FrameIndex);
    });

    [Fact]
    public Task NavigationDisposesEachGuideAndReentryCreatesANewGuideWithoutStartingProviderWork() => Sta(() =>
    {
        var flow = FlowAt(SetupStep.Codex); var reads = 0; var refreshes = 0; var saves = 0;
        using var form = new SetupForm(flow, () => { reads++; return []; }, _ => refreshes++, () => new(),
            _ => saves++, new Startup(), () => { }, () => { });
        ShowActive(form); var next = Assert.IsType<Button>(form.AcceptButton);
        var codex = Assert.Single(Descendants(form).OfType<SetupDemoView>());
        codex.MotionAllowed = () => true; codex.RefreshPlayback(); Assert.True(codex.IsAnimating);
        next.PerformClick();
        Assert.Equal(SetupStep.Claude, flow.Step); Assert.True(codex.IsDisposed); Assert.False(codex.IsAnimating);
        var claude = Assert.Single(Descendants(form).OfType<SetupDemoView>());
        Assert.Equal("Claude Code setup guide", claude.AccessibleName);
        Descendants(form).OfType<Button>().Single(button => button.Text == "Back").PerformClick();
        Assert.Equal(SetupStep.Codex, flow.Step); Assert.True(claude.IsDisposed); Assert.False(claude.IsAnimating);
        var returned = Assert.Single(Descendants(form).OfType<SetupDemoView>());
        Assert.NotSame(codex, returned); Assert.Equal(0, returned.FrameIndex);
        Assert.Equal(0, reads); Assert.Equal(0, refreshes); Assert.Equal(0, saves);
        next.PerformClick(); next.PerformClick();
        Assert.Equal(SetupStep.Verify, flow.Step); Assert.True(returned.IsDisposed);
        Assert.Empty(Descendants(form).OfType<SetupDemoView>()); Assert.Equal(1, refreshes);
    });

    [Theory]
    [InlineData(2, "Codex", "npm install -g @openai/codex", "codex login")]
    [InlineData(3, "Claude Code", "irm https://claude.ai/install.ps1 | iex", "claude auth login")]
    public Task GuideRetainsAccessibleCommandsAndReachableNavigationAtPreferredAndSmallSizes(int step, string provider, string install, string login) => Sta(() =>
    {
        using var form = NewForm((SetupStep)step); ShowActive(form); form.MinimumSize = Size.Empty;
        var demo = Assert.Single(Descendants(form).OfType<SetupDemoView>()); demo.MotionAllowed = () => false; demo.RefreshPlayback();
        Assert.Equal(AccessibleRole.Graphic, demo.AccessibilityObject.Role); Assert.Equal(provider + " setup guide", demo.AccessibilityObject.Name);
        Assert.Contains(install, demo.AccessibilityObject.Description); Assert.Contains(login, demo.AccessibilityObject.Description);
        Assert.Contains("PowerShell", demo.AccessibilityObject.Description); Assert.Contains("browser", demo.AccessibilityObject.Description);
        Assert.Contains("Illustration", demo.AccessibilityObject.Description); Assert.False(demo.TabStop);
        var boxes = Descendants(form).OfType<TextBox>().ToArray(); Assert.Equal(new[] { install, login }, boxes.Select(box => box.Text));
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        foreach (var size in new[] { new Size(S(620), S(580)), new Size(S(420), S(280)) })
        {
            form.ClientSize = size; form.PerformLayout(); Application.DoEvents();
            var viewport = Descendants(form).OfType<FlowLayoutPanel>().Single(panel => panel.Name == "setupViewport");
            Assert.False(viewport.HorizontalScroll.Visible);
            Assert.InRange(Math.Abs(demo.Height - demo.Width * 480d / 1280), 0, 1);
            foreach (var control in Descendants(form).Where(control => control.Visible && control is Button or TextBox or LinkLabel))
            {
                viewport.ScrollControlIntoView(control);
                Assert.True(viewport.ClientRectangle.Contains(viewport.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))),
                    "Unreachable guide action: " + (control.AccessibleName ?? control.Text));
                if (control.TabStop) { control.Select(); Assert.True(control.Focused); }
            }
            Assert.Equal("Continue", Assert.IsType<Button>(form.AcceptButton).Text);
        }
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public Task OtherSetupStepsDoNotLoadOrRunDemonstrations(int step) => Sta(() =>
    {
        using var form = NewForm((SetupStep)step); form.Show();
        Assert.Empty(Descendants(form).OfType<SetupDemoView>());
    });

    private static string Render(Control control)
    {
        using var bitmap = new Bitmap(control.Width, control.Height); control.DrawToBitmap(bitmap, control.ClientRectangle);
        return Fingerprint(bitmap);
    }
    private static string Fingerprint(Bitmap bitmap)
    {
        using var bytes = new MemoryStream(); bitmap.Save(bytes, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    private static void ShowActive(Form form)
    {
        form.Show(); Application.DoEvents();
        // Windows can refuse foreground activation for a background test thread.
        // Drive the actual subscribed Form event, without changing desktop focus.
        RaiseActivation(form, true);
    }
    private static void RaiseActivation(Form form, bool active) => typeof(Form)
        .GetMethod(active ? "OnActivated" : "OnDeactivate", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(form, [EventArgs.Empty]);
    private static SetupFlow FlowAt(SetupStep step)
    {
        var flow = new SetupFlow(new(Path.Combine(Path.GetTempPath(), "Llumi-demo-fixture-" + Guid.NewGuid(), "completion.json")));
        while (flow.Step != step) flow.Next(); return flow;
    }
    private static SetupForm NewForm(SetupStep step) => new(FlowAt(step), () => [], _ => { }, () => new(), _ => { }, new Startup(), () => { }, () => { });
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(Descendants(control)));
    private sealed class Startup : IStartupRegistration
    {
        public bool TryRead(out bool enabled) { enabled = false; return true; }
        public bool TrySet(bool enabled) => true;
    }
    private static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
