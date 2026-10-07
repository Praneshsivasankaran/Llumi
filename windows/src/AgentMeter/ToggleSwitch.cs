using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.ComponentModel;

namespace AgentMeter;

// Keep the native checkbox input and accessibility contract; only its appearance changes.
internal sealed class ToggleSwitch : CheckBox
{
    private bool hovered;
    private readonly System.Windows.Forms.Timer transition = new() { Interval = 15 };
    private readonly Stopwatch clock = new();
    private readonly AncestorVisibilityObserver visibility;
    private double thumbPosition, thumbFrom, thumbTarget;
    private bool? lightAppearanceOverride;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool? LightAppearanceOverride { get => lightAppearanceOverride; set { lightAppearanceOverride = value; Invalidate(); } }
    internal bool IsAnimating => transition.Enabled;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal Func<bool> MotionAllowed { get; set; } = () => MonitorAnimation.MotionAllowed;

    public ToggleSwitch()
    {
        visibility = new(this, StopThumb);
        AutoSize = false;
        AccessibleRole = AccessibleRole.CheckButton;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        transition.Tick += (_, _) =>
        {
            var progress = MotionAllowed() ? Math.Min(1, clock.Elapsed.TotalMilliseconds / 140) : 1;
            thumbPosition = thumbFrom + (thumbTarget - thumbFrom) * (1 - Math.Pow(1 - progress, 3)); Invalidate();
            if (progress >= 1) { transition.Stop(); clock.Stop(); }
        };
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessibleObject(this);

    private sealed class ToggleAccessibleObject(ToggleSwitch owner) : CheckBoxAccessibleObject(owner)
    {
        public override void DoDefaultAction()
        {
            // WinForms' default checkbox action does not check Enabled before dispatching a click.
            if (owner.Enabled && !owner.IsDisposed) base.DoDefaultAction();
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return new(text.Width + S(62), Math.Max(text.Height + S(8), S(34)));
    }

    private void PaintSurface(PaintEventArgs e)
    {
        var background = Palette.HighContrast ? SystemColors.Window : BackColor;
        if (background.A < 255) { base.OnPaintBackground(e); return; }
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.ClipRectangle);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => PaintSurface(e);

    protected override void OnPaint(PaintEventArgs e)
    {
        // ButtonBase marks checkbox controls Opaque, so live HWND painting can skip
        // OnPaintBackground. Paint the clipped surface here before text and track.
        PaintSurface(e);
        var rtl = RightToLeft == RightToLeft.Yes;
        var track = new Rectangle(rtl ? S(3) : Width - S(47), (Height - S(24)) / 2, S(44), S(24));
        var highContrast = Palette.HighContrast;
        var light = LightAppearanceOverride ?? Palette.IsLight;
        var muted = LightAppearanceOverride is null ? Palette.Muted : light ? Color.FromArgb(101, 108, 119) : Color.FromArgb(177, 183, 194);
        var card = LightAppearanceOverride is null ? Palette.Card : light ? Color.FromArgb(253, 253, 254) : Color.FromArgb(43, 45, 49);
        var secondary = LightAppearanceOverride is null ? Palette.Secondary : light ? Color.FromArgb(232, 235, 239) : Color.FromArgb(55, 58, 64);
        var trackColor = LightAppearanceOverride is null ? Palette.Track : light ? Color.FromArgb(227, 231, 237) : Color.FromArgb(66, 71, 81);
        var on = Color.FromArgb(0, 103, 192);
        var fill = highContrast
            ? Checked ? SystemColors.Highlight : SystemColors.Window
            : !Enabled ? card : Checked ? hovered ? Color.FromArgb(0, 89, 168) : on : hovered ? trackColor : secondary;
        var border = highContrast ? Enabled ? SystemColors.WindowText : SystemColors.GrayText
            : Checked && Enabled ? fill : muted;
        var thumb = !Enabled ? highContrast ? SystemColors.GrayText : muted
            : Checked ? highContrast ? SystemColors.HighlightText : Color.White
            : highContrast ? SystemColors.WindowText : muted;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        var diameter = track.Height - 1;
        path.AddArc(track.Left, track.Top, diameter, diameter, 90, 180);
        path.AddArc(track.Right - diameter - 1, track.Top, diameter, diameter, 270, 180);
        path.CloseFigure();
        using var fillBrush = new SolidBrush(fill);
        using var borderPen = new Pen(border, Math.Max(1, S(1)));
        e.Graphics.FillPath(fillBrush, path);
        e.Graphics.DrawPath(borderPen, path);
        var thumbSize = S(16);
        var visualPosition = rtl ? 1 - thumbPosition : thumbPosition;
        var thumbX = track.Left + S(4) + (float)((track.Width - S(8) - thumbSize) * visualPosition);
        using var thumbBrush = new SolidBrush(thumb);
        e.Graphics.FillEllipse(thumbBrush, thumbX, track.Top + S(4), thumbSize, thumbSize);
        var textBounds = rtl ? new Rectangle(track.Right + S(12), 0, Math.Max(0, Width - track.Right - S(15)), Height)
            : new Rectangle(S(3), 0, Math.Max(0, track.Left - S(15)), Height);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (rtl) flags |= TextFormatFlags.Right | TextFormatFlags.RightToLeft;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
            Enabled ? highContrast ? SystemColors.WindowText : ForeColor : SystemColors.GrayText, flags);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -S(1), -S(1)),
                highContrast ? SystemColors.WindowText : ForeColor, highContrast ? SystemColors.Window : BackColor);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        thumbTarget = Checked ? 1 : 0; transition.Stop(); clock.Stop();
        if (Visible && Enabled && MotionAllowed()) { thumbFrom = thumbPosition; clock.Restart(); transition.Start(); }
        else thumbPosition = thumbTarget;
        Invalidate();
    }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Invalidate(); }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e); StopThumb();
    }
    private void StopThumb() { transition.Stop(); clock.Stop(); thumbPosition = Checked ? 1 : 0; Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) { visibility.Dispose(); transition.Dispose(); } base.Dispose(disposing); }
}
