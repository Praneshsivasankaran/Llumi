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
    private readonly Button aboutTab = Palette.Button("About", "About Llumi");
    private readonly AboutView about = new();
    private readonly GlyphButton refresh = new(Glyph.Refresh, "Refresh usage", "Refresh");
    private readonly GlyphButton menu = new(Glyph.Menu, "Llumi menu");
    private readonly Panel content = new() { AutoScroll = true };
    private readonly Panel settings = new() { Name = "settings", AutoScroll = true };
    private readonly CheckBox launch = new() { Text = "Launch at Startup", AutoSize = true };
    private readonly CheckBox compact = new() { Text = "Compact Monitor", AutoSize = true };
    private readonly CheckBox tray = new() { Text = "Tray Icon", AutoSize = true };
    private readonly CheckBox codex = new() { Text = "Monitor Codex", AutoSize = true, AccessibleName = "Monitor Codex" };
    private readonly CheckBox claude = new() { Text = "Monitor Claude Code", AutoSize = true, AccessibleName = "Monitor Claude Code" };
    private readonly ProviderArtwork codexArtwork = new("Codex");
    private readonly ProviderArtwork claudeArtwork = new("Claude");
    private readonly Button resetPosition = Palette.Button("Reset Position", "Reset compact monitor position");
    private readonly Label offMessage = new() { Text = "Monitoring is off.\nEnable Codex or Claude Code in Settings.", AutoSize = false };
    private readonly Button offSettings = Palette.Button("Open Settings", "Enable providers in Settings");
    private readonly ComboBox appearance = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Appearance" };
    private readonly Label settingsMessage = new() { AutoSize = false, Visible = false };
    private readonly Label appearanceLabel = new() { Text = "Appearance", AutoSize = true };
    private readonly Label preferencesHeading = new() { Text = "Preferences", AutoSize = true };
    private readonly Label providerHeading = new() { Text = "Providers", AutoSize = true };
    private readonly Label providerHint = new() { Text = "Check your locally installed tools. Sign-in stays with each provider.", AutoSize = false };
    private readonly Label codexSetup = new() { AutoSize = false, AccessibleName = "Codex setup status" };
    private readonly Label claudeSetup = new() { AutoSize = false, AccessibleName = "Claude Code setup status" };
    private readonly Button checkAgain = Palette.Button("Retry", "Retry provider checks");
    private readonly Label retryMessage = new() { AutoSize = false };
    private readonly System.Windows.Forms.Timer countdown = new() { Interval = 1000 };
    private readonly Button copyDiagnostics = Palette.Button("Copy Diagnostics", "Copy Diagnostics");
    private readonly Label diagnosticMessage = new() { AutoSize = false, Visible = false };
    private readonly ContextMenuStrip actions = new();
    private readonly ToolTip hints = new();
    private readonly Dictionary<string, ProviderCard> cards = new();
    private readonly Font headingFont = new("Segoe UI", 13, FontStyle.Bold);
    private readonly Font bodyFont = new("Segoe UI", 10);
    private Icon headerIdentity = AppIcon.Load();
    private IReadOnlyList<ProviderState> lastStates = [];
    private bool loading, logFailed, rendering, syncing, settingsShown, aboutShown;
    private Preferences preferences = new();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool AllowExit { get; set; }
    internal event Action? SetupRequested;
    internal event Action? RefreshRequested;
    internal event Action? ExitRequested;
    internal event Action? PinRequested;
    internal event Action? StartupToggleRequested;
    internal event Action? ResetPositionRequested;
    internal event Action? MenuOpening;
    internal event Action<Preferences>? PreferencesChanged;

    public UsageForm(IEnumerable<string> names, Icon icon)
    {
        Text = "Llumi"; Icon = icon; Font = bodyFont;
        FormBorderStyle = FormBorderStyle.Sizable; ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        title.Font = headingFont; title.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.AddRange([title, usageTab, settingsTab, aboutTab, refresh, menu]);
        header.Paint += (_, e) => e.Graphics.DrawIcon(headerIdentity, new Rectangle(S(18), S(14), S(24), S(24)));
        Controls.AddRange([header, content, settings, about, footer]);
        foreach (var name in names) { var card = new ProviderCard(hints); cards[name] = card; content.Controls.Add(card); }
        content.Controls.AddRange([offMessage, offSettings]);
        appearance.Items.AddRange(["System", "Light", "Dark"]);
        settings.Controls.AddRange([preferencesHeading, launch, compact, tray, appearanceLabel, appearance, settingsMessage, resetPosition,
            providerHeading, providerHint, codex, claude, codexArtwork, claudeArtwork, codexSetup, claudeSetup, checkAgain, retryMessage, copyDiagnostics, diagnosticMessage]);
        preferencesHeading.Font = providerHeading.Font = headingFont;
        checkAgain.Click += (_, _) => RefreshRequested?.Invoke();
        resetPosition.Click += (_, _) => ResetPositionRequested?.Invoke();
        offSettings.Click += (_, _) => ShowSettings();
        copyDiagnostics.Click += (_, _) => {
            try { Clipboard.SetText(DiagnosticReport()); diagnosticMessage.Text = "Diagnostics copied."; }
            catch (System.Runtime.InteropServices.ExternalException) { diagnosticMessage.Text = "Clipboard is busy. Please try again."; }
            Render(lastStates, loading, logFailed);
        };
        launch.Location = new(S(20), S(24)); compact.Location = new(S(20), S(68)); tray.Location = new(S(20), S(112));
        appearanceLabel.Location = new(S(20), S(164)); appearance.SetBounds(S(20), S(194), S(200), S(30));
        usageTab.Click += (_, _) => ShowUsage(); settingsTab.Click += (_, _) => ShowSettings(); aboutTab.Click += (_, _) => ShowAbout();
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        actions.Items.Add("Open Llumi", null, (_, _) => ShowUsage());
        actions.Items.Add("Refresh", null, (_, _) => RefreshRequested?.Invoke());
        actions.Items.Add("Settings", null, (_, _) => ShowSettings());
        actions.Items.Add("About Llumi", null, (_, _) => ShowAbout());
        actions.Items.Add("Setup Llumi…", null, (_, _) => SetupRequested?.Invoke());
        actions.Items.Add("Quit", null, (_, _) => ExitRequested?.Invoke());
        menu.Click += (_, _) => { MenuOpening?.Invoke(); actions.Show(menu, new Point(0, menu.Height)); };
        launch.Click += (_, _) => { if (!syncing) StartupToggleRequested?.Invoke(); };
        compact.CheckedChanged += (_, _) => SavePreferences(); tray.CheckedChanged += (_, _) => SavePreferences();
        codex.CheckedChanged += (_, _) => SavePreferences(); claude.CheckedChanged += (_, _) => SavePreferences();
        appearance.SelectedIndexChanged += (_, _) => SavePreferences();
        FormClosing += (_, e) => { if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HidePanel(); } };
        Resize += (_, _) => { if (!rendering) Render(lastStates, loading, logFailed); };
        DpiChanged += (_, _) => { LoadHeaderIdentity(); FitInitialWindow(Screen.FromControl(this).WorkingArea); Render(lastStates, loading, logFailed); KeepOnScreen(); };
        countdown.Tick += (_, _) => UpdateRetry();
        VisibleChanged += (_, _) => { if (Visible) countdown.Start(); else countdown.Stop(); };
        _ = Handle;
        LoadHeaderIdentity();
        FitInitialWindow(Screen.FromControl(this).WorkingArea);
        SetPreferences(preferences);
    }
    private int S(int n) => (int)Math.Round(n * DeviceDpi / 96f);
    private void LoadHeaderIdentity()
    {
        var next = AppIcon.Load(S(24)); headerIdentity.Dispose(); headerIdentity = next; header.Invalidate();
    }
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
        preferences = preferences with { CompactMonitor = compact.Checked, TrayIcon = tray.Checked, Appearance = (Appearance)appearance.SelectedIndex,
            CodexEnabled = codex.Checked, ClaudeEnabled = claude.Checked };
        PreferencesChanged?.Invoke(preferences);
    }
    internal void SetPreferences(Preferences value)
    {
        preferences = value; syncing = true;
        compact.Checked = value.CompactMonitor; tray.Checked = value.TrayIcon; appearance.SelectedIndex = (int)value.Appearance;
        codex.Checked = value.CodexEnabled; claude.Checked = value.ClaudeEnabled;
        syncing = false; ApplyTheme();
    }
    internal void PreferenceSaveFailed() => ShowSettingsMessage("Settings could not be saved. The previous preferences remain active.");
    internal void PositionResetFailed() => ShowSettingsMessage("Monitor position could not be saved. Try again.");
    private void ShowSettingsMessage(string message)
    {
        settingsMessage.Text = message;
        Render(lastStates, loading, logFailed);
    }
    internal void ApplyTheme()
    {
        void Theme(Control root)
        {
            if (root is ProviderCard or AboutView) return; // Cards own their pale surface and transparent label backgrounds.
            root.BackColor = Palette.Background; root.ForeColor = Palette.Foreground;
            foreach (Control child in root.Controls) Theme(child);
        }
        Theme(this); footer.ForeColor = Palette.Muted; settingsMessage.ForeColor = Palette.Muted;
        providerHint.ForeColor = diagnosticMessage.ForeColor = retryMessage.ForeColor = offMessage.ForeColor = Palette.Muted;
        foreach (var status in new[] { codexSetup, claudeSetup }) { status.BackColor = Palette.Card; status.Padding = new Padding(S(14)); }
        foreach (var button in new[] { usageTab, settingsTab, aboutTab, checkAgain, copyDiagnostics, resetPosition, offSettings })
        { if (button != usageTab && button != settingsTab && button != aboutTab) button.BackColor = Palette.Card; Palette.StyleButton(button); }
        about.ApplyTheme(); Render(lastStates, loading, logFailed); Invalidate(true);
    }
    internal string DiagnosticReport() => SetupDiagnostics.Report(lastStates,
        typeof(UsageForm).Assembly.GetName().Version?.ToString(3), typeof(UsageForm).Assembly.GetName().Version?.ToString());
    internal void SetStartupState(bool enabled, bool available) { syncing = true; launch.Checked = enabled; launch.Enabled = available; syncing = false; }
    internal void ShowUsage() { settingsShown = aboutShown = false; Render(lastStates, loading, logFailed); }
    internal void ShowSettings() { aboutShown = false; settingsShown = true; MenuOpening?.Invoke(); Render(lastStates, loading, logFailed); }
    internal void ShowAbout() { settingsShown = false; aboutShown = true; about.ShowOverview(); Render(lastStates, loading, logFailed); }
    internal void ShowReleaseNotes() { ShowAbout(); about.ShowReleaseNotes(); }
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
            title.SetBounds(S(52), S(7), width - S(224), S(38));
            usageTab.SetBounds(S(16), S(56), S(95), S(32)); settingsTab.SetBounds(S(120), S(56), S(95), S(32));
            aboutTab.SetBounds(S(224), S(56), S(95), S(32));
            menu.SetBounds(width - S(46), S(12), S(28), S(28)); refresh.SetBounds(width - S(176), S(12), S(120), S(30));
            content.SetBounds(S(12), S(105), width - S(24), Math.Max(S(60), height - S(150)));
            settings.Bounds = about.Bounds = content.Bounds; settings.Visible = settingsShown; about.Visible = aboutShown; content.Visible = !settingsShown && !aboutShown;
            var settingsScroll = settings.AutoScrollPosition;
            settings.AutoScrollPosition = Point.Empty;
            var settingsWidth = Math.Max(S(240), settings.ClientSize.Width - S(40) - SystemInformation.VerticalScrollBarWidth);
            providerHeading.Location = new(S(20), S(12));
            providerHint.SetBounds(S(20), S(51), settingsWidth, S(32));
            var twoColumns = settingsWidth >= S(500);
            var setupWidth = twoColumns ? (settingsWidth - S(12)) / 2 : settingsWidth;
            codexArtwork.SetBounds(S(20), S(88), S(26), S(26)); codex.Location = new(S(56), S(90));
            var claudeX = twoColumns ? S(32) + setupWidth : S(20); var claudeY = twoColumns ? 88 : 240;
            claudeArtwork.SetBounds(claudeX, S(claudeY), S(26), S(26)); claude.Location = new(claudeX + S(36), S(claudeY + 2));
            codexSetup.SetBounds(S(20), S(124), setupWidth, S(100));
            claudeSetup.SetBounds(claudeX, S(claudeY + 36), setupWidth, S(100));
            var actionsY = twoColumns ? 240 : 392;
            checkAgain.SetBounds(S(20), S(actionsY), S(112), S(36));
            copyDiagnostics.SetBounds(S(144), S(actionsY), S(144), S(36));
            retryMessage.SetBounds(S(20), S(actionsY + 45), settingsWidth, S(42));
            var showDiagnosticMessage = !string.IsNullOrEmpty(diagnosticMessage.Text);
            diagnosticMessage.Visible = showDiagnosticMessage;
            diagnosticMessage.SetBounds(S(20), S(actionsY + 96), settingsWidth, S(showDiagnosticMessage ? 48 : 0));
            var preferencesY = actionsY + (showDiagnosticMessage ? 152 : 96);
            preferencesHeading.Location = new(S(20), S(preferencesY));
            launch.Location = new(S(20), S(preferencesY + 44)); compact.Location = new(S(20), S(preferencesY + 80)); tray.Location = new(S(20), S(preferencesY + 116));
            appearanceLabel.Location = new(S(20), S(preferencesY + 162)); appearance.SetBounds(S(20), S(preferencesY + 189), S(200), S(30));
            resetPosition.SetBounds(S(20), S(preferencesY + 236), S(145), S(36));
            settingsMessage.Visible = !string.IsNullOrEmpty(settingsMessage.Text);
            settingsMessage.SetBounds(S(20), S(preferencesY + 287), settingsWidth, S(settingsMessage.Visible ? 100 : 0));
            settings.AutoScrollMinSize = new(0, S(preferencesY + (settingsMessage.Visible ? 405 : 292)));
            settings.AutoScrollPosition = new(0, -settingsScroll.Y);
            string SetupText(string name, string title)
            {
                var state = states.FirstOrDefault(s => s.Name == name || (name == "Claude" && s.Name == "Claude Code"))
                    ?? new ProviderState(name, ProviderStatus.Loading);
                var enabled = name == "Codex" ? preferences.CodexEnabled : preferences.ClaudeEnabled;
                var value = SetupDiagnostic.From(state with { Enabled = enabled });
                return $"{title} · {value.Status}\nMonitoring: {value.Monitoring}";
            }
            codexSetup.Text = SetupText("Codex", "Codex"); claudeSetup.Text = SetupText("Claude", "Claude Code");
            UpdateRetry();
            refresh.Visible = !settingsShown && !aboutShown; refresh.Text = loading ? "Refreshing…" : "Refresh";
            footer.SetBounds(S(24), height - S(39), width - S(48), S(30));
            var observed = states.Where(s => s.Enabled && s.Snapshot is not null).Select(s => s.Snapshot!.ObservedAt).DefaultIfEmpty().Min();
            footer.Visible = !settingsShown && !aboutShown || logFailed;
            footer.Text = logFailed ? "Diagnostic log unavailable" : settingsShown || aboutShown ? string.Empty : observed == default ? "Connect your tools to see remaining allowance." : $"Last updated {observed.ToLocalTime():HH:mm} · Refreshes automatically";
            var scroll = content.AutoScrollPosition;
            content.AutoScrollPosition = Point.Empty;
            var y = 0;
            var enabledStates = states.Where(s => s.Enabled && (s.Name == "Codex" ? preferences.CodexEnabled : preferences.ClaudeEnabled)).ToArray();
            var bothOff = !preferences.CodexEnabled && !preferences.ClaudeEnabled || states.Count > 0 && enabledStates.Length == 0;
            offMessage.Visible = offSettings.Visible = bothOff;
            offMessage.SetBounds(S(14), S(24), content.Width - S(36), S(52)); offSettings.SetBounds(S(14), S(86), S(145), S(36));
            var columns = content.Width >= S(560) && enabledStates.Length > 1 ? 2 : 1;
            var cardWidth = (content.Width - SystemInformation.VerticalScrollBarWidth - S(16) * (columns - 1)) / columns;
            foreach (var (name, card) in cards) card.Visible = enabledStates.Any(s => s.Name == name);
            foreach (var state in enabledStates)
            {
                if (!cards.TryGetValue(state.Name, out var card)) { card = new ProviderCard(hints); cards[state.Name] = card; content.Controls.Add(card); }
                card.Width = cardWidth;
                card.Render(state, DateTimeOffset.UtcNow, DeviceDpi / 96f);
            }
            var equalHeight = enabledStates.Length == 0 ? 0 : enabledStates.Max(s => S(cards[s.Name].LogicalHeight));
            for (var index = 0; index < enabledStates.Length; index++)
            {
                var card = cards[enabledStates[index].Name];
                card.SetBounds(index % columns * (cardWidth + S(16)), index / columns * (equalHeight + S(16)), cardWidth, equalHeight);
            }
            y = bothOff ? S(145) : (int)Math.Ceiling((double)enabledStates.Length / columns) * (equalHeight + S(16));
            content.AutoScrollMinSize = new(0, y); content.AutoScrollPosition = new(0, -scroll.Y);
        }
        finally { rendering = false; }
    }
    private void UpdateRetry()
    {
        var now = DateTimeOffset.UtcNow;
        var active = lastStates.Where(s => s.Enabled && (s.Name == "Codex" ? preferences.CodexEnabled : preferences.ClaudeEnabled)).ToArray();
        checkAgain.Enabled = !loading && active.Any(s => SetupRetryPresentation.CanRetry(s, now));
        checkAgain.Text = loading ? "Checking…" : "Retry";
        retryMessage.Text = string.Join("\n", active.Where(s => s.Status == ProviderStatus.Loading || s.RetryAt > now)
            .Select(s => $"{(UsagePresentation.IsClaude(s.Name) ? "Claude Code" : s.Name)}: {PopupText.Retry(s, now)}"));
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { HidePanel(); return true; }
        if (keyData == (Keys.Control | Keys.R)) { RefreshRequested?.Invoke(); return true; }
        if (keyData == (Keys.Control | Keys.D1)) { ShowUsage(); return true; }
        if (keyData == (Keys.Control | Keys.Oemcomma)) { ShowSettings(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    // Kept as a callable control seam for the existing monitor ownership tests.
    internal void RequestMonitor() => PinRequested?.Invoke();
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { headerIdentity.Dispose(); countdown.Dispose(); actions.Dispose(); hints.Dispose(); headingFont.Dispose(); bodyFont.Dispose(); }
    }
}
