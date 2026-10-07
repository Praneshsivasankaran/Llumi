using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class SetupRefinementTests
{
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
                next.PerformClick();
                Assert.Contains(Labels(form), l => l.Visible && l.Text == "Choose your providers");
                Assert.Same(next, form.AcceptButton); Assert.True(form.Controls.OfType<FlowLayoutPanel>().Single(p => p.Dock == DockStyle.Bottom).Visible);
                Button(form, "Back").PerformClick(); Assert.Same(next, form.AcceptButton);
            }
            Assert.Equal(0, requests); Assert.Equal(0, saves);
        }
        finally { Palette.Apply(Appearance.System); }
    });

    [Fact]
    public Task ClaudeGuideRetriesCanonicalProviderIndependentlyOfCodexLoading() => Sta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        ProviderState[] states = [new("Codex", ProviderStatus.Loading), new("Claude Code", ProviderStatus.Error,
            Failure: FailureKind.Timeout, AutomaticRetryAt: now.AddSeconds(60))];
        var requests = new List<string?>();
        using var form = FormAt(SetupStep.Claude, () => states, requests.Add, () => new(), _ => { });
        form.Show(); var retry = Button(form, "Retry"); Assert.True(retry.Enabled); retry.PerformClick();
        Assert.Equal(new[] { "Claude Code" }, requests);
        Assert.Contains(Labels(form), l => l.AccessibleName == "Claude Code setup status");
        Assert.DoesNotContain(Labels(form), l => l.AccessibleName == "Codex setup status");
        Assert.Contains(Labels(form), l => l.Text.Contains("Automatic retry", StringComparison.Ordinal));
        Assert.DoesNotContain(Labels(form), l => l.Text.StartsWith("Codex:", StringComparison.Ordinal));
        for (var i = 0; i < 4; i++) form.RefreshStatuses(); Assert.Single(requests);
        states = [states[0] with { Status = ProviderStatus.Unavailable }, states[1] with { RetryAt = now.AddSeconds(30), Failure = FailureKind.RateLimited }];
        form.RefreshStatuses(); Assert.False(retry.Enabled); retry.PerformClick(); Assert.Single(requests);
    });

    [Theory]
    [InlineData(1)] [InlineData(4)]
    public Task ProviderChoiceAndCheckSetupUseGlobalRetryAndSharedNestedSwitches(int step) => Sta(() =>
    {
        ProviderState[] states = [new("Codex", ProviderStatus.Loading), Ready("Claude Code")];
        var preferences = new Preferences(); var writes = 0; var requests = new List<string?>();
        using var form = FormAt((SetupStep)step, () => states, requests.Add, () => preferences, value => { preferences = value; writes++; });
        form.Show(); Button(form, "Retry").PerformClick(); Assert.Single(requests); Assert.Null(requests[0]);
        var codex = Switch(form, "Codex"); var claude = Switch(form, "Claude Code");
        Assert.IsType<FlowLayoutPanel>(codex.Parent); Assert.IsType<FlowLayoutPanel>(claude.Parent);
        claude.Checked = false; Assert.False(preferences.ClaudeEnabled); Assert.Equal(1, writes);
        preferences = preferences with { CodexEnabled = false }; form.RefreshStatuses();
        Assert.False(codex.Checked); Assert.False(claude.Checked); Assert.Equal(1, writes);
        Assert.False(Button(form, "Retry").Enabled); Assert.All(Labels(form).Where(l => l.AccessibleName?.EndsWith("setup status", StringComparison.Ordinal) == true), l => Assert.Contains("Monitoring off", l.Text));
        if ((SetupStep)step == SetupStep.Verify) Assert.True(Button(form, "Finish Anyway").Enabled);
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

    [Fact]
    public Task MissingOrDisabledGuideNeverUsesOtherProvidersReadinessOrRetry() => Sta(() =>
    {
        ProviderState[] states = [Ready("Codex")]; var preferences = new Preferences(); var requests = new List<string?>();
        using var form = FormAt(SetupStep.Claude, () => states, requests.Add, () => preferences, _ => { });
        form.Show(); Assert.False(Button(form, "Retry").Enabled);
        states = [states[0], Ready("Claude Code")]; preferences = preferences with { ClaudeEnabled = false }; form.RefreshStatuses();
        Assert.False(Button(form, "Retry").Enabled); Button(form, "Retry").PerformClick(); Assert.Empty(requests);
        Assert.Contains(Labels(form), l => l.AccessibleName == "Claude Code setup status" && l.Text.Contains("Monitoring off", StringComparison.Ordinal));
    });

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public Task NumberedCopyOnlyGuidesKeepCommandsAndNativeActionsReachable(int step) => Sta(() =>
    {
        using var form = FormAt((SetupStep)step, () => [Ready("Codex"), Ready("Claude Code")], _ => { }, () => new(), _ => { });
        form.Show(); form.ClientSize = new Size(620, 580); form.PerformLayout();
        var boxes = Descendants(form).OfType<TextBox>().ToArray(); Assert.Equal(2, boxes.Length);
        Assert.All(boxes, box => Assert.True(box.ReadOnly));
        Assert.Equal((SetupStep)step == SetupStep.Codex ? "npm install -g @openai/codex" : "irm https://claude.ai/install.ps1 | iex", boxes[0].Text);
        Assert.Equal((SetupStep)step == SetupStep.Codex ? "codex login" : "claude auth login", boxes[1].Text);
        Assert.Contains(Labels(form), l => l.Text == "1. Install"); Assert.Contains(Labels(form), l => l.Text == "2. Sign in");
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
