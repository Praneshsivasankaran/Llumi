using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace AgentMeter;

// Button keeps native keyboard/default-button/accessibility behavior. Motion is
// limited to a short hover transition and never owns an idle rendering loop.
internal class RoundedButton : Button
{
    private readonly System.Windows.Forms.Timer transition = new() { Interval = 15 };
    private readonly Stopwatch clock = new();
    private readonly AncestorVisibilityObserver visibility;
    private double hover, from, target;
    private bool pressed, selected;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Navigation { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Selected { get => selected; set { if (selected == value) return; selected = value; Invalidate(); } }
    internal bool IsAnimating => transition.Enabled;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal Func<bool> MotionAllowed { get; set; } = () => MonitorAnimation.MotionAllowed;

    internal RoundedButton()
    {
        visibility = new(this, StopHover);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        transition.Tick += (_, _) =>
        {
            var progress = MotionAllowed() ? Math.Min(1, clock.Elapsed.TotalMilliseconds / 110) : 1;
            hover = from + (target - from) * (1 - Math.Pow(1 - progress, 3)); Invalidate();
            if (progress >= 1) { transition.Stop(); clock.Stop(); }
        };
    }
    private void SetHover(double value)
    {
        target = value; transition.Stop(); clock.Stop();
        if (!Visible || !Enabled || !MotionAllowed()) { hover = value; Invalidate(); return; }
        from = hover; clock.Restart(); transition.Start();
    }
    private void StopHover() { transition.Stop(); clock.Stop(); hover = target = 0; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); SetHover(1); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pressed = false; SetHover(0); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) SetHover(0); Invalidate(); }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e); StopHover();
    }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = DeviceDpi / 96f;
        var highContrast = Palette.HighContrast;
        var background = Navigation ? BackColor : Parent?.BackColor ?? BackColor;
        using (var outside = new SolidBrush(highContrast ? SystemColors.Window : background)) e.Graphics.FillRectangle(outside, e.ClipRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var isLight = BackColor.GetBrightness() > .6;
        var fill = highContrast ? Selected ? SystemColors.Highlight : SystemColors.Window
            : Navigation ? Selected ? isLight ? Color.White : Color.FromArgb(58, 62, 69) : background : BackColor;
        if (!highContrast) fill = Mix(fill, isLight ? Color.Black : Color.White, pressed ? .10 : hover * .045);
        var ink = highContrast ? Selected ? SystemColors.HighlightText : SystemColors.WindowText : ForeColor;
        if (!Enabled) ink = highContrast ? SystemColors.GrayText : Mix(ForeColor, BackColor, .52);
        var bounds = new RectangleF(.75f * scale, .75f * scale, Math.Max(1, Width - 1.5f * scale), Math.Max(1, Height - 1.5f * scale));
        if (!highContrast && Navigation && Selected && isLight)
        {
            using var shadow = DrawingHelpers.RoundedRectangle(new(bounds.X, bounds.Y + scale, bounds.Width, bounds.Height), 8 * scale);
            using var shadowBrush = new SolidBrush(Color.FromArgb(14, Color.Black)); e.Graphics.FillPath(shadowBrush, shadow);
        }
        using var shape = DrawingHelpers.RoundedRectangle(bounds, 8 * scale);
        using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, shape);
        if (!Navigation || Selected || highContrast)
        {
            using var border = new Pen(highContrast ? SystemColors.WindowText : Mix(fill, ink, .12), Math.Max(1, scale));
            e.Graphics.DrawPath(border, shape);
        }
        PaintContent(e.Graphics, ink);
        if (Focused && ShowFocusCues)
        {
            using var focus = DrawingHelpers.RoundedRectangle(RectangleF.Inflate(bounds, -2 * scale, -2 * scale), 6 * scale);
            var prominent = BackColor.B > BackColor.R + 40 && ForeColor.GetBrightness() > .6;
            using var pen = new Pen(highContrast ? Selected ? SystemColors.HighlightText : SystemColors.Highlight
                : prominent ? Color.White : Color.FromArgb(0, 103, 192), 1.4f * scale);
            e.Graphics.DrawPath(pen, focus);
        }
    }
    protected virtual void PaintContent(Graphics graphics, Color ink) => TextRenderer.DrawText(graphics, Text, Font,
        Rectangle.Inflate(ClientRectangle, -(int)Math.Round(8 * DeviceDpi / 96f), 0), ink,
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.EndEllipsis | (UseMnemonic ? ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix : TextFormatFlags.NoPrefix));
    private static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * amount), (int)Math.Round(a.G + (b.G - a.G) * amount), (int)Math.Round(a.B + (b.B - a.B) * amount));
    protected override void Dispose(bool disposing) { if (disposing) { visibility.Dispose(); transition.Dispose(); } base.Dispose(disposing); }
}

// WinForms deliberately omits child VisibleChanged when an ancestor hides.
// Observe the ancestor chain so hidden controls stop immediately, not on a later tick.
internal sealed class AncestorVisibilityObserver : IDisposable
{
    private readonly Control owner;
    private readonly Action hidden;
    private readonly List<Control> ancestors = [];
    internal AncestorVisibilityObserver(Control owner, Action hidden)
    {
        this.owner = owner; this.hidden = hidden;
        owner.ParentChanged += Rebind; Rebind(null, EventArgs.Empty);
    }
    private void Rebind(object? sender, EventArgs e)
    {
        Detach();
        for (var parent = owner.Parent; parent is not null; parent = parent.Parent)
        { ancestors.Add(parent); parent.VisibleChanged += Check; parent.ParentChanged += Rebind; }
        Check(null, EventArgs.Empty);
    }
    private void Check(object? sender, EventArgs e) { if (!owner.Visible) hidden(); }
    private void Detach()
    {
        foreach (var parent in ancestors) { parent.VisibleChanged -= Check; parent.ParentChanged -= Rebind; }
        ancestors.Clear();
    }
    public void Dispose() { owner.ParentChanged -= Rebind; Detach(); }
}
