using System.Drawing.Drawing2D;

namespace AgentMeter;

internal static class DrawingHelpers
{
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum Glyph { Pin, Close, Refresh, Menu }

internal static class GlyphDrawing
{
    public static void Draw(Graphics graphics, Glyph glyph, Rectangle bounds, Color color)
    {
        var saved = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.Left, bounds.Top);
        graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (glyph)
        {
            case Glyph.Close:
                graphics.DrawLine(pen, 6, 6, 18, 18);
                graphics.DrawLine(pen, 18, 6, 6, 18);
                break;
            case Glyph.Pin:
                graphics.DrawLines(pen, new PointF[] { new(9, 3), new(19, 13), new(16, 13), new(13, 16), new(13, 19), new(5, 11), new(8, 11), new(11, 8), new(9, 3) });
                graphics.DrawLine(pen, 9, 15, 4, 20);
                break;
            case Glyph.Refresh:
                graphics.DrawArc(pen, 4, 4, 16, 16, 35, 275);
                graphics.DrawLines(pen, new PointF[] { new(20, 4), new(20, 9), new(15, 9) });
                break;
            case Glyph.Menu:
                using (var dot = new SolidBrush(color))
                    foreach (var x in new[] { 5, 11, 17 }) graphics.FillEllipse(dot, x, 10, 3, 3);
                break;
        }
        graphics.Restore(saved);
    }

    public static void DrawIdentity(Graphics graphics, Rectangle bounds)
    {
        var saved = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.X, bounds.Y);
        graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
        using var pen = new Pen(Palette.Foreground, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(pen, 3, 5, 18, 18, 180, 180);
        using var fill = new SolidBrush(Palette.Foreground);
        graphics.FillPolygon(fill, [new(10, 15), new(18, 8), new(14, 18)]);
        graphics.FillEllipse(fill, 10, 14, 4, 4);
        graphics.Restore(saved);
    }
}

// A real Button retains keyboard navigation, accessible names and native Click semantics.
internal sealed class GlyphButton : RoundedButton
{
    private readonly Glyph glyph;

    public GlyphButton(Glyph glyph, string accessibleName, string text = "")
    {
        this.glyph = glyph;
        AccessibleName = accessibleName; Text = text; UseMnemonic = false;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = Palette.Background; ForeColor = Palette.Foreground;
        Cursor = Cursors.Hand; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void PaintContent(Graphics graphics, Color ink)
    {
        var scale = DeviceDpi / 96f;
        var iconSize = (int)Math.Round(18 * scale);
        var hasText = !string.IsNullOrEmpty(Text);
        var iconX = hasText ? (int)Math.Round(9 * scale) : (Width - iconSize) / 2;
        GlyphDrawing.Draw(graphics, glyph, new(iconX, (Height - iconSize) / 2, iconSize, iconSize), ink);
        if (hasText)
        {
            var textBounds = new Rectangle(iconX + iconSize + (int)Math.Round(5 * scale), 0, Width - iconX - iconSize - (int)Math.Round(9 * scale), Height);
            TextRenderer.DrawText(graphics, Text, Font, textBounds, ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}
