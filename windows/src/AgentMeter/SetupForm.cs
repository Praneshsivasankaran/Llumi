using System.Runtime.InteropServices;
using AgentMeter.Core;

namespace AgentMeter;

// Presentation only: all checks, preferences and startup changes delegate to TrayContext.
internal sealed class SetupForm : Form
{
    private readonly SetupFlow flow;
    private readonly Func<IReadOnlyList<ProviderState>> states;
    private readonly Action<string?> refresh;
    private readonly Func<Preferences> preferences;
    private readonly Action<Preferences> savePreferences;
    private readonly IStartupRegistration startup;
    private readonly Action toggleStartup;
    private readonly Action finished;
    private readonly FlowLayoutPanel body = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24, 16, 24, 16) };
    private readonly FlowLayoutPanel navigation = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
    private readonly Panel welcome = new() { Dock = DockStyle.Fill, Name = "welcome", Visible = false };
    private readonly Panel welcomeGroup = new() { Name = "welcomeGroup" };
    private readonly Panel welcomeLogo = new() { Name = "welcomeLogo", AccessibleName = "Llumi logo" };
    private readonly Label welcomeName = new() { Text = "Llumi", AutoSize = true, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button next = Palette.Button("Continue", "Continue");
    private readonly Button back = Palette.Button("Back", "Back");
    private readonly Dictionary<string, Label> statusLabels = new();
    private readonly List<Button> retryButtons = [];
    private readonly Dictionary<string, CheckBox> providerSwitches = new();
    private readonly List<(FlowLayoutPanel Row, TextBox Box, Button Copy)> commandRows = [];
    private readonly Label retryMessage = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly System.Windows.Forms.Timer countdown = new() { Interval = 1000 };
    private readonly Label message = new() { AutoSize = true, MaximumSize = new Size(520, 0), Visible = false };
    private readonly Font bodyFont = new("Segoe UI", 10);
    private readonly Font headingFont = new("Segoe UI", 16, FontStyle.Bold);
    private readonly Font welcomeFont = new("Segoe UI", 24, FontStyle.Bold);
    private bool syncingProviders;
    private bool syncingPreferences;
    private bool initialized;

    internal SetupForm(SetupFlow flow, Func<IReadOnlyList<ProviderState>> states, Action<string?> refresh,
        Func<Preferences> preferences, Action<Preferences> savePreferences, IStartupRegistration startup,
        Action toggleStartup, Action finished)
    {
        SuspendLayout();
        this.flow = flow; this.states = states; this.refresh = refresh; this.preferences = preferences;
        this.savePreferences = savePreferences; this.startup = startup; this.toggleStartup = toggleStartup;
        this.finished = finished;
        var current = preferences(); flow.Codex = current.CodexEnabled; flow.Claude = current.ClaudeEnabled;
        Text = "Setup Llumi"; Font = bodyFont; Icon = AppIcon.Load();
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(620, 580);
        MinimumSize = new Size(560, 480); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(body); Controls.Add(navigation); Controls.Add(welcome);
        welcome.Controls.Add(welcomeGroup); welcomeGroup.Controls.Add(welcomeLogo); welcomeGroup.Controls.Add(welcomeName);
        welcomeName.Font = welcomeFont;
        welcomeLogo.Paint += (_, e) => { using var mark = AppIcon.Load(welcomeLogo.Width); e.Graphics.DrawIcon(mark, welcomeLogo.ClientRectangle); };
        welcome.SizeChanged += (_, _) => LayoutWelcome();
        welcomeName.SizeChanged += (_, _) => LayoutWelcome();
        next.AutoSize = back.AutoSize = true; next.AutoSizeMode = back.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        next.MinimumSize = back.MinimumSize = new Size(112, 38); navigation.Controls.Add(next); navigation.Controls.Add(back);
        back.Click += (_, _) => { flow.Back(); RenderStep(); };
        next.Click += (_, _) =>
        {
            if (flow.Step == SetupStep.Done)
            {
                if (!flow.Complete()) { Feedback("Setup completion could not be saved. Please try again."); return; }
                finished(); Close(); return;
            }
            flow.Next(); RenderStep(); if (flow.Step == SetupStep.Verify) refresh(null);
        };
        countdown.Tick += (_, _) => RefreshStatuses();
        VisibleChanged += (_, _) => { if (Visible) countdown.Start(); else countdown.Stop(); };
        body.SizeChanged += (_, _) => FitBodyContent();
        AcceptButton = next; RenderStep(); ResumeLayout(true); initialized = true; FitBodyContent(); LayoutWelcome();
        Load += (_, _) => FitInitialWindow();
        Shown += (_, _) => next.Select();
    }
    private void TextLine(string text, bool heading = false, int spacing = 8)
    {
        body.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0),
            Font = heading ? headingFont : bodyFont, Margin = new Padding(0, 0, 0, spacing) });
    }
    private Button ActionButton(string title, Action action, FlowLayoutPanel? parent = null)
    {
        var button = Palette.Button(title, title); button.AutoSize = true; button.MinimumSize = new Size(112, 36);
        button.Margin = parent is null ? new Padding(0, 0, 0, 8) : new Padding(0, 0, 12, 0);
        button.Click += (_, _) => action(); (parent ?? body).Controls.Add(button); return button;
    }
    private void Command(string title, string command)
    {
        TextLine(title, spacing: 2);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        var box = new TextBox { Text = command, ReadOnly = true, Width = 414, AccessibleName = title, ShortcutsEnabled = true, Margin = new Padding(0, 4, 12, 0) };
        var copy = Palette.Button("Copy", "Copy " + title + " command"); copy.AutoSize = true; copy.MinimumSize = new Size(90, 32); copy.Margin = Padding.Empty;
        copy.Click += (_, _) => Copy(command); row.Controls.Add(box); row.Controls.Add(copy); body.Controls.Add(row);
        commandRows.Add((row, box, copy));
    }
    private void Copy(string text)
    {
        try { Clipboard.SetText(text); Feedback("Copied."); }
        catch (ExternalException) { Feedback("Clipboard is busy. Please try again."); }
    }
    private void Feedback(string text) { message.Text = text; message.Visible = !string.IsNullOrEmpty(text); body.PerformLayout(); }
    private void NextCaption(string text) { next.Text = text; next.AccessibleName = text; }
    private void SavePreference(Preferences requested, Action<Preferences> restore)
    {
        if (syncingPreferences) return;
        savePreferences(requested);
        var actual = preferences();
        syncingPreferences = true;
        try { restore(actual); }
        finally { syncingPreferences = false; }
        Feedback(actual == requested ? "" : "Settings could not be saved. Please try again.");
        ApplyTheme();
    }
    private static string Title(string provider) => provider == "Claude" ? "Claude Code" : provider;
    private ProviderState StateFor(string provider, IReadOnlyList<ProviderState> source, Preferences current)
    {
        var state = source.FirstOrDefault(s => s.Name == provider || provider == "Claude" && UsagePresentation.IsClaude(s.Name))
            ?? new ProviderState(provider == "Claude" ? "Claude Code" : provider, ProviderStatus.Loading);
        return state with { Enabled = state.Enabled && (provider == "Codex" ? current.CodexEnabled : current.ClaudeEnabled) };
    }
    private ProviderState[] RelevantStates()
    {
        var source = states(); var current = preferences();
        return new[] { "Codex", "Claude" }.Select(p => StateFor(p, source, current)).ToArray();
    }
    private void Retry()
    {
        var relevant = RelevantStates();
        if (!relevant.Any(s => SetupRetryPresentation.CanRetry(s, DateTimeOffset.UtcNow))) return;
        refresh(null);
    }
    private void ProviderSwitch(string provider, FlowLayoutPanel row)
    {
        var current = preferences();
        var control = new CheckBox { Text = "Monitor " + Title(provider), AutoSize = true, AccessibleName = "Monitor " + Title(provider),
            Checked = provider == "Codex" ? current.CodexEnabled : current.ClaudeEnabled, Margin = new Padding(0, 3, 0, 0) };
        providerSwitches[provider] = control;
        control.CheckedChanged += (_, _) =>
        {
            if (syncingProviders) return;
            var requested = control.Checked;
            savePreferences(provider == "Codex" ? preferences() with { CodexEnabled = requested } : preferences() with { ClaudeEnabled = requested });
            RefreshStatuses();
            if ((provider == "Codex" ? preferences().CodexEnabled : preferences().ClaudeEnabled) != requested)
                Feedback("Settings could not be saved. Please try again.");
            else Feedback("");
        };
        row.Controls.Add(control);
    }
    private void ProviderHeading(string provider, bool switches, int spacing = 6)
    {
        var heading = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, spacing) };
        heading.Controls.Add(new ProviderArtwork(provider) { Size = new Size(26, 26), Margin = new Padding(0, 0, 10, 0) });
        if (switches) ProviderSwitch(provider, heading);
        else heading.Controls.Add(new Label { Text = Title(provider), AutoSize = true, Font = bodyFont, Margin = new Padding(0, 3, 0, 0) });
        body.Controls.Add(heading);
    }
    private void ProviderChoices()
    {
        foreach (var provider in new[] { "Codex", "Claude" }) ProviderHeading(provider, true, 12);
    }
    private void Statuses(bool switches = false)
    {
        foreach (var provider in new[] { "Codex", "Claude" })
        {
            ProviderHeading(provider, switches);
            var label = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 8), AccessibleName = Title(provider) + " setup status" };
            statusLabels[provider] = label; body.Controls.Add(label);
        }
        var actions = new FlowLayoutPanel { Name = "setupActions", AutoSize = true, WrapContents = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 8) };
        retryButtons.Add(ActionButton("Retry", Retry, actions));
        ActionButton("Copy Diagnostics", () => Copy(SetupDiagnostics.Report(states(),
            typeof(SetupForm).Assembly.GetName().Version?.ToString(3), typeof(SetupForm).Assembly.GetName().Version?.ToString())), actions);
        body.Controls.Add(actions);
        body.Controls.Add(retryMessage);
    }
    internal void RefreshStatuses()
    {
        var current = preferences(); flow.Codex = current.CodexEnabled; flow.Claude = current.ClaudeEnabled;
        syncingProviders = true;
        foreach (var (provider, control) in providerSwitches)
        {
            control.Checked = provider == "Codex" ? current.CodexEnabled : current.ClaudeEnabled;
        }
        syncingProviders = false;
        if (statusLabels.Count == 0) return;
        foreach (var (name, label) in statusLabels)
        {
            var state = StateFor(name, states(), current);
            var diagnostic = SetupDiagnostic.From(state);
            label.Text = diagnostic.Summary;
            label.AccessibleDescription = diagnostic.Description + (state.Enabled && state.Snapshot is not null
                ? "\n" + UsageAccessibility.Observation(state, DateTimeOffset.UtcNow) : "");
        }
        var selected = RelevantStates().Where(s => s.Enabled).ToArray();
        var at = DateTimeOffset.UtcNow;
        foreach (var button in retryButtons) button.Enabled = selected.Any(s => SetupRetryPresentation.CanRetry(s, at));
        retryMessage.Text = string.Join("\n", selected.Select(s => (State: s, Message: SetupRetryPresentation.Message(s, at)))
            .Where(x => x.Message is not null).Select(x => $"{(UsagePresentation.IsClaude(x.State.Name) ? "Claude Code" : x.State.Name)}: {x.Message}"));
        retryMessage.Visible = !string.IsNullOrEmpty(retryMessage.Text);
        if (flow.Step == SetupStep.Verify)
            NextCaption(states().Any(s => ((flow.Codex && s.Name == "Codex") || (flow.Claude && s.Name is "Claude" or "Claude Code"))
                && s.Enabled && s.Authentication == AuthenticationStatus.Verified) ? "Continue" : "Finish Anyway");
    }
    private void RenderStep()
    {
        body.SuspendLayout(); body.Controls.Remove(message); body.Controls.Remove(retryMessage);
        foreach (var control in body.Controls.Cast<Control>().ToArray()) control.Dispose();
        body.Controls.Clear(); statusLabels.Clear(); retryButtons.Clear(); providerSwitches.Clear(); commandRows.Clear(); message.Text = ""; message.Visible = false; retryMessage.Text = "";
        var isWelcome = flow.Step == SetupStep.Welcome;
        welcome.Visible = isWelcome;
        if (isWelcome) { welcomeGroup.Controls.Add(next); next.AutoSize = false; }
        else
        {
            navigation.Controls.Add(next); navigation.Controls.SetChildIndex(next, 0);
            var scale = initialized ? DeviceDpi / 96d : 1;
            next.MinimumSize = new Size((int)Math.Round(112 * scale), (int)Math.Round(38 * scale));
            next.Size = next.MinimumSize; next.AutoSize = true;
        }
        body.Visible = navigation.Visible = !isWelcome;
        Controls.SetChildIndex(body, 0); Controls.SetChildIndex(navigation, 1);
        back.Visible = flow.Step != SetupStep.Welcome;
        NextCaption(flow.Step == SetupStep.Welcome ? "Get Started" : flow.Step == SetupStep.Done ? "Start Llumi" : "Continue");
        switch (flow.Step)
        {
            case SetupStep.Welcome:
                break;
            case SetupStep.Providers:
                TextLine("Choose your providers", true); TextLine("Choose either, both, or set them up later."); ProviderChoices(); break;
            case SetupStep.Codex: case SetupStep.Claude:
                var isCodex = flow.Step == SetupStep.Codex;
                TextLine(isCodex ? "Set up Codex" : "Set up Claude Code", true);
                TextLine("Run these in PowerShell.");
                Command("1. Install", isCodex ? "npm install -g @openai/codex" : "irm https://claude.ai/install.ps1 | iex");
                Command("2. Sign in", isCodex ? "codex login" : "claude auth login");
                var help = new LinkLabel { Text = "Setup help", AccessibleName = (isCodex ? "Codex" : "Claude Code") + " setup help",
                    AutoSize = true, TabStop = true, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 8, 0, 0) };
                help.LinkClicked += (_, _) => ProviderSetup.Open(new Uri(isCodex
                    ? "https://learn.chatgpt.com/docs/codex/cli" : "https://code.claude.com/docs/en/setup"));
                body.Controls.Add(help); break;
            case SetupStep.Verify:
                TextLine("Check Setup", true); Statuses(switches: true); break;
            case SetupStep.Preferences:
                TextLine("Preferences", true);
                var p = preferences();
                var compact = new CheckBox { Text = "Compact Monitor", Checked = p.CompactMonitor, AutoSize = true };
                var tray = new CheckBox { Text = "Tray Icon", Checked = p.TrayIcon, AutoSize = true };
                var available = startup.TryRead(out var enabled);
                var launch = new CheckBox { Text = "Launch at Startup", Checked = enabled, Enabled = available, AutoSize = true };
                launch.Click += (_, _) =>
                {
                    if (!launch.Enabled) return;
                    var requested = launch.Checked;
                    toggleStartup();
                    launch.Enabled = startup.TryRead(out var actual);
                    launch.Checked = actual;
                    Feedback(!launch.Enabled ? "Startup setting is unavailable. Please try again." :
                        actual == requested ? "" : "Startup setting could not be saved. Please try again.");
                };
                var appearance = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Appearance", Width = 200 };
                appearance.Items.AddRange(["System", "Light", "Dark"]); appearance.SelectedIndex = (int)p.Appearance;
                void Restore(Preferences actual)
                {
                    compact.Checked = actual.CompactMonitor; tray.Checked = actual.TrayIcon; appearance.SelectedIndex = (int)actual.Appearance;
                }
                compact.CheckedChanged += (_, _) => SavePreference(preferences() with { CompactMonitor = compact.Checked }, Restore);
                tray.CheckedChanged += (_, _) => SavePreference(preferences() with { TrayIcon = tray.Checked }, Restore);
                appearance.SelectedIndexChanged += (_, _) => SavePreference(preferences() with { Appearance = (Appearance)appearance.SelectedIndex }, Restore);
                body.Controls.Add(compact); body.Controls.Add(tray); body.Controls.Add(launch); TextLine("Appearance"); body.Controls.Add(appearance); break;
            case SetupStep.Done:
                TextLine("You’re all set", true); break;
        }
        body.Controls.Add(message); RefreshStatuses(); ApplyTheme(); body.ResumeLayout(true); PerformLayout(); FitBodyContent(); LayoutWelcome();
        AcceptButton = next;
        if (Visible) next.Select();
    }
    private void LayoutWelcome()
    {
        if (!initialized || flow.Step != SetupStep.Welcome) return;
        int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        var width = S(240);
        welcomeLogo.SetBounds((width - S(72)) / 2, 0, S(72), S(72));
        welcomeName.Location = new Point((width - welcomeName.Width) / 2, welcomeLogo.Bottom + S(20));
        next.MinimumSize = new Size(S(160), S(44));
        next.SetBounds((width - S(160)) / 2, welcomeName.Bottom + S(24), S(160), S(44));
        welcomeGroup.Size = new Size(width, next.Bottom);
        welcomeGroup.Location = new Point((welcome.ClientSize.Width - width) / 2, (welcome.ClientSize.Height - welcomeGroup.Height) / 2);
    }
    private void FitBodyContent()
    {
        if (!initialized) return;
        var width = Math.Max(200, Math.Min((int)Math.Round(520 * DeviceDpi / 96d), body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth));
        foreach (var label in Descendants(body).OfType<Label>().Where(l => l.AutoSize && l.MaximumSize.Width > 0)) label.MaximumSize = new Size(width, 0);
        foreach (var actions in body.Controls.OfType<FlowLayoutPanel>().Where(p => p.Name == "setupActions")) actions.MaximumSize = new Size(width, 0);
        foreach (var (row, box, copy) in commandRows) { row.MaximumSize = new Size(width, 0); box.Width = Math.Max(100, width - copy.Width - box.Margin.Horizontal); }
    }
    private void FitInitialWindow()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var margin = (int)Math.Round(16 * DeviceDpi / 96d);
        var available = new Size(Math.Max(1, area.Width - margin * 2), Math.Max(1, area.Height - margin * 2));
        MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
        Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        PerformLayout(); FitBodyContent(); LayoutWelcome();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitBodyContent(); LayoutWelcome();
    }
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    internal void ApplyTheme()
    {
        void Theme(Control root) { root.BackColor = Palette.Background; root.ForeColor = Palette.Foreground;
            if (root is Button button) { button.BackColor = Palette.Card; Palette.StyleButton(button); }
            if (root is LinkLabel link) { link.LinkColor = Palette.IsLight ? Color.FromArgb(0, 103, 192) : Palette.Accent;
                link.ActiveLinkColor = link.VisitedLinkColor = link.LinkColor; }
            foreach (Control child in root.Controls) Theme(child); }
        Theme(this);
        if (flow.Step == SetupStep.Welcome)
        {
            next.BackColor = Color.FromArgb(0, 103, 192); next.ForeColor = Color.White;
            next.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 86, 160);
            next.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 70, 130);
        }
    }
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); if (disposing) { countdown.Dispose(); retryMessage.Dispose(); Icon?.Dispose(); bodyFont.Dispose(); headingFont.Dispose(); welcomeFont.Dispose(); } }
}
