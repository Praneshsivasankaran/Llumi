using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class SetupRefinementTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public Task InitialWindowUsesScaledPreferredSizeAndFitsWorkAreaAtEveryStep(int step) => Sta(() =>
    {
        using var form = FormAt((SetupStep)step, () => [Ready("Codex"), Ready("Claude Code")], _ => { }, () => new(), _ => { });
        form.Show();
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        var area = Screen.FromControl(form).WorkingArea;
        var chrome = form.Size - form.ClientSize;
        var expected = new Size(Math.Min(S(620), area.Width - S(16) * 2 - chrome.Width),
            Math.Min(S(580), area.Height - S(16) * 2 - chrome.Height));
        Assert.InRange(Math.Abs(form.ClientSize.Width - expected.Width), 0, 2);
        Assert.InRange(Math.Abs(form.ClientSize.Height - expected.Height), 0, 2);
        area.Inflate(-S(16), -S(16)); Assert.True(area.Contains(form.Bounds));
        if ((SetupStep)step != SetupStep.Welcome)
        {
            var next = Assert.IsType<Button>(form.AcceptButton); var back = Button(form, "Back");
            Assert.Equal(new Size(S(112), S(38)), next.MinimumSize);
            Assert.Equal(back.MinimumSize, next.MinimumSize);
            var footer = form.Controls.OfType<FlowLayoutPanel>().Single(p => p.Dock == DockStyle.Bottom);
            Assert.Equal(form.ClientSize.Height, footer.Bottom);
            Assert.True(footer.ClientRectangle.Contains(next.Bounds)); Assert.True(footer.ClientRectangle.Contains(back.Bounds));
            Assert.True(form.Controls.OfType<FlowLayoutPanel>().Single(p => p.Dock == DockStyle.Fill).Bottom <= footer.Top);
        }
    });

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public Task CenteredWelcomeResizesAndKeepsDefaultNavigationThroughRepeatedBack(int appearance) => Sta(() =>
    {
        try
        {
            Palette.Apply((Appearance)appearance);
            var requests = 0; var saves = 0;
            using var form = FormAt(SetupStep.Welcome, () => [Ready("Codex"), Ready("Claude Code")], _ => requests++, () => new(), _ => saves++);
            form.Show(); var next = Assert.IsType<Button>(form.AcceptButton);
            for (var pass = 0; pass < 3; pass++)
            {
                form.ClientSize = pass == 1 ? new Size(544, 442) : new Size(760, 640); form.PerformLayout();
                var group = Descendants(form).Single(c => c.Name == "welcomeGroup");
                var logo = Descendants(form).Single(c => c.Name == "welcomeLogo");
                var name = Labels(form).Single(l => l.Text == "Llumi");
                Assert.True(group.Visible); Assert.Equal("Get Started", next.Text); Assert.False(next.IsDisposed);
                Assert.InRange(Math.Abs(group.Left * 2 + group.Width - group.Parent!.ClientSize.Width), 0, 1);
                Assert.InRange(Math.Abs(group.Top * 2 + group.Height - group.Parent.ClientSize.Height), 0, 1);
                Assert.InRange(Math.Abs(logo.Left * 2 + logo.Width - group.Width), 0, 1);
                Assert.InRange(Math.Abs(name.Left * 2 + name.Width - group.Width), 0, 1);
                Assert.InRange(Math.Abs(next.Left * 2 + next.Width - group.Width), 0, 1);
                Assert.True(logo.Bottom < name.Top && name.Bottom < next.Top);
                Assert.Equal(Color.White.ToArgb(), next.ForeColor.ToArgb()); Assert.True(next.BackColor.B > next.BackColor.R + 100);
                Assert.False(form.Controls.OfType<FlowLayoutPanel>().Single(p => p.Dock == DockStyle.Bottom).Visible);
                var sizeBeforeNavigation = form.ClientSize;
                next.PerformClick();
                Assert.Contains(Labels(form), l => l.Visible && l.Text == "Choose your providers");
                Assert.Same(next, form.AcceptButton); Assert.True(form.Controls.OfType<FlowLayoutPanel>().Single(p => p.Dock == DockStyle.Bottom).Visible);
                var navigation = form.Controls.OfType<FlowLayoutPanel>().Single(p => p.Dock == DockStyle.Bottom);
                Assert.Equal(sizeBeforeNavigation, form.ClientSize);
                Assert.True(form.ClientRectangle.Contains(navigation.Bounds));
                Assert.True(navigation.ClientRectangle.Contains(next.Bounds));
                Assert.True(navigation.ClientRectangle.Contains(Button(form, "Back").Bounds));
                Assert.True(form.Controls.OfType<FlowLayoutPanel>().Single(p => p.Dock == DockStyle.Fill).Bottom <= navigation.Top);
                Button(form, "Back").PerformClick(); Assert.Same(next, form.AcceptButton);
                Assert.Equal(sizeBeforeNavigation, form.ClientSize);
            }
            Assert.Equal(0, requests); Assert.Equal(0, saves);
        }
        finally { Palette.Apply(Appearance.System); }
    });

    [Theory]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public Task CleanGuidesDoNotReadCollectorStateOrOfferCheckActionsBeforeVerification(int step, int scenario) => Sta(() =>
    {
        var now = DateTimeOffset.UtcNow; var reads = 0; var saves = 0; var requests = new List<string?>();
        Preferences preferences = scenario == 1 ? new(CodexEnabled: false, ClaudeEnabled: false) : new();
        ProviderState[] states = scenario switch
        {
            0 => [],
            1 => [new("Codex", ProviderStatus.Unavailable, Enabled: false), new("Claude Code", ProviderStatus.Unavailable, Enabled: false)],
            _ => [new("Codex", ProviderStatus.Error, Failure: FailureKind.RateLimited, RetryAt: now.AddMinutes(1)),
                new("Claude Code", ProviderStatus.Error, Failure: FailureKind.Timeout, AutomaticRetryAt: now.AddMinutes(2))]
        };
        var flow = new SetupFlow(new(Path.Combine(Path.GetTempPath(), "Llumi-guide-fixture-" + Guid.NewGuid(), "completion.json")));
        while (flow.Step != (SetupStep)step) flow.Next();
        using var form = new SetupForm(flow, () => { reads++; return states; }, requests.Add, () => preferences,
            _ => saves++, new Startup(), () => { }, () => { });
        form.Show(); var isCodex = (SetupStep)step == SetupStep.Codex;
        var title = isCodex ? "Codex" : "Claude Code";
        Assert.Equal(new[] { "Set up " + title, "Run these in PowerShell.", "1. Install", "2. Sign in" },
            Labels(form).Where(l => l.Visible && l is not LinkLabel).Select(l => l.Text).ToArray());
        Assert.Equal(new[] { "Back", "Continue", "Copy", "Copy" },
            Descendants(form).OfType<Button>().Where(b => b.Visible).Select(b => b.Text).Order().ToArray());
        Assert.DoesNotContain(Descendants(form).OfType<CheckBox>(), c => c.Visible);
        Assert.DoesNotContain(Labels(form), l => l.Visible && l.AccessibleName?.EndsWith("setup status", StringComparison.Ordinal) == true);
        var help = Descendants(form).OfType<LinkLabel>().Single(l => l.Visible);
        Assert.Equal("Setup help", help.Text); Assert.Equal(title + " setup help", help.AccessibleName);
        Assert.True(help.TabStop);
        for (var pass = 0; pass < 5; pass++) form.RefreshStatuses();
        Assert.Equal(0, reads); Assert.Equal(0, saves); Assert.Empty(requests);

        var next = Assert.IsType<Button>(form.AcceptButton); next.PerformClick();
        if (isCodex && preferences.ClaudeEnabled)
        {
            Assert.Equal(SetupStep.Claude, flow.Step);
            Assert.Equal(0, reads); Assert.Empty(requests); next.PerformClick();
        }
        Assert.Equal(SetupStep.Verify, flow.Step); Assert.True(reads > 0);
        Assert.Single(requests); Assert.Null(requests[0]);
        Assert.Contains(Descendants(form).OfType<Button>(), b => b.Visible && b.Text == "Retry");
        Assert.Contains(Descendants(form).OfType<Button>(), b => b.Visible && b.Text == "Copy Diagnostics");
        Assert.Equal(0, saves);
    });

    [Fact]
    public Task CheckSetupUsesGlobalRetryAndSharedNestedSwitches() => Sta(() =>
    {
        ProviderState[] states = [new("Codex", ProviderStatus.Loading), Ready("Claude Code")];
        var preferences = new Preferences(); var writes = 0; var requests = new List<string?>();
        using var form = FormAt(SetupStep.Verify, () => states, requests.Add, () => preferences, value => { preferences = value; writes++; });
        form.Show(); Button(form, "Retry").PerformClick(); Assert.Single(requests); Assert.Null(requests[0]);
        var codex = Switch(form, "Codex"); var claude = Switch(form, "Claude Code");
        Assert.IsType<FlowLayoutPanel>(codex.Parent); Assert.IsType<FlowLayoutPanel>(claude.Parent);
        claude.Checked = false; Assert.False(preferences.ClaudeEnabled); Assert.Equal(1, writes);
        preferences = preferences with { CodexEnabled = false }; form.RefreshStatuses();
        Assert.False(codex.Checked); Assert.False(claude.Checked); Assert.Equal(1, writes);
        Assert.False(Button(form, "Retry").Enabled); Assert.All(Labels(form).Where(l => l.AccessibleName?.EndsWith("setup status", StringComparison.Ordinal) == true), l => Assert.Contains("Monitoring off", l.Text));
        Assert.True(Button(form, "Finish Anyway").Enabled);
    });

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)] [InlineData(false, false)]
    public Task ProviderChoicePersistsOnlyChoicesAndRoutesWithoutRequestingChecks(bool codexEnabled, bool claudeEnabled) => Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Llumi-provider-choice-fixture-" + Guid.NewGuid());
        try
        {
            var preferences = new PreferenceStore(Path.Combine(directory, "preferences.json"));
            Assert.True(preferences.Save(new()));
            var flow = new SetupFlow(new(Path.Combine(directory, "completion.json")));
            var requests = new List<string?>(); var writes = 0; var now = DateTimeOffset.UtcNow;
            ProviderState[] states = [new("Codex", ProviderStatus.Error, Failure: FailureKind.RateLimited,
                RetryAt: now.AddMinutes(1), AutomaticRetryAt: now.AddMinutes(2)), Ready("Claude Code")];
            using var form = new SetupForm(flow, () => states, requests.Add, preferences.Load,
                value => { Assert.True(preferences.Save(value)); writes++; }, new Startup(), () => { }, () => { });
            form.Show(); Button(form, "Get Started").PerformClick();
            Assert.Equal(SetupStep.Providers, flow.Step);
            Assert.Equal(new[] { "Back", "Continue" }, Descendants(form).OfType<Button>().Where(b => b.Visible).Select(b => b.Text).Order().ToArray());
            Assert.Equal(2, Descendants(form).OfType<CheckBox>().Count());
            Assert.Equal(2, Descendants(form).OfType<ProviderArtwork>().Count());
            Assert.DoesNotContain(Labels(form), l => l.AccessibleName?.EndsWith("setup status", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(Descendants(form).OfType<Button>(), b => b.Text is "Retry" or "Copy Diagnostics" or "Official setup guide");
            Assert.DoesNotContain(Labels(form), l => l.Visible && (l.Text.Contains("Automatic retry", StringComparison.Ordinal)
                || l.Text.Contains("Rate limited", StringComparison.Ordinal) || l.Text.Contains("Signed in", StringComparison.Ordinal)
                || l.Text.Contains("Monitoring:", StringComparison.Ordinal)));

            var codex = Switch(form, "Codex"); var claude = Switch(form, "Claude Code");
            codex.Checked = !codexEnabled; codex.Checked = codexEnabled;
            claude.Checked = !claudeEnabled; claude.Checked = claudeEnabled;
            Assert.True(writes > 0);
            Assert.Equal(codexEnabled, preferences.Load().CodexEnabled); Assert.Equal(claudeEnabled, preferences.Load().ClaudeEnabled);
            var savedWrites = writes;
            for (var pass = 0; pass < 3; pass++) form.RefreshStatuses();
            Assert.Equal(savedWrites, writes); Assert.Empty(requests);
            Assert.Equal(codexEnabled, flow.Codex); Assert.Equal(claudeEnabled, flow.Claude);

            var next = Assert.IsType<Button>(form.AcceptButton); next.PerformClick();
            if (codexEnabled)
            {
                Assert.Equal(SetupStep.Codex, flow.Step);
                Assert.Contains(Labels(form), l => l.Visible && l.Text == "Set up Codex");
                Assert.DoesNotContain(Labels(form), l => l.Visible && l.AccessibleName?.EndsWith("setup status", StringComparison.Ordinal) == true);
                Assert.DoesNotContain(Descendants(form).OfType<Button>(), b => b.Text is "Retry" or "Copy Diagnostics");
                Assert.Empty(requests); next.PerformClick();
            }
            if (claudeEnabled)
            {
                Assert.Equal(SetupStep.Claude, flow.Step);
                Assert.Contains(Labels(form), l => l.Visible && l.Text == "Set up Claude Code");
                Assert.DoesNotContain(Labels(form), l => l.Visible && l.AccessibleName?.EndsWith("setup status", StringComparison.Ordinal) == true);
                Assert.DoesNotContain(Descendants(form).OfType<Button>(), b => b.Text is "Retry" or "Copy Diagnostics");
                Assert.Empty(requests); next.PerformClick();
            }
            Assert.Equal(SetupStep.Verify, flow.Step); Assert.Single(requests); Assert.Null(requests[0]);
            Assert.Contains(Descendants(form).OfType<Button>(), b => b.Text == "Retry");
            Assert.Contains(Descendants(form).OfType<Button>(), b => b.Text == "Copy Diagnostics");
            Assert.Equal(codexEnabled, Switch(form, "Codex").Checked);
            Assert.Equal(claudeEnabled, Switch(form, "Claude Code").Checked);
            if (!codexEnabled && !claudeEnabled) Assert.False(Button(form, "Retry").Enabled);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task FailedProviderSaveRollsBackNestedSwitchAndShowsFeedbackBeforeBothOffFinish() => Sta(() =>
    {
        var preferences = new Preferences(); var reject = true; var writes = 0; var requests = new List<string?>();
        using var form = FormAt(SetupStep.Verify, () => [Ready("Codex"), Ready("Claude Code")], requests.Add, () => preferences,
            value => { writes++; if (!reject) preferences = value; });
        form.Show(); var codex = Switch(form, "Codex"); var claude = Switch(form, "Claude Code");
        codex.Checked = false;
        Assert.True(codex.Checked); Assert.True(claude.Checked); Assert.True(preferences.CodexEnabled); Assert.Equal(1, writes);
        Assert.Contains(Labels(form), l => l.Visible && l.Text == "Settings could not be saved. Please try again.");
        reject = false; codex.Checked = false; claude.Checked = false;
        Assert.False(preferences.CodexEnabled); Assert.False(preferences.ClaudeEnabled); Assert.False(Button(form, "Retry").Enabled);
        Assert.DoesNotContain(Labels(form), l => l.Visible && l.Text.Contains("could not be saved", StringComparison.Ordinal));
        Button(form, "Finish Anyway").PerformClick(); Assert.Contains(Descendants(form).OfType<CheckBox>(), c => c.Text == "Compact Monitor");
        Assert.Empty(requests);
    });

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public Task CleanCopyOnlyGuidesKeepCommandsAndKeyboardActionsReachable(int step) => Sta(() =>
    {
        using var form = FormAt((SetupStep)step, () => [Ready("Codex"), Ready("Claude Code")], _ => { }, () => new(), _ => { });
        form.Show(); form.ClientSize = new Size(620, 580); form.PerformLayout();
        var boxes = Descendants(form).OfType<TextBox>().ToArray(); Assert.Equal(2, boxes.Length);
        Assert.All(boxes, box => { Assert.True(box.ReadOnly); Assert.True(box.ShortcutsEnabled); Assert.True(box.TabStop); box.Select(); Assert.True(box.Focused); });
        Assert.Equal((SetupStep)step == SetupStep.Codex ? "npm install -g @openai/codex" : "irm https://claude.ai/install.ps1 | iex", boxes[0].Text);
        Assert.Equal((SetupStep)step == SetupStep.Codex ? "codex login" : "claude auth login", boxes[1].Text);
        Assert.Contains(Labels(form), l => l.Text == "1. Install"); Assert.Contains(Labels(form), l => l.Text == "2. Sign in");
        Assert.Equal(new[] { "1. Install", "2. Sign in" }, boxes.Select(box => box.AccessibleName).ToArray());
        Assert.Contains(Labels(form), l => l.Visible && l.Text == "Run these in PowerShell.");
        Assert.Equal(new[] { "Copy 1. Install command", "Copy 2. Sign in command" },
            Descendants(form).OfType<Button>().Where(button => button.Visible && button.Text == "Copy").Select(button => button.AccessibleName).ToArray());
        Assert.All(Descendants(form).OfType<Button>().Where(button => button.Text == "Copy"), button =>
        {
            Assert.True(button.ClientSize.Height >= button.GetPreferredSize(Size.Empty).Height);
            Assert.True(button.TabStop); button.Select(); Assert.True(button.Focused);
        });
        Assert.DoesNotContain(Labels(form), l => l.Text.Contains("Copies only", StringComparison.Ordinal) || l.Text.Contains("Console/API", StringComparison.Ordinal) || l.Text.Contains("Credentials stay", StringComparison.Ordinal));
        var body = form.Controls.OfType<FlowLayoutPanel>().Single(panel => panel.Dock == DockStyle.Fill);
        foreach (var control in Descendants(body).Where(c => c is System.Windows.Forms.Button or TextBox))
        {
            body.ScrollControlIntoView(control);
            Assert.True(body.ClientRectangle.Contains(body.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))), "Unreachable guide control: " + control.AccessibleName);
        }
    });

    private static ProviderState Ready(string name) => new(name, ProviderStatus.Ready,
        new([new("five_hour", "synthetic", 20, DateTimeOffset.UtcNow.AddHours(5))], DateTimeOffset.UtcNow, "fixture"), Authentication: AuthenticationStatus.Verified);
    private static SetupForm FormAt(SetupStep step, Func<IReadOnlyList<ProviderState>> states, Action<string?> refresh,
        Func<Preferences> preferences, Action<Preferences> save)
    {
        var flow = new SetupFlow(new(Path.Combine(Path.GetTempPath(), "Llumi-setup-fixture-" + Guid.NewGuid(), "completion.json")));
        while (flow.Step != step) flow.Next();
        return new(flow, states, refresh, preferences, save, new Startup(), () => { }, () => { });
    }
    private static Button Button(Form form, string text) => Descendants(form).OfType<Button>().Single(b => b.Text == text);
    private static CheckBox Switch(Form form, string provider) => Descendants(form).OfType<CheckBox>().Single(c => c.AccessibleName == "Monitor " + provider);
    private static IEnumerable<Label> Labels(Control root) => Descendants(root).OfType<Label>();
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private sealed class Startup : IStartupRegistration
    {
        public bool TryRead(out bool enabled) { enabled = false; return true; }
        public bool TrySet(bool enabled) => true;
    }
    private static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }
}
