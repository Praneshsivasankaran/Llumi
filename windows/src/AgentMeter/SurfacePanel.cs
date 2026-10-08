using System.Drawing.Drawing2D;

namespace AgentMeter;

internal static class SurfaceDrawing
{
    internal static void Card(Graphics graphics, RectangleF bounds, float scale)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Palette.IsLight && !Palette.HighContrast)
        {
            for (var layer = 3; layer > 0; layer--)
            {
                var shadowBounds = RectangleF.Inflate(bounds, layer * scale * .6f, layer * scale * .6f);
                shadowBounds.Y += scale;
                using var shadow = DrawingHelpers.RoundedRectangle(shadowBounds, (14 + layer * .6f) * scale);
                using var brush = new SolidBrush(Color.FromArgb(4, Color.Black)); graphics.FillPath(brush, shadow);
            }
        }
        using var shape = DrawingHelpers.RoundedRectangle(bounds, 14 * scale);
        using var fill = new SolidBrush(Palette.HighContrast ? SystemColors.Window : Palette.Card);
        using var border = new Pen(Palette.HighContrast ? SystemColors.WindowText : Palette.Border, Math.Max(1, scale * .75f));
        graphics.FillPath(fill, shape); graphics.DrawPath(border, shape);
    }
}

internal sealed class SurfacePanel : Panel
{
    private Rectangle[] surfaces = [];
    internal SurfacePanel() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    internal void SetSurfaces(params Rectangle[] bounds) { surfaces = bounds; Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        foreach (var source in surfaces)
        {
            var bounds = source; bounds.Offset(AutoScrollPosition);
            SurfaceDrawing.Card(e.Graphics, bounds, DeviceDpi / 96f);
        }
    }
}
