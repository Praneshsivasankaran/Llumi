using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentMeter;

internal sealed class ProviderArtwork(string provider) : Control
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        ProviderMark.Draw(e.Graphics, provider, ClientRectangle, Palette.Foreground);
    }
}

// Renders the two audited, embedded vendor vectors; not a general SVG loader.
internal static class ProviderMark
{
    private sealed record Shape(GraphicsPath Path, Color? Fill);
    private static readonly Dictionary<string, (float Width, float Height, Shape[] Shapes)> Marks = new();
    internal static void Draw(Graphics graphics, string provider, RectangleF bounds, Color color)
    {
        var key = provider.StartsWith("Claude", StringComparison.Ordinal) ? "Claude" : "Codex";
        lock (Marks)
        {
            if (!Marks.TryGetValue(key, out var mark))
            {
                var asset = key == "Claude" ? "ClaudeCodeMascot" : "CodexLogo";
                using var stream = typeof(ProviderMark).Assembly.GetManifestResourceStream($"AgentMeter.Assets.{asset}.svg")!;
                var xml = XDocument.Load(stream);
                var dimensions = xml.Root!.Attribute("viewBox")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                mark = (dimensions[2], dimensions[3], xml.Descendants().Where(n => n.Name.LocalName == "path" || key == "Claude" && n.Name.LocalName == "rect")
                    .Select(n => new Shape(n.Name.LocalName == "path" ? Parse(n.Attribute("d")!.Value) : Rectangle(n),
                        key == "Claude" && n.Attribute("fill")?.Value is { } fill && fill.StartsWith('#') ? ColorTranslator.FromHtml(fill) : null)).ToArray());
                Marks[key] = mark;
            }
            var state = graphics.Save();
            var scale = Math.Min(bounds.Width / mark.Width, bounds.Height / mark.Height);
            graphics.TranslateTransform(bounds.X + (bounds.Width - mark.Width * scale) / 2, bounds.Y + (bounds.Height - mark.Height * scale) / 2);
            graphics.ScaleTransform(scale, scale);
            foreach (var shape in mark.Shapes)
            {
                using var brush = new SolidBrush(SystemInformation.HighContrast ? color : shape.Fill ?? color);
                graphics.FillPath(brush, shape.Path);
            }
            graphics.Restore(state);
        }
    }
    private static GraphicsPath Rectangle(XElement element)
    {
        float Number(string name) => float.Parse(element.Attribute(name)?.Value ?? "0", CultureInfo.InvariantCulture);
        var path = new GraphicsPath(); path.AddRectangle(new RectangleF(Number("x"), Number("y"), Number("width"), Number("height"))); return path;
    }
    private static GraphicsPath Parse(string source)
    {
        var tokens = Regex.Matches(source, @"[MLCHVZ]|-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?").Select(m => m.Value).ToArray();
        var path = new GraphicsPath(FillMode.Winding); var index = 0; var x = 0f; var y = 0f; var command = 'M';
        float Number() => float.Parse(tokens[index++], CultureInfo.InvariantCulture);
        while (index < tokens.Length)
        {
            if (tokens[index].Length == 1 && char.IsLetter(tokens[index][0])) command = tokens[index++][0];
            switch (command)
            {
                case 'M': x = Number(); y = Number(); path.StartFigure(); command = 'L'; break;
                case 'L': { var nx = Number(); var ny = Number(); path.AddLine(x, y, nx, ny); x = nx; y = ny; break; }
                case 'H': { var nx = Number(); path.AddLine(x, y, nx, y); x = nx; break; }
                case 'V': { var ny = Number(); path.AddLine(x, y, x, ny); y = ny; break; }
                case 'C': { var a = Number(); var b = Number(); var c = Number(); var d = Number(); var nx = Number(); var ny = Number(); path.AddBezier(x, y, a, b, c, d, nx, ny); x = nx; y = ny; break; }
                case 'Z': path.CloseFigure(); break;
                default: throw new InvalidOperationException("Unsupported embedded vector.");
            }
        }
        return path;
    }
}
