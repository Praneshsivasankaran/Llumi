using System.Text.Json;

namespace AgentMeter;

internal sealed record MonitorPosition(int Version, string Display, double Left, double Top)
{
    internal bool IsValid => Version is 1 or 2 && !string.IsNullOrWhiteSpace(Display) && Display.Length <= 256 &&
        !Display.Any(char.IsControl) && double.IsFinite(Left) && double.IsFinite(Top) &&
        (Version == 1 ? Math.Abs(Left) <= 100_000 && Math.Abs(Top) <= 100_000 : Left is >= 0 and <= 1 && Top is >= 0 and <= 1);

    internal static string SelectDisplay(string? savedDisplay, string primaryDisplay, IEnumerable<string> connectedDisplays) =>
        connectedDisplays.FirstOrDefault(display => string.Equals(display, savedDisplay, StringComparison.OrdinalIgnoreCase))
        ?? primaryDisplay;

    // Version 1 stores logical offsets. Version 2 stores the horizontal center and top
    // relative to the display's work area, independent of window size and display DPI.
    internal static MonitorPosition Capture(Point location, string display, Rectangle workArea, Size size)
    {
        var point = PopupPlacement.Clamp(location, size, workArea);
        return new(2, display,
            Math.Clamp((point.X + size.Width / 2d - workArea.Left) / Math.Max(1, workArea.Width), 0, 1),
            Math.Clamp(((double)point.Y - workArea.Top) / Math.Max(1, workArea.Height), 0, 1));
    }

    internal static Point Restore(MonitorPosition? saved, string display, Rectangle workArea, Size size, int dpi)
    {
        var scale = SafeDpi(dpi) / 96d;
        var sameDisplay = saved is { IsValid: true } && string.Equals(saved.Display, display, StringComparison.OrdinalIgnoreCase);
        if (sameDisplay && saved!.Version == 2)
            return PopupPlacement.Clamp(new Point(
                SafeCoordinate(workArea.Left + Math.Round(saved.Left * workArea.Width - size.Width / 2d)),
                SafeCoordinate(workArea.Top + Math.Round(saved.Top * workArea.Height))), size, workArea);
        var x = sameDisplay ? saved!.Left : 20;
        var y = sameDisplay ? saved!.Top : 20;
        return PopupPlacement.Clamp(new Point(
            SafeCoordinate(workArea.Left + Math.Round(x * scale)),
            SafeCoordinate(workArea.Top + Math.Round(y * scale))), size, workArea);
    }

    private static int SafeDpi(int dpi) => dpi is >= 48 and <= 960 ? dpi : 96;
    private static int SafeCoordinate(double value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}

// UI preferences only: no provider state, account binding or credentials are persisted.
internal sealed class MonitorPositionStore(string path, Action<string>? log = null)
{
    internal static MonitorPositionStore Default(Action<string>? log = null) => new(Path.Combine(
        PackagedEnvironment.DataDirectory, "monitor-position.json"), log);

    internal MonitorPosition? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length is <= 0 or > 4096) { log?.Invoke("monitor.position-invalid"); return null; }
            var bytes = new byte[4097];
            var total = 0;
            while (total < bytes.Length)
            {
                var count = input.Read(bytes, total, bytes.Length - total);
                if (count == 0) break;
                total += count;
            }
            if (total > 4096) { log?.Invoke("monitor.position-invalid"); return null; }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, total), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var expected = new HashSet<string>(["Version", "Display", "Left", "Top"], StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !expected.Remove(p.Name)) || expected.Count != 0)
            { log?.Invoke("monitor.position-invalid"); return null; }
            var position = root.Deserialize<MonitorPosition>();
            if (position is not { IsValid: true }) { log?.Invoke("monitor.position-invalid"); return null; }
            return position;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { log?.Invoke("monitor.position-read-failed"); return null; }
    }

    internal bool Save(MonitorPosition position)
    {
        if (!position.IsValid) { log?.Invoke("monitor.position-invalid"); return false; }
        string? temporary = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, position); output.Flush(true); }
            File.Move(temporary, fullPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { log?.Invoke("monitor.position-write-failed"); return false; }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { log?.Invoke("monitor.position-cleanup-failed"); }
            }
        }
    }
}
