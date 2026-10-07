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
    public static bool HighContrast { get; private set; } = SystemInformation.HighContrast;
    public static void Apply(Appearance appearance, bool? systemLight = null, bool? highContrast = null)
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
        // System preference changes call Apply again; the optional value only allows
        // deterministic color regression tests without changing Windows settings.
        HighContrast = highContrast ?? SystemInformation.HighContrast;
        if (HighContrast)
        {
            Background = Card = Secondary = SystemColors.Window;
            Foreground = Muted = Border = Live = Warning = SystemColors.WindowText;
            Track = SystemColors.GrayText;
            return;
        }
        Background = light ? Color.FromArgb(247, 248, 250) : Color.FromArgb(32, 33, 36);
        Foreground = light ? Color.FromArgb(30, 33, 38) : Color.FromArgb(244, 245, 247);
        Muted = light ? Color.FromArgb(101, 108, 119) : Color.FromArgb(177, 183, 194);
        Card = light ? Color.FromArgb(253, 253, 254) : Color.FromArgb(43, 45, 49);
        Secondary = light ? Color.FromArgb(232, 235, 239) : Color.FromArgb(55, 58, 64);
        Border = light ? Color.FromArgb(217, 221, 227) : Color.FromArgb(67, 71, 79);
        Track = light ? Color.FromArgb(227, 231, 237) : Color.FromArgb(66, 71, 81);
        Live = light ? Color.FromArgb(29, 119, 43) : Color.FromArgb(93, 222, 100);
        Warning = light ? Color.FromArgb(146, 91, 12) : Color.FromArgb(242, 192, 105);
    }
    public static readonly Color Codex = Color.FromArgb(46, 166, 240);
    public static readonly Color Claude = Codex;
    public static readonly Color Accent = Codex;
    public static Color Live = Color.FromArgb(93, 222, 100);
    public static Color Warning = Color.FromArgb(242, 192, 105);
    public static Color Track = Color.FromArgb(76, 85, 97);

    public static Color ProviderAccent(string provider) => HighContrast ? SystemColors.WindowText : provider.StartsWith("Claude", StringComparison.OrdinalIgnoreCase)
        ? Claude : Codex;

    public static Button Button(string text, string accessibleName) => new RoundedButton()
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
