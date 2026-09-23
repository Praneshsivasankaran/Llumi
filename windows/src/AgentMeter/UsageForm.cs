using System.ComponentModel;
using AgentMeter.Core;

namespace AgentMeter;

internal sealed class UsageForm : Form
{
    private readonly Panel header = new();
    private readonly Label title = new() { Text = "Llumi", AutoSize = false };
    private readonly Label footer = new() { AutoEllipsis = true };
    private readonly Button usageTab = Palette.Button("Usage", "Usage");
    private readonly Button settingsTab = Palette.Button("Settings", "Settings");
    private readonly GlyphButton refresh = new(Glyph.Refresh, "Refresh usage", "Refresh");
    private readonly GlyphButton menu = new(Glyph.Menu, "Llumi menu");
    private readonly Panel content = new() { AutoScroll = true };
    private readonly Panel settings = new() { Name = "settings", AutoScroll = true };
    private readonly CheckBox launch = new() { Text = "Launch at Startup", AutoSize = true };
    private readonly CheckBox compact = new() { Text = "Compact Monitor", AutoSize = true };
    private readonly CheckBox tray = new() { Text = "Tray Icon", AutoSize = true };
    private readonly ComboBox appearance = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Appearance" };
    private readonly Label settingsMessage = new() { AutoSize = false };
    private readonly Label appearanceLabel = new() { Text = "Appearance", AutoSize = true };
    private readonly Label preferencesHeading = new() { Text = "Preferences", AutoSize = true };
    private readonly Label providerHeading = new() { Text = "Provider Setup", AutoSize = true };
    private readonly Label providerHint = new() { Text = "Check your locally installed tools. Sign-in stays with each provider.", AutoSize = false };
    private readonly Label codexSetup = new() { AutoSize = false, AccessibleName = "Codex setup status" };
    private readonly Label claudeSetup = new() { AutoSize = false, AccessibleName = "Claude Code setup status" };
    private readonly Button checkAgain = Palette.Button("Check Again", "Check Again");
    private readonly Button copyDiagnostics = Palette.Button("Copy Diagnostics", "Copy Diagnostics");
    private readonly Button setupGuide = Palette.Button("Setup Llumi…", "Setup Llumi");
    private readonly Label diagnosticMessage = new() { Text = "Copies only app, OS and provider status categories. Nothing is uploaded.", AutoSize = false };
    private readonly ContextMenuStrip actions = new();
    private readonly ToolTip hints = new();
    private readonly Dictionary<string, ProviderCard> cards = new();
    private readonly Font headingFont = new("Segoe UI", 13, FontStyle.Bold);
    private readonly Font bodyFont = new("Segoe UI", 10);
    private IReadOnlyList<ProviderState> lastStates = [];
    private bool loading, logFailed, rendering, syncing, settingsShown;
    private Preferences preferences = new();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool AllowExit { get; set; }
    internal event Action? SetupRequested;
    internal event Action? RefreshRequested;
    internal event Action? ExitRequested;
    internal event Action? PinRequested;
    internal event Action? StartupToggleRequested;
    internal event Action? MenuOpening;
    internal event Action<Preferences>? PreferencesChanged;

    public UsageForm(IEnumerable<string> names, Icon icon)
    {
        Text = "Llumi"; Icon = icon; Font = bodyFont;
        FormBorderStyle = FormBorderStyle.Sizable; ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        title.Font = headingFont; title.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.AddRange([title, usageTab, settingsTab, refresh, menu]);
        header.Paint += (_, e) => GlyphDrawing.DrawIdentity(e.Graphics, new Rectangle(S(18), S(15), S(18), S(22)));
        Controls.AddRange([header, content, settings, footer]);
        foreach (var name in names) { var card = new ProviderCard(hints); cards[name] = card; content.Controls.Add(card); }
        appearance.Items.AddRange(["System", "Light", "Dark"]);
        settings.Controls.AddRange([preferencesHeading, launch, compact, tray, appearanceLabel, appearance, settingsMessage,
            providerHeading, providerHint, codexSetup, claudeSetup, checkAgain, copyDiagnostics, setupGuide, diagnosticMessage]);
        preferencesHeading.Font = providerHeading.Font = headingFont;
        checkAgain.Click += (_, _) => RefreshRequested?.Invoke();
        setupGuide.Click += (_, _) => SetupRequested?.Invoke();
        copyDiagnostics.Click += (_, _) => {
            try { Clipboard.SetText(DiagnosticReport()); diagnosticMessage.Text = "Diagnostics copied. Nothing is uploaded."; }
            catch (System.Runtime.InteropServices.ExternalException) { diagnosticMessage.Text = "Clipboard is busy. Please try again."; }
        };
        launch.Location = new(S(20), S(24)); compact.Location = new(S(20), S(68)); tray.Location = new(S(20), S(112));
        appearanceLabel.Location = new(S(20), S(164)); appearance.SetBounds(S(20), S(194), S(200), S(30));
        settingsMessage.SetBounds(S(20), S(244), S(410), S(100));
        settingsMessage.Text = "Llumi stays available from the taskbar when the tray icon is hidden.\n\nIndependent of OpenAI and Anthropic. No Llumi account or telemetry.";
        usageTab.Click += (_, _) => ShowUsage(); settingsTab.Click += (_, _) => ShowSettings();
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        actions.Items.Add("Open Llumi", null, (_, _) => ShowUsage());
        actions.Items.Add("Refresh", null, (_, _) => RefreshRequested?.Invoke());
        actions.Items.Add("Settings", null, (_, _) => ShowSettings());
        actions.Items.Add("Setup Llumi…", null, (_, _) => SetupRequested?.Invoke());
        actions.Items.Add("Quit", null, (_, _) => ExitRequested?.Invoke());
        menu.Click += (_, _) => { MenuOpening?.Invoke(); actions.Show(menu, new Point(0, menu.Height)); };
        launch.Click += (_, _) => { if (!syncing) StartupToggleRequested?.Invoke(); };
        compact.CheckedChanged += (_, _) => SavePreferences(); tray.CheckedChanged += (_, _) => SavePreferences();
        appearance.SelectedIndexChanged += (_, _) => SavePreferences();
        FormClosing += (_, e) => { if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HidePanel(); } };
        Resize += (_, _) => { if (!rendering) Render(lastStates, loading, logFailed); };
        DpiChanged += (_, _) => { FitInitialWindow(Screen.FromControl(this).WorkingArea); Render(lastStates, loading, logFailed); KeepOnScreen(); };
        _ = Handle;
        FitInitialWindow(Screen.FromControl(this).WorkingArea);
        SetPreferences(preferences);
    }
    private int S(int n) => (int)Math.Round(n * DeviceDpi / 96f);
    private void FitInitialWindow(Rectangle work)
    {
        // WinForms autoscaling is disabled because this view lays out in device pixels.
        // Set dimensions only after the handle establishes the monitor's actual DPI.
        var available = new Size(Math.Max(1, work.Width - S(24)), Math.Max(1, work.Height - S(24)));
        MinimumSize = new(Math.Min(S(380), available.Width), Math.Min(S(320), available.Height));
        var desired = SizeFromClientSize(new Size(S(640), S(440)));
        Size = new(Math.Min(desired.Width, available.Width), Math.Min(desired.Height, available.Height));
    }
    private void SavePreferences()
    {
        if (syncing || appearance.SelectedIndex < 0) return;
        preferences = new(compact.Checked, tray.Checked, (Appearance)appearance.SelectedIndex);
        PreferencesChanged?.Invoke(preferences);
    }
    internal void SetPreferences(Preferences value)
    {
        preferences = value; syncing = true;
        compact.Checked = value.CompactMonitor; tray.Checked = value.TrayIcon; appearance.SelectedIndex = (int)value.Appearance;
        syncing = false; ApplyTheme();
    }
    internal void PreferenceSaveFailed() => settingsMessage.Text = "Settings could not be saved. The previous preferences remain active.";
    internal void ApplyTheme()
    {
        void Theme(Control root)
        {
            root.BackColor = Palette.Background; root.ForeColor = Palette.Foreground;
            foreach (Control child in root.Controls) Theme(child);
        }
        Theme(this); footer.ForeColor = Palette.Muted; settingsMessage.ForeColor = Palette.Muted;
        providerHint.ForeColor = diagnosticMessage.ForeColor = Palette.Muted;
        foreach (var status in new[] { codexSetup, claudeSetup }) { status.BackColor = Palette.Card; status.Padding = new Padding(S(14)); }
        foreach (var button in new[] { usageTab, settingsTab, checkAgain, copyDiagnostics, setupGuide })
        { if (button != usageTab && button != settingsTab) button.BackColor = Palette.Card; Palette.StyleButton(button); }
        Render(lastStates, loading, logFailed); Invalidate(true);
    }
    internal string DiagnosticReport() => SetupDiagnostics.Report(lastStates,
        typeof(UsageForm).Assembly.GetName().Version?.ToString(3), typeof(UsageForm).Assembly.GetName().Version?.ToString());
    internal void SetStartupState(bool enabled, bool available) { syncing = true; launch.Checked = enabled; launch.Enabled = available; syncing = false; }
    internal void ShowUsage() { settingsShown = false; Render(lastStates, loading, logFailed); }
    internal void ShowSettings() { settingsShown = true; MenuOpening?.Invoke(); Render(lastStates, loading, logFailed); }
    internal void ShowPanel(Point anchor)
    {
        if (!Visible) Location = PopupPlacement.NearTray(Screen.FromPoint(anchor).Bounds, Screen.FromPoint(anchor).WorkingArea, Size, S(16));
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show(); Activate(); KeepOnScreen();
    }
    internal void HidePanel() { actions.Close(); if (preferences.TrayIcon) Hide(); else WindowState = FormWindowState.Minimized; }
    internal void KeepOnScreen() => Location = PopupPlacement.Clamp(Location, Size, Screen.FromControl(this).WorkingArea);
    internal void Render(IReadOnlyList<ProviderState> states, bool loading, bool logFailed, Rectangle? availableWorkArea = null)
    {
        lastStates = states; this.loading = loading; this.logFailed = logFailed;
        if (rendering) return;
        rendering = true;
        try
        {
            if (availableWorkArea is { } work) FitInitialWindow(work);
            var width = ClientSize.Width; var height = ClientSize.Height;
            header.SetBounds(0, 0, width, S(103));
            title.SetBounds(S(46), S(7), width - S(90), S(38));
            usageTab.SetBounds(S(16), S(56), S(95), S(32)); settingsTab.SetBounds(S(120), S(56), S(95), S(32));
            menu.SetBounds(width - S(46), S(12), S(28), S(28)); refresh.SetBounds(width - S(136), S(57), S(120), S(30));
            content.SetBounds(S(12), S(105), width - S(24), Math.Max(S(60), height - S(150)));
            settings.Bounds = content.Bounds; settings.Visible = settingsShown; content.Visible = !settingsShown;
            var settingsScroll = settings.AutoScrollPosition;
            settings.AutoScrollPosition = Point.Empty;
            var settingsWidth = Math.Max(S(240), settings.ClientSize.Width - S(40) - SystemInformation.VerticalScrollBarWidth);
            providerHeading.Location = new(S(20), S(12));
            providerHint.SetBounds(S(20), S(51), settingsWidth, S(32));
            var twoColumns = settingsWidth >= S(500);
            var setupWidth = twoColumns ? (settingsWidth - S(12)) / 2 : settingsWidth;
            codexSetup.SetBounds(S(20), S(88), setupWidth, S(128));
            claudeSetup.SetBounds(twoColumns ? S(32) + setupWidth : S(20), twoColumns ? S(88) : S(228), setupWidth, S(128));
            var actionsY = twoColumns ? 232 : 372;
            checkAgain.SetBounds(S(20), S(actionsY), S(112), S(36));
            copyDiagnostics.SetBounds(S(144), S(actionsY), S(144), S(36));
            diagnosticMessage.SetBounds(S(20), S(actionsY + 48), settingsWidth, S(48));
            setupGuide.SetBounds(S(20), S(actionsY + 104), S(140), S(36));
            var preferencesY = actionsY + 170;
            preferencesHeading.Location = new(S(20), S(preferencesY));
            launch.Location = new(S(20), S(preferencesY + 44)); compact.Location = new(S(20), S(preferencesY + 80)); tray.Location = new(S(20), S(preferencesY + 116));
            appearanceLabel.Location = new(S(20), S(preferencesY + 162)); appearance.SetBounds(S(20), S(preferencesY + 189), S(200), S(30));
            settingsMessage.SetBounds(S(20), S(preferencesY + 235), settingsWidth, S(100));
            settings.AutoScrollMinSize = new(0, S(preferencesY + 350));
            settings.AutoScrollPosition = new(0, -settingsScroll.Y);
            string SetupText(string name, string title)
            {
                var state = states.FirstOrDefault(s => s.Name == name || (name == "Claude" && s.Name == "Claude Code"))
                    ?? new ProviderState(name, ProviderStatus.Loading);
                var value = SetupDiagnostic.From(state);
                return $"{title} · {value.Status}\nInstallation: {value.Detected}\nAuthentication: {value.Authentication}\nUsage: {value.Usage}";
            }
            codexSetup.Text = SetupText("Codex", "Codex"); claudeSetup.Text = SetupText("Claude", "Claude Code");
            checkAgain.Enabled = !loading; checkAgain.Text = loading ? "Checking…" : "Check Again";
            refresh.Visible = !settingsShown; refresh.Text = loading ? "Refreshing…" : "Refresh";
            footer.SetBounds(S(24), height - S(39), width - S(48), S(30));
            var observed = states.Where(s => s.Snapshot is not null).Select(s => s.Snapshot!.ObservedAt).DefaultIfEmpty().Min();
            footer.Text = logFailed ? "Diagnostic log unavailable" : observed == default ? "Connect your tools to see remaining allowance." : $"Last updated {observed.ToLocalTime():HH:mm} · Refreshes automatically";
            if (settingsShown && !logFailed) footer.Text = "Preferences are saved automatically.";
            var scroll = content.AutoScrollPosition;
            content.AutoScrollPosition = Point.Empty;
            var y = 0;
            var columns = content.Width >= S(560) && states.Count > 1 ? 2 : 1;
            var cardWidth = (content.Width - SystemInformation.VerticalScrollBarWidth - S(16) * (columns - 1)) / columns;
            var index = 0; var rowHeight = 0;
            foreach (var (name, card) in cards) card.Visible = states.Any(s => s.Name == name);
            foreach (var state in states)
            {
                if (!cards.TryGetValue(state.Name, out var card)) { card = new ProviderCard(hints); cards[state.Name] = card; content.Controls.Add(card); }
                card.Width = cardWidth;
                card.Render(state, DateTimeOffset.UtcNow, DeviceDpi / 96f);
                card.SetBounds(index % columns * (cardWidth + S(16)), y, card.Width, S(card.LogicalHeight));
                rowHeight = Math.Max(rowHeight, card.Height);
                if (++index % columns == 0) { y += rowHeight + S(16); rowHeight = 0; }
            }
            if (rowHeight > 0) y += rowHeight + S(16);
            content.AutoScrollMinSize = new(0, y); content.AutoScrollPosition = new(0, -scroll.Y);
        }
        finally { rendering = false; }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { HidePanel(); return true; }
        if (keyData == (Keys.Control | Keys.R)) { RefreshRequested?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    // Kept as a callable control seam for the existing monitor ownership tests.
    internal void RequestMonitor() => PinRequested?.Invoke();
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { actions.Dispose(); hints.Dispose(); headingFont.Dispose(); bodyFont.Dispose(); }
    }
}
