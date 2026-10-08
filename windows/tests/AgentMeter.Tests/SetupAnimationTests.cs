using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class SetupAnimationTests
{
    [Theory]
    [InlineData("CodexSetup")]
    [InlineData("ClaudeSetup")]
    public void BundledIllustrationsMatchMacPacingAndHaveStaticPosters(string asset)
    {
        using var gifStream = Asset(asset + ".gif");
        using var gif = Image.FromStream(gifStream);
        using var pngStream = Asset(asset + ".png");
        using var poster = Image.FromStream(pngStream);
        Assert.Equal(100, gif.GetFrameCount(FrameDimension.Time));
        Assert.Equal(new Size(1280, 720), gif.Size);
        Assert.Equal(gif.Size, poster.Size);
        var delays = gif.GetPropertyItem(0x5100)!.Value!;
        Assert.Equal(400, delays.Length);
        Assert.All(Enumerable.Range(0, 100), index => Assert.Equal(10, BitConverter.ToInt32(delays, index * 4)));
        Assert.Equal(0, BitConverter.ToUInt16(gif.GetPropertyItem(0x5101)!.Value!, 0));
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude Code")]
    public void AnimationRunsOnlyWhileVisibleAndStopsForReducedMotion(string provider) => Sta(() =>
    {
        var motion = false;
        using var host = new Form { ClientSize = new Size(520, 292) };
        using var view = new SetupAnimation(provider, () => motion) { Dock = DockStyle.Fill };
        host.Controls.Add(view);
        Assert.False(view.IsAnimating);
        host.Show();
        Assert.True(view.ShowingPoster); Assert.False(view.IsAnimating);
        var poster = Capture(view);
        motion = true; view.SynchronizeMotionPreference();
        Assert.False(view.ShowingPoster); Assert.True(view.IsAnimating);
        for (var index = 0; index < 65; index++) view.AdvanceFrame();
        Assert.Equal(65, view.FrameIndex);
        Assert.NotEqual(poster, Capture(view));
        motion = false; view.SynchronizeMotionPreference();
        Assert.True(view.ShowingPoster); Assert.False(view.IsAnimating);
        Assert.Equal(poster, Capture(view));
        view.AdvanceFrame(); Assert.Equal(65, view.FrameIndex);
        motion = true; view.SynchronizeMotionPreference();
        host.Hide(); Assert.False(view.IsAnimating);
        view.AdvanceFrame(); Assert.Equal(65, view.FrameIndex);
        host.Show(); Assert.True(view.IsAnimating);
        for (var index = 0; index < 35; index++) view.AdvanceFrame();
        Assert.Equal(0, view.FrameIndex);
        host.WindowState = FormWindowState.Minimized;
        Assert.False(view.IsAnimating); view.AdvanceFrame(); Assert.Equal(0, view.FrameIndex);
        host.WindowState = FormWindowState.Normal; Assert.True(view.IsAnimating);
        Assert.Equal(AccessibleRole.Graphic, view.AccessibilityObject.Role);
        Assert.Contains(provider, view.AccessibilityObject.Name);
        Assert.Contains("No commands run", view.AccessibilityObject.Description);
        Assert.False(view.TabStop); Assert.Empty(view.Controls.Cast<Control>());
        view.Dispose(); Assert.False(view.IsAnimating);
    });

    [Fact]
    public void NestedGuideStopsWhenAnAncestorIsHiddenAndRestartsWhenShown() => Sta(() =>
    {
        using var host = new Form { ClientSize = new Size(520, 292) };
        using var outer = new Panel { Dock = DockStyle.Fill };
        using var inner = new Panel { Dock = DockStyle.Fill };
        using var view = new SetupAnimation("Codex", () => true) { Dock = DockStyle.Fill };
        inner.Controls.Add(view); outer.Controls.Add(inner); host.Controls.Add(outer); host.Show();
        Assert.True(view.IsAnimating);
        outer.Hide(); Assert.False(view.IsAnimating);
        var index = view.FrameIndex; view.AdvanceFrame(); Assert.Equal(index, view.FrameIndex);
        outer.Show(); Assert.True(view.IsAnimating);
        host.Hide(); Assert.False(view.IsAnimating);
        host.Show(); Assert.True(view.IsAnimating);
    });

    private static byte[] Capture(Control view)
    {
        using var bitmap = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(bitmap, view.ClientRectangle);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    private static Stream Asset(string name) => typeof(SetupAnimation).Assembly.GetManifestResourceStream("AgentMeter.Assets.SetupGuides." + name)!;
    private static void Sta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10))); failure?.Throw();
    }
}
