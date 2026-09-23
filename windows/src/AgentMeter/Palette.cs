namespace AgentMeter;

internal static class Palette
{
    public static Color Background = Color.FromArgb(38, 38, 38);
    public static Color Card = Color.FromArgb(49, 57, 66);
    public static Color Secondary = Color.FromArgb(59, 68, 78);
    public static Color Border = Color.FromArgb(77, 85, 94);
    public static Color Foreground = Color.FromArgb(244, 247, 251);
    public static Color Muted = Color.FromArgb(183, 190, 200);
    public static bool IsLight { get; private set; }
    public static void Apply(Appearance appearance, bool? systemLight = null)
    {
        var light = appearance == Appearance.Light;
        if (appearance == Appearance.System)
        {
            if (systemLight is { } supplied) light = supplied;
            else
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                light = key?.GetValue("AppsUseLightTheme") is int value && value != 0;
            }
        }
        IsLight = light;
        Background = light ? Color.FromArgb(245, 245, 245) : Color.FromArgb(38, 38, 38);
        Foreground = light ? Color.FromArgb(28, 28, 28) : Color.FromArgb(244, 247, 251);
        Muted = light ? Color.FromArgb(84, 84, 84) : Color.FromArgb(183, 190, 200);
        Card = light ? Color.FromArgb(228, 231, 235) : Color.FromArgb(49, 57, 66);
        Secondary = light ? Color.FromArgb(213, 220, 228) : Color.FromArgb(59, 68, 78);
        Border = light ? Color.FromArgb(187, 193, 200) : Color.FromArgb(77, 85, 94);
        Track = light ? Color.FromArgb(204, 210, 218) : Color.FromArgb(76, 85, 97);
        Live = light ? Color.FromArgb(29, 119, 43) : Color.FromArgb(93, 222, 100);
        Warning = light ? Color.FromArgb(146, 91, 12) : Color.FromArgb(242, 192, 105);
    }
    public static readonly Color Codex = Color.FromArgb(46, 166, 240);
    public static readonly Color Claude = Codex;
    public static readonly Color Accent = Codex;
    public static Color Live = Color.FromArgb(93, 222, 100);
    public static Color Warning = Color.FromArgb(242, 192, 105);
    public static Color Track = Color.FromArgb(76, 85, 97);

    public static Color ProviderAccent(string provider) => provider.StartsWith("Claude", StringComparison.OrdinalIgnoreCase)
        ? Claude : Codex;

    public static Button Button(string text, string accessibleName) => new()
    {
        Text = text, AccessibleName = accessibleName, FlatStyle = FlatStyle.Flat,
        BackColor = Card, ForeColor = Foreground, Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false, TabStop = true
    };

    public static void StyleButton(Button button)
    {
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Secondary;
        button.FlatAppearance.MouseDownBackColor = Track;
    }
}
