namespace AgentMeter;

internal static class AppIcon
{
    public static Bitmap LoadArtwork()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AgentMeter.Assets.Llumi.png")
            ?? throw new InvalidOperationException("Application artwork resource is missing.");
        using var original = new Bitmap(stream);
        return new Bitmap(original);
    }

    public static Icon Load(int size = 32)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AgentMeter.Assets.Llumi.ico")
            ?? throw new InvalidOperationException("Application icon resource is missing.");
        using var original = new Icon(stream, new Size(size, size));
        return (Icon)original.Clone();
    }
    // Use the same embedded identity at the native notification-area size.
    public static Icon LoadTray(int size) => Load(size);
}
