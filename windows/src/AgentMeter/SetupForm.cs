using System.Runtime.InteropServices;
using AgentMeter.Core;

namespace AgentMeter;

// Presentation only: all checks, preferences and startup changes delegate to TrayContext.
internal sealed class SetupForm : Form
{
    private readonly SetupFlow flow;
    private readonly Func<IReadOnlyList<ProviderState>> states;
    private readonly Action refresh;
    private readonly Func<Preferences> preferences;
    private readonly Action<Preferences> savePreferences;
    private readonly IStartupRegistration startup;
    private readonly Action toggleStartup;
    private readonly Action finished;
    private readonly FlowLayoutPanel body = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
    private readonly FlowLayoutPanel navigation = new() { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
    private readonly Button next = Palette.Button("Continue", "Continue setup");
    private readonly Button back = Palette.Button("Back", "Previous setup step");
    private readonly Dictionary<string, Label> statusLabels = new();
    private readonly Label message = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Font bodyFont = new("Segoe UI", 10);
    private readonly Font headingFont = new("Segoe UI", 16, FontStyle.Bold);

    internal SetupForm(SetupFlow flow, Func<IReadOnlyList<ProviderState>> states, Action refresh,
        Func<Preferences> preferences, Action<Preferences> savePreferences, IStartupRegistration startup,
        Action toggleStartup, Action finished)
    {
        this.flow = flow; this.states = states; this.refresh = refresh; this.preferences = preferences;
        this.savePreferences = savePreferences; this.startup = startup; this.toggleStartup = toggleStartup;
        this.finished = finished;
        Text = "Setup Llumi"; Font = bodyFont; Icon = AppIcon.Load();
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(620, 580);
        MinimumSize = new Size(560, 480); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(body); Controls.Add(navigation);
        next.AutoSize = back.AutoSize = true; next.MinimumSize = back.MinimumSize = new Size(112, 38); navigation.Controls.Add(next); navigation.Controls.Add(back);
        back.Click += (_, _) => { flow.Back(); RenderStep(); };
        next.Click += (_, _) =>
        {
            if (flow.Step == SetupStep.Done)
            {
                if (!flow.Complete()) { message.Text = "Setup completion could not be saved. Please try again."; return; }
                finished(); Close(); return;
            }
            flow.Next(); RenderStep(); if (flow.Step == SetupStep.Verify) refresh();
        };
        AcceptButton = next; RenderStep();
    }
    private void TextLine(string text, bool heading = false)
    {
        body.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0),
            Font = heading ? headingFont : bodyFont, Margin = new Padding(0, 0, 0, 16) });
    }
    private Button ActionButton(string title, Action action)
    {
        var button = Palette.Button(title, title); button.AutoSize = true; button.MinimumSize = new Size(112, 36); button.Margin = new Padding(0, 0, 0, 14);
        button.Click += (_, _) => action(); body.Controls.Add(button); return button;
    }
    private void Command(string title, string command)
    {
        TextLine(title);
        var box = new TextBox { Text = command, ReadOnly = true, Width = 510, AccessibleName = title, ShortcutsEnabled = true };
        body.Controls.Add(box);
        ActionButton("Copy " + title, () => Copy(command));
    }
    private void Copy(string text)
    {
        try { Clipboard.SetText(text); message.Text = "Copied."; }
        catch (ExternalException) { message.Text = "Clipboard is busy. Please try again."; }
    }
    private void Statuses()
    {
        foreach (var provider in new[] { "Codex", "Claude" })
        {
            TextLine(provider == "Claude" ? "Claude Code" : provider, true);
            var label = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 12) };
            statusLabels[provider] = label; body.Controls.Add(label);
        }
        ActionButton("Check Again", refresh);
        ActionButton("Copy Diagnostics", () => Copy(SetupDiagnostics.Report(states(),
            typeof(SetupForm).Assembly.GetName().Version?.ToString(3), typeof(SetupForm).Assembly.GetName().Version?.ToString())));
        TextLine("Copies only app/OS versions, architecture and status categories. Nothing is uploaded.");
    }
    internal void RefreshStatuses()
    {
        foreach (var (name, label) in statusLabels)
        {
            var state = states().FirstOrDefault(s => s.Name == name || (name == "Claude" && s.Name == "Claude Code"))
                ?? new ProviderState(name, ProviderStatus.Loading);
            label.Text = SetupDiagnostic.From(state).Summary;
        }
        if (flow.Step == SetupStep.Verify)
            next.Text = states().Any(s => ((flow.Codex && s.Name == "Codex") || (flow.Claude && s.Name is "Claude" or "Claude Code"))
                && SetupDiagnostic.From(s).Status == "Ready") ? "Continue" : "Finish Anyway";
    }
    private void RenderStep()
    {
        body.SuspendLayout(); body.Controls.Remove(message);
        foreach (var control in body.Controls.Cast<Control>().ToArray()) control.Dispose();
        body.Controls.Clear(); statusLabels.Clear(); message.Text = "";
        back.Visible = flow.Step != SetupStep.Welcome;
        next.Text = flow.Step == SetupStep.Welcome ? "Set Up Llumi" : flow.Step == SetupStep.Done ? "Start Llumi" : "Continue";
        switch (flow.Step)
        {
            case SetupStep.Welcome:
                var identity = new Panel { Width = 64, Height = 64, Margin = new Padding(0, 8, 0, 24) };
                identity.Paint += (_, e) => { using var mark = AppIcon.Load(); e.Graphics.DrawIcon(mark, new Rectangle(0, 0, 64, 64)); };
                body.Controls.Add(identity);
                TextLine("Welcome to Llumi", true); TextLine("Track your AI coding usage.");
                TextLine("Llumi monitors usage from your locally installed Codex and Claude Code tools. Use either provider, or both. Your sign-in stays with your provider."); break;
            case SetupStep.Providers:
                TextLine("Choose providers", true);
                var codex = new CheckBox { Text = "Codex", AutoSize = true, Checked = flow.Codex };
                var claude = new CheckBox { Text = "Claude Code", AutoSize = true, Checked = flow.Claude };
                codex.CheckedChanged += (_, _) => flow.Codex = codex.Checked;
                claude.CheckedChanged += (_, _) => flow.Claude = claude.Checked;
                body.Controls.Add(codex); body.Controls.Add(claude); Statuses(); break;
            case SetupStep.Codex: case SetupStep.Claude:
                var isCodex = flow.Step == SetupStep.Codex;
                TextLine(isCodex ? "Set up Codex" : "Set up Claude Code", true);
                TextLine("Open PowerShell. Copy each command, paste it there, then press Enter. Llumi never executes these commands or reads Terminal contents.");
                TextLine(isCodex ? "This installation method requires Node.js and npm. Skip installation if Codex is already installed. Use native Windows, not a WSL-only installation."
                    : "This is Anthropic’s native Windows installer. Skip installation if Claude Code is already installed.");
                Command("Install", isCodex ? "npm install -g @openai/codex" : "irm https://claude.ai/install.ps1 | iex");
                Command("Sign in", isCodex ? "codex login" : "claude auth login");
                TextLine(isCodex ? "Choose your ChatGPT subscription account." : "Choose your Claude subscription account, not Console/API billing. Credentials stay with Claude Code.");
                ActionButton("Official setup guide", () => ProviderSetup.Open(new Uri(isCodex
                    ? "https://developers.openai.com/codex/cli/" : "https://code.claude.com/docs/en/setup")));
                Statuses(); break;
            case SetupStep.Verify:
                TextLine("Verify setup", true); TextLine("One ready provider is enough. You can also finish without configuring a provider."); Statuses(); break;
            case SetupStep.Preferences:
                TextLine("Preferences", true); TextLine("These use the same settings as the main application.");
                var p = preferences();
                var compact = new CheckBox { Text = "Compact Monitor", Checked = p.CompactMonitor, AutoSize = true };
                var tray = new CheckBox { Text = "Tray Icon", Checked = p.TrayIcon, AutoSize = true };
                compact.CheckedChanged += (_, _) => { savePreferences(preferences() with { CompactMonitor = compact.Checked }); compact.Checked = preferences().CompactMonitor; ApplyTheme(); };
                tray.CheckedChanged += (_, _) => { savePreferences(preferences() with { TrayIcon = tray.Checked }); tray.Checked = preferences().TrayIcon; ApplyTheme(); };
                var available = startup.TryRead(out var enabled);
                var launch = new CheckBox { Text = "Launch at Startup", Checked = enabled, Enabled = available, AutoSize = true };
                launch.Click += (_, _) => { toggleStartup(); launch.Enabled = startup.TryRead(out var actual); launch.Checked = actual; };
                var appearance = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Appearance", Width = 200 };
                appearance.Items.AddRange(["System", "Light", "Dark"]); appearance.SelectedIndex = (int)p.Appearance;
                appearance.SelectedIndexChanged += (_, _) => { savePreferences(preferences() with { Appearance = (Appearance)appearance.SelectedIndex }); appearance.SelectedIndex = (int)preferences().Appearance; ApplyTheme(); };
                body.Controls.Add(compact); body.Controls.Add(tray); body.Controls.Add(launch); TextLine("Appearance"); body.Controls.Add(appearance); break;
            case SetupStep.Done:
                TextLine("You’re all set", true); TextLine("Setup Llumi… remains available from the tray and app menu."); Statuses(); break;
        }
        body.Controls.Add(message); RefreshStatuses(); ApplyTheme(); body.ResumeLayout(true);
    }
    internal void ApplyTheme()
    {
        void Theme(Control root) { root.BackColor = Palette.Background; root.ForeColor = Palette.Foreground;
            if (root is Button button) { button.BackColor = Palette.Card; Palette.StyleButton(button); }
            foreach (Control child in root.Controls) Theme(child); }
        Theme(this);
    }
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); if (disposing) { Icon?.Dispose(); bodyFont.Dispose(); headingFont.Dispose(); } }
}
