namespace AgentMeter;

internal sealed class MonitorDragGesture(Point origin, int dpi)
{
    internal Point Origin { get; } = origin;
    internal bool IsDragging { get; private set; }

    internal void Move(Point point)
    {
        var x = (double)point.X - Origin.X;
        var y = (double)point.Y - Origin.Y;
        var threshold = 5 * Math.Clamp(dpi, 48, 768) / 96d;
        // Once the gesture crosses the threshold, returning to the press point is
        // still a drag and must never activate the monitor's click action.
        if (x * x + y * y >= threshold * threshold) IsDragging = true;
    }
}
