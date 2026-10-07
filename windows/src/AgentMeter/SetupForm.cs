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
    private readonly FlowLayoutPanel body = new() { Name = "setupViewport", Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly FlowLayoutPanel page = new() { Name = "setupPage", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    private readonly FlowLayoutPanel content = new() { Name = "setupContent", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    private readonly FlowLayoutPanel navigation = new() { Name = "setupNavigation", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
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
    private bool fitting;
    private bool rendering;

    internal SetupForm(SetupFlow flow, Func<IReadOnlyList<ProviderState>> states, Action<string?> refresh,
        Func<Preferences> preferences, Action<Preferences> savePreferences, IStartupRegistration startup,
        Action toggleStartup, Action finished)
    {
        SuspendLayout();
        this.flow = flow; this.states = states; this.refresh = refresh; this.preferences = preferences;
        this.savePreferences = savePreferences; this.startup = startup; this.toggleStartup = toggleStartup;
        this.finished = finished;
        var current = preferences(); flow.Codex = current.CodexEnabled; flow.Claude = current.ClaudeEnabled;
        message.Margin = retryMessage.Margin = Padding.Empty;
        Text = "Setup Llumi"; Font = bodyFont; Icon = AppIcon.Load();
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(620, 580);
        MinimumSize = new Size(560, 480); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(body); Controls.Add(welcome); body.Controls.Add(page); page.Controls.Add(content); page.Controls.Add(navigation);
        welcome.Controls.Add(welcomeGroup); welcomeGroup.Controls.Add(welcomeLogo); welcomeGroup.Controls.Add(welcomeName);
        welcomeName.Font = welcomeFont;
        welcomeLogo.Paint += (_, e) => { using var mark = AppIcon.Load(welcomeLogo.Width); e.Graphics.DrawIcon(mark, welcomeLogo.ClientRectangle); };
        welcome.SizeChanged += (_, _) => LayoutWelcome();
        welcomeName.SizeChanged += (_, _) => LayoutWelcome();
        next.AutoSize = back.AutoSize = true; next.AutoSizeMode = back.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        next.MinimumSize = back.MinimumSize = new Size(112, 38); next.Margin = Padding.Empty; back.Margin = new Padding(0, 0, 12, 0);
        navigation.Controls.Add(next); navigation.Controls.Add(back);
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
        content.SizeChanged += (_, _) => FitBodyContent();
        AcceptButton = next; RenderStep(); ResumeLayout(true); initialized = true; FitBodyContent(); LayoutWelcome();
        Load += (_, _) => FitInitialWindow();
        Shown += (_, _) => FocusPageStart();
    }
    private int S(int value) => initialized ? (int)Math.Round(value * DeviceDpi / 96d) : value;
    private void TextLine(string text, bool heading = false, int spacing = 12, FlowLayoutPanel? parent = null)
    {
        (parent ?? content).Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(S(520), 0),
            Font = heading ? headingFont : bodyFont, Margin = new Padding(0, 0, 0, S(heading ? 16 : spacing)) });
    }
    private FlowLayoutPanel Card(int minimumHeight = 0)
    {
        var card = new FlowLayoutPanel { Name = "setupCard", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(S(18)),
            Margin = new Padding(0, 0, 0, S(14)), MinimumSize = new Size(0, S(minimumHeight)) };
        content.Controls.Add(card); return card;
    }
    private Button ActionButton(string title, Action action, FlowLayoutPanel? parent = null)
    {
        var button = Palette.Button(title, title); button.AutoSize = true; button.MinimumSize = new Size(S(112), S(36));
        button.Margin = parent is null ? new Padding(0, 0, 0, S(8)) : new Padding(0, 0, S(12), 0);
        button.Click += (_, _) => action(); (parent ?? content).Controls.Add(button); return button;
    }
    private void Command(string title, string command)
    {
        var card = Card(96);
        TextLine(title, spacing: 12, parent: card);
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        var box = new TextBox { Text = command, ReadOnly = true, Width = S(370), AccessibleName = title, ShortcutsEnabled = true, Margin = new Padding(0, S(4), S(12), 0) };
        var copy = Palette.Button("Copy", "Copy " + title + " command"); copy.AutoSize = true; copy.MinimumSize = new Size(S(72), S(32)); copy.Margin = Padding.Empty;
        copy.Click += (_, _) => Copy(command); row.Controls.Add(box); row.Controls.Add(copy); card.Controls.Add(row);
        commandRows.Add((row, box, copy));
    }
    private void Copy(string text)
    {
        try { Clipboard.SetText(text); Feedback("Copied."); }
        catch (ExternalException) { Feedback("Clipboard is busy. Please try again."); }
    }
    private void Feedback(string text) { message.Text = text; message.Visible = !string.IsNullOrEmpty(text); FitBodyContent(); }
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
            Checked = provider == "Codex" ? current.CodexEnabled : current.ClaudeEnabled, Margin = new Padding(0, S(8), 0, 0) };
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
    private void ProviderHeading(string provider, bool switches, FlowLayoutPanel parent, int spacing = 10)
    {
        var heading = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, S(spacing)) };
        var artwork = new ProviderArtwork(provider) { Size = new Size(S(36), S(36)), Margin = new Padding(0, 0, S(16), 0) };
        heading.Controls.Add(artwork);
        if (switches) ProviderSwitch(provider, heading);
        else heading.Controls.Add(new Label { Text = Title(provider), AutoSize = true, Font = bodyFont, Margin = new Padding(0, 3, 0, 0) });
        parent.Controls.Add(heading);
        if (switches)
        {
            void Toggle(object? sender, EventArgs e) => providerSwitches[provider].Checked = !providerSwitches[provider].Checked;
            parent.Cursor = heading.Cursor = artwork.Cursor = Cursors.Hand;
            parent.Click += Toggle; heading.Click += Toggle; artwork.Click += Toggle;
        }
    }
    private void ProviderChoices()
    {
        foreach (var provider in new[] { "Codex", "Claude" }) ProviderHeading(provider, true, Card(88), 0);
    }
    private void Statuses(bool switches = false)
    {
        foreach (var provider in new[] { "Codex", "Claude" })
        {
            var card = Card(); ProviderHeading(provider, switches, card);
            var label = new Label { AutoSize = true, MaximumSize = new Size(S(484), 0), Margin = Padding.Empty, Cursor = Cursors.Default, AccessibleName = Title(provider) + " setup status" };
            statusLabels[provider] = label; card.Controls.Add(label);
        }
        var actions = new FlowLayoutPanel { Name = "setupActions", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, MaximumSize = new Size(S(520), 0), Margin = new Padding(0, 0, 0, S(12)) };
        retryButtons.Add(ActionButton("Retry", Retry, actions));
        ActionButton("Copy Diagnostics", () => Copy(SetupDiagnostics.Report(states(),
            typeof(SetupForm).Assembly.GetName().Version?.ToString(3), typeof(SetupForm).Assembly.GetName().Version?.ToString())), actions);
        content.Controls.Add(actions);
        content.Controls.Add(retryMessage);
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
        FitBodyContent();
    }
    private void RenderStep()
    {
        rendering = true;
        content.SuspendLayout(); content.Controls.Remove(message); content.Controls.Remove(retryMessage);
        foreach (var control in content.Controls.Cast<Control>().ToArray()) control.Dispose();
        content.Controls.Clear(); statusLabels.Clear(); retryButtons.Clear(); providerSwitches.Clear(); commandRows.Clear(); message.Text = ""; message.Visible = false; retryMessage.Text = "";
        body.AutoScrollPosition = Point.Empty;
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
        Controls.SetChildIndex(body, 0);
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
                content.Controls.Add(help); break;
            case SetupStep.Verify:
                TextLine("Check Setup", true); Statuses(switches: true); break;
            case SetupStep.Preferences:
                TextLine("Preferences", true);
                var p = preferences();
                var compact = new CheckBox { Text = "Compact Monitor", Checked = p.CompactMonitor, AutoSize = true };
                var tray = new CheckBox { Text = "Tray Icon", Checked = p.TrayIcon, AutoSize = true };
                var available = startup.TryRead(out var enabled);
                var launch = new CheckBox { Text = "Launch at Startup", Checked = enabled, Enabled = available, AutoSize = true };
                void ChangeStartup()
                {
                    if (!launch.Enabled) return;
                    var requested = launch.Checked;
                    toggleStartup();
                    launch.Enabled = startup.TryRead(out var actual);
                    launch.Checked = actual;
                    Feedback(!launch.Enabled ? "Startup setting is unavailable. Please try again." :
                        actual == requested ? "" : "Startup setting could not be saved. Please try again.");
                }
                launch.Click += (_, _) => ChangeStartup();
                var appearance = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Appearance", Width = 200 };
                appearance.Items.AddRange(["System", "Light", "Dark"]); appearance.SelectedIndex = (int)p.Appearance;
                void Restore(Preferences actual)
                {
                    compact.Checked = actual.CompactMonitor; tray.Checked = actual.TrayIcon; appearance.SelectedIndex = (int)actual.Appearance;
                }
                compact.CheckedChanged += (_, _) => SavePreference(preferences() with { CompactMonitor = compact.Checked }, Restore);
                tray.CheckedChanged += (_, _) => SavePreference(preferences() with { TrayIcon = tray.Checked }, Restore);
                appearance.SelectedIndexChanged += (_, _) => SavePreference(preferences() with { Appearance = (Appearance)appearance.SelectedIndex }, Restore);
                foreach (var setting in new[] { compact, tray, launch })
                {
                    setting.Margin = Padding.Empty;
                    var row = Card(56); row.Controls.Add(setting);
                    row.Cursor = Cursors.Hand;
                    row.Click += (_, _) =>
                    {
                        if (!setting.Enabled) return;
                        setting.Focus(); setting.Checked = !setting.Checked;
                        if (setting == launch) ChangeStartup();
                    };
                }
                var appearanceRow = Card(66); appearanceRow.FlowDirection = FlowDirection.LeftToRight;
                appearanceRow.Controls.Add(new Label { Text = "Appearance", AutoSize = true, Margin = new Padding(0, S(5), S(24), 0) });
                appearance.Margin = Padding.Empty; appearance.Width = S(180); appearanceRow.Controls.Add(appearance); break;
            case SetupStep.Done:
                var finishLogo = new Panel { Name = "finishLogo", Height = S(96), Margin = Padding.Empty, AccessibleName = "Llumi logo" };
                finishLogo.Paint += (_, e) => { using var mark = AppIcon.Load(S(72)); e.Graphics.DrawIcon(mark, new Rectangle((finishLogo.Width - S(72)) / 2, 0, S(72), S(72))); };
                content.Controls.Add(finishLogo);
                content.Controls.Add(new Label { Name = "finishHeading", Text = "You’re all set", AutoSize = true, Font = headingFont, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty }); break;
        }
        content.Controls.Add(message); RefreshStatuses(); ApplyTheme(); content.ResumeLayout(true); rendering = false;
        PerformLayout(); FitBodyContent(); LayoutWelcome();
        AcceptButton = next;
        if (Visible) FocusPageStart();
    }
    private void FocusPageStart()
    {
        var first = flow.Step == SetupStep.Welcome ? next : Descendants(content).FirstOrDefault(c => c.Visible && c.Enabled && c.TabStop && c.CanSelect) ?? next;
        first.Select();
        body.AutoScrollPosition = Point.Empty;
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
        if (!initialized || fitting || rendering || flow.Step == SetupStep.Welcome) return;
        fitting = true;
        try
        {
            // Reserve a scrollbar gutter consistently so its appearance cannot make
            // wrapping/height alternate between two layouts at the same window size.
            var width = Math.Max(S(240), Math.Min(S(520), body.ClientSize.Width - S(48) - SystemInformation.VerticalScrollBarWidth));
            foreach (var stack in new[] { page, content, navigation })
            {
                stack.MinimumSize = new Size(width, 0); stack.MaximumSize = new Size(width, 0); stack.Width = width;
            }
            navigation.Margin = new Padding(0, S(24), 0, 0);
            var actionsWidth = next.GetPreferredSize(Size.Empty).Width + back.GetPreferredSize(Size.Empty).Width + back.Margin.Horizontal;
            navigation.Padding = flow.Step == SetupStep.Done ? new Padding(0, 0, Math.Max(0, (width - actionsWidth) / 2), 0) : Padding.Empty;
            foreach (var card in content.Controls.OfType<FlowLayoutPanel>().Where(p => p.Name == "setupCard"))
            {
                card.MinimumSize = new Size(width, card.MinimumSize.Height); card.MaximumSize = new Size(width, 0); card.Width = width;
            }
            foreach (var label in Descendants(content).OfType<Label>().Where(l => l.AutoSize && l.MaximumSize.Width > 0))
                label.MaximumSize = new Size(label.Parent == content ? width : Math.Max(S(100), width - S(36)), 0);
            foreach (var actions in content.Controls.OfType<FlowLayoutPanel>().Where(p => p.Name == "setupActions")) actions.MaximumSize = new Size(width, 0);
            foreach (var (row, box, copy) in commandRows)
            {
                var innerWidth = width - S(36);
                row.MaximumSize = new Size(innerWidth, 0); box.Width = Math.Max(S(100), innerWidth - copy.GetPreferredSize(Size.Empty).Width - box.Margin.Horizontal);
            }
            foreach (var centered in content.Controls.Cast<Control>().Where(c => c.Name is "finishLogo" or "finishHeading"))
            { centered.MinimumSize = new Size(width, centered.Name == "finishLogo" ? S(96) : 0); centered.MaximumSize = new Size(width, 0); centered.Width = width; }
            content.PerformLayout(); navigation.PerformLayout(); page.PerformLayout();
            var height = page.GetPreferredSize(new Size(width, 0)).Height;
            var top = Math.Max(S(24), (body.ClientSize.Height - height) / 2);
            var left = Math.Max(S(24), (body.ClientSize.Width - width - (body.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0)) / 2);
            var padding = new Padding(left, top, S(24), S(24));
            if (body.Padding != padding) body.Padding = padding;
            // Include both gutters in the scroll extent, including when nested
            // auto-sized content grows after status or save feedback changes.
            body.AutoScrollMinSize = new Size(0, top + height + S(24));
            body.PerformLayout();
        }
        finally { fitting = false; }
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
        void Theme(Control root, Color background) {
            if (root.Name == "setupCard") background = Palette.IsLight ? Color.White : Palette.Card;
            root.BackColor = background; root.ForeColor = Palette.Foreground;
            if (root is Button button) { button.BackColor = Palette.Card; Palette.StyleButton(button); }
            if (root is LinkLabel link) { link.LinkColor = Palette.IsLight ? Color.FromArgb(0, 103, 192) : Palette.Accent;
                link.ActiveLinkColor = link.VisitedLinkColor = link.LinkColor; }
            foreach (Control child in root.Controls) Theme(child, background); }
        Theme(this, Palette.Background);
        next.BackColor = Color.FromArgb(0, 103, 192); next.ForeColor = Color.White;
        next.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 86, 160);
        next.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 70, 130);
    }
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); if (disposing) { countdown.Dispose(); retryMessage.Dispose(); Icon?.Dispose(); bodyFont.Dispose(); headingFont.Dispose(); welcomeFont.Dispose(); } }
}
