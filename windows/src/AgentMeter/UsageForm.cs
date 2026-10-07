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
    private readonly SurfacePanel settings = new() { Name = "settings", AutoScroll = true };
    private readonly ToggleSwitch launch = new() { Text = "Launch at Startup", AccessibleName = "Launch at Startup" };
    private readonly ToggleSwitch compact = new() { Text = "Show monitor", AccessibleName = "Show monitor" };
    private readonly ToggleSwitch tray = new() { Text = "Tray Icon", AccessibleName = "Tray Icon" };
    private readonly ToggleSwitch codex = new() { Text = "Monitor Codex", AccessibleName = "Monitor Codex" };
    private readonly ToggleSwitch claude = new() { Text = "Monitor Claude Code", AccessibleName = "Monitor Claude Code" };
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
    private readonly Label codexSetup = new() { AutoSize = false, UseCompatibleTextRendering = false, AccessibleName = "Codex setup status" };
    private readonly Label claudeSetup = new() { AutoSize = false, UseCompatibleTextRendering = false, AccessibleName = "Claude Code setup status" };
    private readonly Button checkAgain = Palette.Button("Retry", "Retry provider checks");
    private readonly Label retryMessage = new() { AutoSize = false };
    private readonly System.Windows.Forms.Timer countdown = new() { Interval = 1000 };
    private readonly ContextMenuStrip actions = new();
    private readonly ToolTip hints = new();
    private readonly Dictionary<string, ProviderCard> cards = new();
    private readonly Font headingFont = new("Segoe UI Semibold", 13, FontStyle.Regular);
    private readonly Font bodyFont = new("Segoe UI", 10);
    private readonly Font navigationFont = new("Segoe UI Semibold", 10, FontStyle.Regular);
    private Icon headerIdentity = AppIcon.Load();
    private IReadOnlyList<ProviderState> lastStates = [];
    private bool loading, logFailed, rendering, syncing, settingsShown, aboutShown;
    private enum SettingsError { Preferences, Position, Startup }
    private SettingsError? settingsError;
    private Preferences preferences = new();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool AllowExit { get; set; }
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
        header.Paint += (_, e) =>
        {
            using var nav = DrawingHelpers.RoundedRectangle(new RectangleF(S(15), S(55), S(306), S(36)), S(10));
            using var fill = new SolidBrush(Palette.Secondary); e.Graphics.FillPath(fill, nav);
            e.Graphics.DrawIcon(headerIdentity, new Rectangle(S(18), S(14), S(24), S(24)));
        };
        Controls.AddRange([header, content, settings, about, footer]);
        foreach (var name in names) { var card = new ProviderCard(hints); cards[name] = card; content.Controls.Add(card); }
        content.Controls.AddRange([offMessage, offSettings]);
        appearance.Items.AddRange(["System", "Light", "Dark"]);
        settings.Controls.AddRange([preferencesHeading, launch, compact, tray, appearanceLabel, appearance, settingsMessage, resetPosition,
            providerHeading, codex, claude, codexArtwork, claudeArtwork, codexSetup, claudeSetup, checkAgain, retryMessage]);
        foreach (var tab in new[] { usageTab, settingsTab, aboutTab }.OfType<RoundedButton>()) { tab.Navigation = true; tab.Font = navigationFont; }
        codex.TabIndex = 0; claude.TabIndex = 1; checkAgain.TabIndex = 2;
        launch.TabIndex = 3; compact.TabIndex = 4; tray.TabIndex = 5; appearance.TabIndex = 6; resetPosition.TabIndex = 7;
        codexArtwork.TabStop = claudeArtwork.TabStop = false;
        preferencesHeading.Font = providerHeading.Font = headingFont;
        checkAgain.Click += (_, _) => RefreshRequested?.Invoke();
        resetPosition.Click += (_, _) => ResetPositionRequested?.Invoke();
        offSettings.Click += (_, _) => ShowSettings();
        launch.Location = new(S(20), S(24)); compact.Location = new(S(20), S(68)); tray.Location = new(S(20), S(112));
        appearanceLabel.Location = new(S(20), S(164)); appearance.SetBounds(S(20), S(194), S(200), S(30));
        usageTab.Click += (_, _) => ShowUsage(); settingsTab.Click += (_, _) => ShowSettings(); aboutTab.Click += (_, _) => ShowAbout();
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        actions.Items.Add("Open Llumi", null, (_, _) => ShowUsage());
        actions.Items.Add("Refresh", null, (_, _) => RefreshRequested?.Invoke());
        actions.Items.Add("Settings", null, (_, _) => ShowSettings());
        actions.Items.Add("About Llumi", null, (_, _) => ShowAbout());
        actions.Items.Add("Quit", null, (_, _) => ExitRequested?.Invoke());
        menu.Click += (_, _) => { MenuOpening?.Invoke(); actions.Show(menu, new Point(0, menu.Height)); };
        launch.CheckedChanged += (_, _) => { if (!syncing && launch.Enabled) StartupToggleRequested?.Invoke(); };
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
    internal void PreferenceSaveFailed() => ShowSettingsMessage(SettingsError.Preferences, "Settings could not be saved. The previous preferences remain active.");
    internal void PositionResetFailed() => ShowSettingsMessage(SettingsError.Position, "Monitor position could not be saved. Try again.");
    internal void PreferenceSaveSucceeded() => ClearSettingsMessage(SettingsError.Preferences);
    internal void PositionResetSucceeded() => ClearSettingsMessage(SettingsError.Position);
    internal void StartupChangeResult(bool saved, bool available)
    {
        if (saved && available) ClearSettingsMessage(SettingsError.Startup);
        else ShowSettingsMessage(SettingsError.Startup, !available
            ? "Startup setting is unavailable. Please try again."
            : "Startup setting could not be saved. Please try again.");
    }
    private void ClearSettingsMessage(SettingsError recovered)
    {
        if (settingsError != recovered) return;
        settingsError = null;
        settingsMessage.Text = "";
        Render(lastStates, loading, logFailed);
    }
    private void ShowSettingsMessage(SettingsError error, string message)
    {
        settingsError = error;
        settingsMessage.Text = message;
        Render(lastStates, loading, logFailed);
        if (settingsShown && settingsMessage.Visible) settings.ScrollControlIntoView(settingsMessage);
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
        retryMessage.ForeColor = offMessage.ForeColor = Palette.Muted;
        foreach (var status in new[] { codexSetup, claudeSetup }) { status.BackColor = Palette.Card; status.Padding = new Padding(0); status.ForeColor = Palette.Muted; }
        foreach (var control in new Control[] { codex, claude, codexArtwork, claudeArtwork, launch, compact, tray, appearanceLabel, appearance }) control.BackColor = Palette.Card;
        foreach (var button in new[] { usageTab, settingsTab, aboutTab, checkAgain, resetPosition, offSettings })
        { if (button != usageTab && button != settingsTab && button != aboutTab) button.BackColor = Palette.Card; Palette.StyleButton(button); }
        foreach (var tab in new[] { usageTab, settingsTab, aboutTab }) tab.BackColor = Palette.Secondary;
        refresh.BackColor = Palette.Card; menu.BackColor = Palette.Background;
        appearance.FlatStyle = FlatStyle.Flat;
        about.ApplyTheme(); Render(lastStates, loading, logFailed); Invalidate(true);
    }
    internal void SetStartupState(bool enabled, bool available) { syncing = true; launch.Checked = enabled; launch.Enabled = available; syncing = false; }
    internal void ShowUsage() { settingsShown = aboutShown = false; Render(lastStates, loading, logFailed); }
    internal void ShowSettings() { aboutShown = false; settingsShown = true; MenuOpening?.Invoke(); Render(lastStates, loading, logFailed); }
    internal void ShowAbout() { settingsShown = false; aboutShown = true; about.ShowOverview(); Render(lastStates, loading, logFailed); }
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
            usageTab.SetBounds(S(18), S(57), S(99), S(32)); settingsTab.SetBounds(S(119), S(57), S(99), S(32));
            aboutTab.SetBounds(S(220), S(57), S(99), S(32));
            ((RoundedButton)usageTab).Selected = !settingsShown && !aboutShown;
            ((RoundedButton)settingsTab).Selected = settingsShown; ((RoundedButton)aboutTab).Selected = aboutShown;
            usageTab.AccessibleDescription = !settingsShown && !aboutShown ? "Current page" : "Open Usage";
            settingsTab.AccessibleDescription = settingsShown ? "Current page" : "Open Settings";
            aboutTab.AccessibleDescription = aboutShown ? "Current page" : "Open About";
            menu.SetBounds(width - S(46), S(12), S(28), S(28)); refresh.SetBounds(width - S(176), S(12), S(120), S(30));
            content.SetBounds(S(12), S(105), width - S(24), Math.Max(S(60), height - S(logFailed ? 150 : 117)));
            settings.Bounds = about.Bounds = content.Bounds; settings.Visible = settingsShown; about.Visible = aboutShown; content.Visible = !settingsShown && !aboutShown;
            var settingsScroll = settings.AutoScrollPosition;
            settings.AutoScrollPosition = Point.Empty;
            var settingsWidth = Math.Max(S(240), settings.ClientSize.Width - S(40) - SystemInformation.VerticalScrollBarWidth);
            providerHeading.Location = new(S(20), S(12));
            var twoColumns = settingsWidth >= S(548);
            var setupWidth = twoColumns ? (settingsWidth - S(12)) / 2 : settingsWidth;
            void SetSetupStatus(Label label, string name)
            {
                var state = states.FirstOrDefault(s => s.Name == name || (name == "Claude" && s.Name == "Claude Code"))
                    ?? new ProviderState(name, ProviderStatus.Loading);
                var enabled = name == "Codex" ? preferences.CodexEnabled : preferences.ClaudeEnabled;
                state = state with { Enabled = enabled };
                var value = SetupDiagnostic.From(state);
                label.Text = $"{value.Status}\n{value.Monitoring}";
                label.AccessibleDescription = value.Description + (state.Enabled && state.Snapshot is not null
                    ? "\n" + UsageAccessibility.Observation(state, DateTimeOffset.UtcNow) : "");
            }
            SetSetupStatus(codexSetup, "Codex"); SetSetupStatus(claudeSetup, "Claude");
            var narrowProvider = setupWidth < S(268);
            var statusY = narrowProvider ? 88 : 58;
            var statusWidth = setupWidth - S(28);
            using var settingsGraphics = settings.CreateGraphics();
            int MeasureStatus(Label label) => TextRenderer.MeasureText(settingsGraphics, label.Text, label.Font,
                new Size(statusWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            var statusHeight = Math.Max(MeasureStatus(codexSetup), MeasureStatus(claudeSetup));
            // Measure actual wrapped text instead of keeping blank padding beneath every status.
            var providerHeight = statusY + (int)Math.Ceiling(statusHeight * 96d / DeviceDpi) + 16;
            var cardY = 54; var claudeY = twoColumns ? cardY : cardY + providerHeight + 12;
            var claudeX = twoColumns ? S(32) + setupWidth : S(20);
            codexArtwork.SetBounds(S(32), S(cardY + 18), S(24), S(24));
            codex.SetBounds(S(66), S(cardY + 13), setupWidth - S(60), S(34));
            claudeArtwork.SetBounds(claudeX + S(12), S(claudeY + 18), S(24), S(24));
            claude.SetBounds(claudeX + S(46), S(claudeY + 13), setupWidth - S(60), S(34));
            // A narrow single column puts the mascot above the switch, preserving the full label.
            if (narrowProvider)
            {
                codex.SetBounds(S(32), S(cardY + 48), setupWidth - S(24), S(34));
                claude.SetBounds(claudeX + S(12), S(claudeY + 48), setupWidth - S(24), S(34));
            }
            codexSetup.SetBounds(S(34), S(cardY + statusY), statusWidth, statusHeight);
            claudeSetup.SetBounds(claudeX + S(14), S(claudeY + statusY), statusWidth, statusHeight);
            var actionsY = claudeY + providerHeight + 16;
            checkAgain.SetBounds(S(20), S(actionsY), S(112), S(36));
            UpdateRetry(); var hasRetry = !string.IsNullOrEmpty(retryMessage.Text); retryMessage.Visible = hasRetry;
            retryMessage.SetBounds(S(20), S(actionsY + 45), settingsWidth, S(hasRetry ? 42 : 0));
            var preferencesY = actionsY + (hasRetry ? 106 : 64);
            preferencesHeading.Location = new(S(20), S(preferencesY));
            var preferenceWidth = settingsWidth - S(28);
            launch.SetBounds(S(34), S(preferencesY + 52), preferenceWidth, S(34));
            compact.SetBounds(S(34), S(preferencesY + 96), preferenceWidth, S(34));
            tray.SetBounds(S(34), S(preferencesY + 140), preferenceWidth, S(34));
            var stackedAppearance = settingsWidth < S(380);
            appearanceLabel.Location = new(S(36), S(preferencesY + 190));
            appearance.SetBounds(stackedAppearance ? S(36) : S(20) + settingsWidth - S(214), S(preferencesY + (stackedAppearance ? 220 : 186)), Math.Min(S(198), preferenceWidth), S(30));
            var preferencesHeight = stackedAppearance ? 230 : 200;
            resetPosition.SetBounds(S(20), S(preferencesY + preferencesHeight + 56), S(145), S(36));
            settings.SetSurfaces(new Rectangle(S(20), S(cardY), setupWidth, S(providerHeight)), new Rectangle(claudeX, S(claudeY), setupWidth, S(providerHeight)),
                new Rectangle(S(20), S(preferencesY + 36), settingsWidth, S(preferencesHeight)));
            settingsMessage.Visible = !string.IsNullOrEmpty(settingsMessage.Text);
            settingsMessage.SetBounds(S(20), S(preferencesY + preferencesHeight + 104), settingsWidth, S(settingsMessage.Visible ? 100 : 0));
            settings.AutoScrollMinSize = new(0, S(preferencesY + preferencesHeight + (settingsMessage.Visible ? 216 : 106)));
            settings.AutoScrollPosition = new(0, -settingsScroll.Y);
            UpdateRetry();
            refresh.Visible = !settingsShown && !aboutShown; refresh.Text = loading ? "Refreshing…" : "Refresh";
            footer.SetBounds(S(24), height - S(39), width - S(48), S(30));
            footer.Visible = logFailed;
            footer.Text = logFailed ? "Diagnostic log unavailable" : string.Empty;
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
        checkAgain.Enabled = active.Any(s => SetupRetryPresentation.CanRetry(s, now));
        checkAgain.Text = !checkAgain.Enabled && active.Any(s => s.Status == ProviderStatus.Loading) ? "Checking…" : "Retry";
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
        if (disposing) { headerIdentity.Dispose(); countdown.Dispose(); actions.Dispose(); hints.Dispose(); headingFont.Dispose(); bodyFont.Dispose(); navigationFont.Dispose(); }
    }
}
