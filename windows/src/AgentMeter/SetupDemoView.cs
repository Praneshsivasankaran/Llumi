using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AgentMeter;

// Bundled illustrations only. Playback never reads a provider or runs a command.
internal sealed class SetupDemoView : Control
{
    private readonly Stream animationStream;
    private readonly Stream posterStream;
    private readonly Image animation;
    private readonly Image poster;
    private readonly int[] delays;
    private readonly System.Windows.Forms.Timer timer = new();
    private Form? owner;
    private bool ownerActive;
    private bool released;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<bool> MotionAllowed { get; set; } = () => MonitorAnimation.MotionAllowed;
    internal bool IsAnimating => timer.Enabled;
    internal int FrameIndex { get; private set; }
    internal int FrameCount => delays.Length;
    internal int DurationMilliseconds => delays.Sum();
    internal bool ShowingPoster => !IsAnimating;

    internal SetupDemoView(string provider)
    {
        var isCodex = provider == "Codex";
        var name = isCodex ? "CodexSetup" : "ClaudeSetup";
        Name = "setupDemo"; TabStop = false; AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = (isCodex ? "Codex" : "Claude Code") + " setup guide";
        AccessibleDescription = "Illustration. " + (isCodex ? "Requires Node.js and npm. " : "") + "Copy " + ProviderSetup.InstallCommand(provider) +
            ", paste it into PowerShell and press Enter. After installation, open a new PowerShell window and run " +
            ProviderSetup.SignInCommand(provider) + ". Finish signing in through the browser, then return to Llumi and continue to Check Setup. Installation may take longer than shown.";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        animationStream = Resource(name + ".gif"); posterStream = Resource(name + ".png");
        animation = Image.FromStream(animationStream); poster = Image.FromStream(posterStream);
        var frameCount = animation.GetFrameCount(FrameDimension.Time);
        var durationBytes = animation.GetPropertyItem(0x5100)?.Value ?? throw new InvalidDataException("Setup guide timing is missing.");
        if (frameCount < 2 || frameCount > 100 || durationBytes.Length < frameCount * 4)
            throw new InvalidDataException("Setup guide timing is invalid.");
        delays = Enumerable.Range(0, frameCount).Select(index => Math.Clamp(BitConverter.ToInt32(durationBytes, index * 4) * 10, 100, 5000)).ToArray();
        timer.Tick += (_, _) => AdvanceFrame();
    }

    private static Stream Resource(string name) => typeof(SetupDemoView).Assembly.GetManifestResourceStream("AgentMeter.Assets.SetupDemos." + name)
        ?? throw new InvalidOperationException("A bundled setup guide is missing.");

    internal void RefreshPlayback()
    {
        if (released) return;
        var play = IsHandleCreated && Visible && Enabled && owner is { Visible: true } &&
            owner.WindowState != FormWindowState.Minimized && ownerActive && MotionAllowed() && InViewport();
        if (play == timer.Enabled) return;
        timer.Stop(); FrameIndex = 0;
        animation.SelectActiveFrame(FrameDimension.Time, 0);
        if (play) { timer.Interval = delays[0]; timer.Start(); }
        Invalidate();
    }

    private bool InViewport()
    {
        var visible = RectangleToScreen(ClientRectangle);
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            visible.Intersect(ancestor.RectangleToScreen(ancestor.ClientRectangle));
            if (visible.Width <= 0 || visible.Height <= 0) return false;
        }
        return visible.Width > 0 && visible.Height > 0;
    }

    internal void AdvanceFrame()
    {
        RefreshPlayback();
        if (released || !timer.Enabled) return;
        FrameIndex = (FrameIndex + 1) % delays.Length;
        animation.SelectActiveFrame(FrameDimension.Time, FrameIndex);
        timer.Interval = delays[FrameIndex]; Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (released) return;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(ShowingPoster ? poster : animation, ClientRectangle);
    }

    private void OwnerChanged(object? sender, EventArgs e) => RefreshPlayback();
    private void OwnerActivated(object? sender, EventArgs e) { ownerActive = true; RefreshPlayback(); }
    private void OwnerDeactivated(object? sender, EventArgs e)
    {
        // Track the events: ActiveForm can lag during activation/deactivation.
        ownerActive = false;
        timer.Stop(); FrameIndex = 0; Invalidate();
    }
    private void DetachOwner()
    {
        if (owner is null) return;
        owner.Activated -= OwnerActivated; owner.Deactivate -= OwnerDeactivated;
        owner.Resize -= OwnerChanged; owner.VisibleChanged -= OwnerChanged;
        owner = null; ownerActive = false;
    }
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e); DetachOwner(); owner = FindForm();
        if (owner is not null)
        {
            ownerActive = Form.ActiveForm == owner;
            owner.Activated += OwnerActivated; owner.Deactivate += OwnerDeactivated;
            owner.Resize += OwnerChanged; owner.VisibleChanged += OwnerChanged;
        }
        RefreshPlayback();
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); RefreshPlayback(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); RefreshPlayback(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RefreshPlayback(); }
    protected override void OnHandleDestroyed(EventArgs e) { if (!released) timer.Stop(); base.OnHandleDestroyed(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !released)
        {
            released = true; timer.Stop(); timer.Dispose(); DetachOwner();
            animation.Dispose(); poster.Dispose(); animationStream.Dispose(); posterStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
