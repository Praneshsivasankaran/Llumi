using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace AgentMeter;

// Embedded illustrations only. This control never executes setup commands,
// consults providers, opens a browser, or reads installation/account state.
internal sealed class SetupAnimation : Control
{
    private readonly Func<bool> motionAllowed;
    private readonly Stream animatedStream;
    private readonly Stream posterStream;
    private readonly Image animation;
    private readonly Image poster;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly AncestorVisibilityObserver visibilityObserver;
    private Form? ownerWindow;
    private bool usePoster = true;
    private bool disposed;
    internal int FrameIndex { get; private set; }
    internal int FrameCount { get; }
    internal bool IsAnimating => !disposed && timer.Enabled;
    internal bool ShowingPoster => usePoster;

    internal SetupAnimation(string provider, Func<bool>? motionAllowed = null)
    {
        this.motionAllowed = motionAllowed ?? (() => MonitorAnimation.MotionAllowed);
        var isClaude = provider == "Claude" || provider == "Claude Code";
        var title = isClaude ? "Claude Code" : "Codex";
        var asset = isClaude ? "ClaudeSetup" : "CodexSetup";
        animatedStream = Asset(asset + ".gif"); posterStream = Asset(asset + ".png");
        animation = Image.FromStream(animatedStream); poster = Image.FromStream(posterStream);
        FrameCount = animation.GetFrameCount(FrameDimension.Time);
        Name = "setupIllustration"; AccessibleRole = AccessibleRole.Graphic; TabStop = false;
        AccessibleName = $"Illustrated {title} setup in PowerShell";
        AccessibleDescription = "Illustration: copy the install command into PowerShell, press Enter, then sign in using the sign-in command and your browser. " +
            (isClaude ? "Open a new PowerShell window before signing in. " : "") + "No commands run in this illustration.";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        timer.Tick += (_, _) => AdvanceFrame();
        visibilityObserver = new(this, timer.Stop);
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
    }

    private static Stream Asset(string name) => typeof(SetupAnimation).Assembly.GetManifestResourceStream("AgentMeter.Assets.SetupGuides." + name)
        ?? throw new InvalidOperationException("Bundled setup illustration is missing.");

    internal void SynchronizeMotionPreference()
    {
        if (disposed || IsDisposed) return;
        usePoster = !motionAllowed();
        if (!IsHandleCreated || !Visible || ownerWindow?.WindowState == FormWindowState.Minimized || usePoster) timer.Stop();
        else timer.Start();
        Invalidate();
    }

    internal void AdvanceFrame()
    {
        if (disposed || !IsHandleCreated || !Visible || ownerWindow?.WindowState == FormWindowState.Minimized || !motionAllowed())
        { SynchronizeMotionPreference(); return; }
        usePoster = false;
        FrameIndex = (FrameIndex + 1) % FrameCount;
        animation.SelectActiveFrame(FrameDimension.Time, FrameIndex);
        Invalidate();
    }

    private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (disposed || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(SynchronizeMotionPreference); }
        catch (InvalidOperationException) { /* The owning setup page closed during the notification. */ }
    }

    private void BindOwnerWindow()
    {
        var current = FindForm();
        if (ownerWindow == current) return;
        if (ownerWindow is not null) ownerWindow.Resize -= OwnerWindowChanged;
        ownerWindow = current;
        if (ownerWindow is not null) ownerWindow.Resize += OwnerWindowChanged;
    }
    private void OwnerWindowChanged(object? sender, EventArgs e) => SynchronizeMotionPreference();
    protected override void OnParentChanged(EventArgs e)
    { base.OnParentChanged(e); if (!disposed) { BindOwnerWindow(); SynchronizeMotionPreference(); } }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); BindOwnerWindow(); SynchronizeMotionPreference(); }
    protected override void OnHandleDestroyed(EventArgs e) { if (!disposed) timer.Stop(); base.OnHandleDestroyed(e); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); SynchronizeMotionPreference(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (disposed || Width < 2 || Height < 2) return;
        var radius = Math.Min(Width, Math.Min(Height, (int)Math.Round(14 * DeviceDpi / 96d)));
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var round = new GraphicsPath();
        round.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90);
        round.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 90);
        round.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
        round.AddArc(bounds.Left, bounds.Bottom - radius, radius, radius, 90, 90); round.CloseFigure();
        var state = e.Graphics.Save(); e.Graphics.SetClip(round);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(usePoster ? poster : animation, bounds);
        e.Graphics.Restore(state);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(220, 224, 232));
        e.Graphics.DrawPath(border, round);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
            visibilityObserver.Dispose();
            if (ownerWindow is not null) ownerWindow.Resize -= OwnerWindowChanged;
            timer.Stop(); timer.Dispose(); animation.Dispose(); poster.Dispose(); animatedStream.Dispose(); posterStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
