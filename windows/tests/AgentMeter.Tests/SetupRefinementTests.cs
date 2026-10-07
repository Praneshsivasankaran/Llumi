using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class SetupRefinementTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(2, false)] [InlineData(2, true)]
    public Task ReopenedSetupStaysLightAtEveryStepWithoutChangingSavedOrOtherWindowAppearance(int appearance, bool systemLight) => Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Llumi-setup-theme-fixture-" + Guid.NewGuid());
        try
        {
            var path = Path.Combine(directory, "preferences.json"); var store = new PreferenceStore(path);
            var choice = new Preferences(Appearance: (Appearance)appearance); Assert.True(store.Save(choice));
            var original = File.ReadAllText(path); var saves = 0;
            Palette.Apply(choice.Appearance, systemLight);
            var paletteBefore = (Palette.IsLight, Palette.Background, Palette.Foreground, Palette.Card, Palette.Secondary,
                Palette.Border, Palette.Muted, Palette.Track, Palette.Live, Palette.Warning);
            using var icon = AppIcon.Load();
            using var usage = new UsageForm(["Codex", "Claude Code"], icon);
            usage.SetPreferences(choice); usage.Show();
            using var monitor = new MonitorForm(["Codex", "Claude Code"], icon) { MotionAllowed = () => false };
            monitor.Render([Ready("Codex"), Ready("Claude Code")]);
            Color MonitorBackground()
            {
                using var bitmap = monitor.CreatePreviewBitmap();
                return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height - Math.Max(4, monitor.DeviceDpi / 24));
            }
            var monitorBefore = MonitorBackground(); var appBackground = usage.BackColor;
            using var form = FormAt(SetupStep.Welcome, () => [Ready("Codex"), Ready("Claude Code")], _ => { }, store.Load, _ => saves++);
            form.Show();
            void AssertUnchanged()
            {
                form.ApplyTheme();
                Assert.Equal(Color.FromArgb(247, 248, 250), form.BackColor);
                Assert.Equal(Color.FromArgb(28, 28, 28), form.ForeColor);
                Assert.All(Descendants(form).Where(c => c.Visible && c is not System.Windows.Forms.Button), c => Assert.Equal(form.ForeColor, c.ForeColor));
                Assert.All(Descendants(form).OfType<FlowLayoutPanel>().Where(c => c.Name == "setupCard"), c => Assert.Equal(Color.White, c.BackColor));
                Assert.All(Descendants(form).OfType<ProviderArtwork>(), artwork => Assert.Equal(form.ForeColor, artwork.ForeColor));
                Assert.All(Descendants(form).OfType<ToggleSwitch>(), toggle => Assert.True(toggle.LightAppearanceOverride));
                Assert.Equal(paletteBefore, (Palette.IsLight, Palette.Background, Palette.Foreground, Palette.Card, Palette.Secondary,
                    Palette.Border, Palette.Muted, Palette.Track, Palette.Live, Palette.Warning));
                Assert.Equal(choice, store.Load()); Assert.Equal(original, File.ReadAllText(path)); Assert.Equal(0, saves);
                usage.ApplyTheme(); monitor.UpdateSurface();
                Assert.Equal(appBackground, usage.BackColor); Assert.Equal(monitorBefore, MonitorBackground());
            }
            for (var step = 0; step < 7; step++)
            {
                AssertUnchanged();
                if ((SetupStep)step == SetupStep.Preferences)
                    Assert.Equal(appearance, Descendants(form).OfType<ComboBox>().Single().SelectedIndex);
                if (step < 6) Assert.IsAssignableFrom<Button>(form.AcceptButton).PerformClick();
            }
            for (var step = 5; step >= 0; step--) { Button(form, "Back").PerformClick(); AssertUnchanged(); }
        }
        finally { Palette.Apply(Appearance.System); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task ExplicitAppearanceChangesFromSetupAffectTheAppWhileSetupRemainsLight() => Sta(() =>
    {
        try
        {
            var saved = new Preferences(); Palette.Apply(saved.Appearance);
            using var usage = new UsageForm(["Codex"], SystemIcons.Application); usage.Show();
            using var form = FormAt(SetupStep.Preferences, () => [], _ => { }, () => saved,
                requested => { saved = requested; Palette.Apply(saved.Appearance, false); usage.SetPreferences(saved); });
            form.Show(); var picker = Descendants(form).OfType<ComboBox>().Single();
            foreach (var appearance in new[] { Appearance.Dark, Appearance.System, Appearance.Light })
            {
                picker.SelectedIndex = (int)appearance;
                Assert.Equal(appearance, saved.Appearance);
                Assert.Equal(appearance == Appearance.Light, Palette.IsLight);
                Assert.Equal(Palette.Background, usage.BackColor);
                Assert.Equal(Color.FromArgb(247, 248, 250), form.BackColor);
                Assert.Equal(Color.FromArgb(28, 28, 28), form.ForeColor);
            }
        }
        finally { Palette.Apply(Appearance.System); }
    });

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
            var next = Assert.IsAssignableFrom<Button>(form.AcceptButton); var back = Button(form, "Back");
            Assert.Equal(new Size(S(112), S(38)), next.MinimumSize);
            Assert.Equal(back.MinimumSize, next.MinimumSize);
            AssertNavigationFollowsContent(form);
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
            form.Show(); var next = Assert.IsAssignableFrom<Button>(form.AcceptButton);
            for (var pass = 0; pass < 3; pass++)
            {
                form.ClientSize = pass == 1 ? new Size(544, 442) : new Size(760, 640); form.PerformLayout();
                var group = Descendants(form).Single(c => c.Name == "welcomeGroup");
                var logo = Descendants(form).Single(c => c.Name == "welcomeLogo");
                var name = Labels(form).Single(l => l.Text == "Llumi");
                Assert.True(group.Visible); Assert.Equal("Get Started", next.Text); Assert.False(next.IsDisposed);
                Assert.Equal(next.Text, next.AccessibilityObject.Name);
                Assert.InRange(Math.Abs(group.Left * 2 + group.Width - group.Parent!.ClientSize.Width), 0, 1);
                Assert.InRange(Math.Abs(group.Top * 2 + group.Height - group.Parent.ClientSize.Height), 0, 1);
                Assert.InRange(Math.Abs(logo.Left * 2 + logo.Width - group.Width), 0, 1);
                Assert.InRange(Math.Abs(name.Left * 2 + name.Width - group.Width), 0, 1);
                Assert.InRange(Math.Abs(next.Left * 2 + next.Width - group.Width), 0, 1);
                Assert.True(logo.Bottom < name.Top && name.Bottom < next.Top);
                Assert.Equal(Color.White.ToArgb(), next.ForeColor.ToArgb()); Assert.True(next.BackColor.B > next.BackColor.R + 100);
                Assert.False(Panel(form, "setupNavigation").Visible);
                var sizeBeforeNavigation = form.ClientSize;
                next.PerformClick();
                Assert.Contains(Labels(form), l => l.Visible && l.Text == "Choose your providers");
                Assert.Same(next, form.AcceptButton); Assert.True(Panel(form, "setupNavigation").Visible);
                Assert.Equal(next.Text, next.AccessibilityObject.Name);
                var navigation = Panel(form, "setupNavigation");
                Assert.Equal(sizeBeforeNavigation, form.ClientSize);
                Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(navigation.RectangleToScreen(navigation.ClientRectangle))));
                Assert.True(navigation.ClientRectangle.Contains(next.Bounds));
                Assert.True(navigation.ClientRectangle.Contains(Button(form, "Back").Bounds));
                Assert.Equal(Button(form, "Back").Height, next.Height);
                Assert.Equal(Button(form, "Back").Top, next.Top);
                AssertNavigationFollowsContent(form);
                Button(form, "Back").PerformClick(); Assert.Same(next, form.AcceptButton);
                Assert.Equal(sizeBeforeNavigation, form.ClientSize);
            }
            Assert.Equal(0, requests); Assert.Equal(0, saves);
        }
        finally { Palette.Apply(Appearance.System); }
    });

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public Task EveryContentPageBalancesAvailableSpaceAndScrollsWhenConstrained(int step) => Sta(() =>
    {
        using var form = FormAt((SetupStep)step, () => [Ready("Codex"), Ready("Claude Code")], _ => { }, () => new(), _ => { });
        form.Show(); form.MinimumSize = Size.Empty;
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        foreach (var size in new[] { new Size(S(620), S(580)), new Size(S(544), S(442)), new Size(S(420), S(280)) })
        {
            form.ClientSize = size; form.PerformLayout(); Application.DoEvents();
            var viewport = Panel(form, "setupViewport"); var page = Panel(form, "setupPage");
            var content = Panel(form, "setupContent");
            viewport.AutoScrollPosition = Point.Empty; form.PerformLayout(); Application.DoEvents();
            Assert.False(viewport.HorizontalScroll.Visible);
            Assert.True(page.Width <= viewport.ClientSize.Width, "Page should fit the viewport width.");
            Assert.InRange(page.Left, 0, viewport.ClientSize.Width - page.Width);
            Assert.InRange(Math.Abs(page.Left * 2 + page.Width - viewport.ClientSize.Width), 0, SystemInformation.VerticalScrollBarWidth + 2);
            if (!viewport.VerticalScroll.Visible && page.Height <= viewport.ClientSize.Height - S(32))
                Assert.InRange(Math.Abs(page.Top * 2 + page.Height - viewport.ClientSize.Height), 0, 2);
            else
                // Resizing may scroll to preserve the focused control. Check the
                // page's unscrolled origin independently of that native behavior.
                Assert.InRange(page.Top - viewport.AutoScrollPosition.Y, 0, S(32));
            AssertNavigationFollowsContent(form);
            var cards = Descendants(content).OfType<FlowLayoutPanel>().Where(c => c.Name == "setupCard").ToArray();
            if (cards.Length > 0)
            {
                Assert.All(cards, card => Assert.True(card.Width >= content.ClientSize.Width * .85, "Cards should use the available content width."));
                Assert.Single(cards.Select(card => card.Width).Distinct());
                if ((SetupStep)step == SetupStep.Providers)
                {
                    Assert.Equal(2, cards.Length); Assert.Equal(cards[0].Height, cards[1].Height);
                    Assert.All(cards, card => Assert.True(card.Height >= S(48), "Provider choices need a comfortable padded row."));
                }
            }
            AssertControlsReachable(viewport);
        }
    });

    [Fact]
    public Task EachSetupStepStartsAtTheTopAndKeepsTheDefaultActionWhenSpaceIsConstrained() => Sta(() =>
    {
        using var form = FormAt(SetupStep.Welcome, () => [Ready("Codex"), Ready("Claude Code")], _ => { }, () => new(), _ => { });
        form.Show(); form.MinimumSize = Size.Empty;
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        form.ClientSize = new Size(S(420), S(280));
        var next = Assert.IsAssignableFrom<Button>(form.AcceptButton);
        for (var step = 1; step <= 6; step++)
        {
            next.PerformClick(); Application.DoEvents();
            var viewport = Panel(form, "setupViewport");
            Assert.Equal(Point.Empty, viewport.AutoScrollPosition);
            Assert.Same(next, form.AcceptButton);
            AssertControlsReachable(viewport);
        }
    });

    [Fact]
    public Task ForwardAndBackTransitionsKeepNavigationVisibleAndNeverScrollHorizontallyAfterResizing() => Sta(() =>
    {
        // A form constructed directly on a guide skips Welcome reparenting and
        // earlier resize/docking transitions. Keep the host's DPI context;
        // changing it after WinForms caches metrics produces mixed scale values.
        // The separate ReviewHost capture process covers PerMonitorV2 scaling.
        using var form = FormAt(SetupStep.Welcome, () => [Ready("Codex"), Ready("Claude Code")], _ => { }, () => new(), _ => { });
        form.Show(); Application.DoEvents();
        var preferred = form.ClientSize; var minimum = form.MinimumSize;
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        var next = Assert.IsAssignableFrom<Button>(form.AcceptButton);
        form.ClientSize = new Size(544, 442); Application.DoEvents();
        form.ClientSize = preferred; Application.DoEvents();
        void Check(string stage)
        {
            var viewport = Panel(form, "setupViewport"); var page = Panel(form, "setupPage");
            var footer = Panel(form, "setupFixedNavigation"); var navigation = Panel(form, "setupNavigation");
            var detail = $"{stage}: DPI={form.DeviceDpi}, form={form.ClientSize}, viewport={viewport.Bounds}/{viewport.ClientSize}, display={viewport.DisplayRectangle}, scroll={viewport.AutoScrollPosition}, page={page.Bounds}, footer={footer.Bounds}/{footer.Visible}, navigation={navigation.Bounds}/{navigation.Parent?.Name}, next={next.Bounds}, back={Button(form, "Back").Bounds}";
            Assert.False(viewport.HorizontalScroll.Visible, detail);
            var style = GetWindowLong(viewport.Handle, -16);
            Assert.True((style & 0x00100000) == 0, "Native WS_HSCROLL: " + detail);
            Assert.Equal(viewport.VerticalScroll.Visible, (style & 0x00200000) != 0);
            Assert.Equal(0, viewport.AutoScrollPosition.X);
            foreach (var button in new[] { Button(form, "Back"), next })
                Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))), detail);
            if (footer.Visible) Assert.True(footer.ClientRectangle.Contains(navigation.Bounds), detail);
            Assert.True(navigation.ClientRectangle.Contains(next.Bounds), detail);
            Assert.True(navigation.ClientRectangle.Contains(Button(form, "Back").Bounds), detail);
            AssertNavigationFollowsContent(form);
            if (footer.Visible) Assert.True(viewport.Bottom <= footer.Top, detail);
        }
        for (var step = 1; step <= 6; step++)
        {
            next.PerformClick(); Application.DoEvents(); Check($"forward {step}");
            form.MinimumSize = Size.Empty;
            form.ClientSize = new Size(S(420), S(280)); Application.DoEvents(); Check($"constrained {step}");
            form.MinimumSize = minimum; form.ClientSize = preferred; Application.DoEvents(); Check($"restored {step}");
        }
        for (var step = 5; step >= 1; step--)
        {
            Button(form, "Back").PerformClick(); Application.DoEvents(); Check($"back {step}");
            form.MinimumSize = Size.Empty;
            form.ClientSize = new Size(S(420), S(280)); Application.DoEvents(); Check($"back constrained {step}");
            form.MinimumSize = minimum; form.ClientSize = preferred; Application.DoEvents(); Check($"back restored {step}");
        }
    }, TimeSpan.FromSeconds(30));

    [Fact]
    public Task VerificationWrapsFeedbackAndKeepsScrollPositionAcrossStatusUpdates() => Sta(() =>
    {
        var at = DateTimeOffset.UtcNow;
        ProviderState[] states = [
            new("Codex", ProviderStatus.Error, Failure: FailureKind.RateLimited, RetryAt: at.AddMinutes(15), AutomaticRetryAt: at.AddMinutes(15)),
            new("Claude Code", ProviderStatus.Error, Failure: FailureKind.RateLimited, RetryAt: at.AddMinutes(15), AutomaticRetryAt: at.AddMinutes(15))
        ];
        using var form = FormAt(SetupStep.Verify, () => states, _ => { }, () => new(), _ => { });
        form.Show(); form.MinimumSize = Size.Empty;
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        form.ClientSize = new Size(S(420), S(280)); form.PerformLayout(); Application.DoEvents();
        Switch(form, "Codex").Checked = false;
        Assert.Contains(Labels(form), label => label.Visible && label.Text == "Settings could not be saved. Please try again.");
        var viewport = Panel(form, "setupViewport");
        Assert.True(viewport.VerticalScroll.Visible); Assert.False(viewport.HorizontalScroll.Visible);
        AssertControlsReachable(viewport); AssertNavigationFollowsContent(form);
        var next = Assert.IsAssignableFrom<Button>(form.AcceptButton);
        viewport.AutoScrollPosition = new Point(0, viewport.VerticalScroll.Maximum);
        var scroll = viewport.AutoScrollPosition;
        var extent = viewport.AutoScrollMinSize;
        Assert.True(scroll.Y < 0);
        for (var pass = 0; pass < 3; pass++)
        {
            form.RefreshStatuses(); form.PerformLayout(); Application.DoEvents();
            Assert.Equal(extent, viewport.AutoScrollMinSize);
            Assert.Equal(scroll, viewport.AutoScrollPosition);
        }
        Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(next.RectangleToScreen(next.ClientRectangle))));
        Assert.Equal("Finish Anyway", next.AccessibilityObject.Name);
        states = [Ready("Codex"), Ready("Claude Code")]; form.RefreshStatuses(); form.PerformLayout();
        Assert.InRange(-viewport.AutoScrollPosition.Y, 0, Math.Max(0, viewport.AutoScrollMinSize.Height - viewport.ClientSize.Height));
        Assert.Equal("Continue", next.AccessibilityObject.Name); Assert.False(viewport.HorizontalScroll.Visible);
        AssertControlsReachable(viewport); AssertNavigationFollowsContent(form);
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
        var expectedLabels = new List<string> { "Set up " + title, "Run these in PowerShell.", "1. Install", "2. Sign in" };
        if (!isCodex) expectedLabels.Add("Open a new PowerShell window.");
        Assert.Equal(expectedLabels,
            Labels(form).Where(l => l.Visible && l is not LinkLabel).Select(l => l.Text).ToArray());
        Assert.Equal(new[] { "Back", "Continue", "Copy", "Copy" },
            Descendants(form).OfType<Button>().Where(b => b.Visible).Select(b => b.Text).Order().ToArray());
        Assert.DoesNotContain(Descendants(form).OfType<CheckBox>(), c => c.Visible);
        Assert.DoesNotContain(Labels(form), l => l.Visible && l.AccessibleName?.EndsWith("setup status", StringComparison.Ordinal) == true);
        var help = Descendants(form).OfType<LinkLabel>().Single(l => l.Visible);
        Assert.Equal("Setup help", help.Text); Assert.Equal(title + " setup help", help.AccessibleName);
        Assert.True(help.TabStop);
        var illustration = Assert.Single(Descendants(form).OfType<SetupAnimation>());
        Assert.Contains(title, illustration.AccessibleName);
        Assert.False(illustration.TabStop);
        for (var pass = 0; pass < 5; pass++) form.RefreshStatuses();
        Assert.Equal(0, reads); Assert.Equal(0, saves); Assert.Empty(requests);

        var next = Assert.IsAssignableFrom<Button>(form.AcceptButton); next.PerformClick();
        Assert.True(illustration.IsDisposed);
        if (isCodex && preferences.ClaudeEnabled)
        {
            Assert.Equal(SetupStep.Claude, flow.Step);
            Assert.Equal(0, reads); Assert.Empty(requests); next.PerformClick();
        }
        Assert.Equal(SetupStep.Verify, flow.Step); Assert.True(reads > 0);
        Assert.Single(requests); Assert.Null(requests[0]);
        Assert.Contains(Descendants(form).OfType<Button>(), b => b.Visible && b.Text == "Retry");
        Assert.DoesNotContain(Descendants(form).OfType<Button>(), b => b.Text == "Copy Diagnostics");
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
            Assert.Equal(2, Descendants(form).OfType<ToggleSwitch>().Count());
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

            var next = Assert.IsAssignableFrom<Button>(form.AcceptButton); next.PerformClick();
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
            Assert.DoesNotContain(Descendants(form).OfType<Button>(), b => b.Text == "Copy Diagnostics");
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
        var body = Panel(form, "setupViewport");
        foreach (var control in Descendants(body).Where(c => c is System.Windows.Forms.Button or TextBox))
        {
            body.ScrollControlIntoView(control);
            Assert.True(body.ClientRectangle.Contains(body.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))), "Unreachable guide control: " + control.AccessibleName);
        }
    });

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public Task GuidesKeepNavigationVisibleAtDefaultSizeAndScrollOnlyVertically(int step) => Sta(() =>
    {
        using var form = FormAt((SetupStep)step, () => [Ready("Codex"), Ready("Claude Code")], _ => { }, () => new(), _ => { });
        form.Show();
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        form.ClientSize = new Size(S(620), S(580)); form.PerformLayout(); Application.DoEvents();
        var viewport = Panel(form, "setupViewport");
        var footer = Panel(form, "setupFixedNavigation");
        var navigation = Panel(form, "setupNavigation");
        var illustration = Assert.Single(Descendants(form).OfType<SetupAnimation>());
        Assert.True(footer.Visible); Assert.Same(footer, navigation.Parent);
        Assert.True(viewport.VerticalScroll.Visible); Assert.False(viewport.HorizontalScroll.Visible);
        Assert.InRange(Math.Abs(illustration.Width * 9d / 16 - illustration.Height), 0, 1);
        foreach (var scroll in new[] { 0, viewport.VerticalScroll.Maximum, 0 })
        {
            viewport.AutoScrollPosition = new Point(0, scroll); form.PerformLayout(); Application.DoEvents();
            Assert.False(viewport.HorizontalScroll.Visible); Assert.Equal(0, viewport.AutoScrollPosition.X);
            foreach (var button in new[] { Button(form, "Back"), Assert.IsAssignableFrom<Button>(form.AcceptButton) })
                Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))));
            Assert.True(viewport.Bottom <= footer.Top);
        }
        viewport.ScrollControlIntoView(illustration);
        Assert.True(viewport.ClientRectangle.Contains(viewport.RectangleToClient(illustration.RectangleToScreen(illustration.ClientRectangle))));
        AssertControlsReachable(viewport);
    });

    [Theory]
    [InlineData("Compact Monitor")] [InlineData("Tray Icon")] [InlineData("Appearance")]
    public Task RejectedPreferencesRestoreAuthoritativeControlsOnceAndCanRecover(string setting) => Sta(() =>
    {
        var preferences = new Preferences(); var reject = true; var writes = 0;
        using var form = FormAt(SetupStep.Preferences, () => [], _ => { }, () => preferences,
            requested => { writes++; if (!reject) preferences = requested; });
        form.Show();
        var appearance = Descendants(form).OfType<ComboBox>().Single();
        var compact = Descendants(form).OfType<CheckBox>().Single(c => c.Text == "Compact Monitor");
        var tray = Descendants(form).OfType<CheckBox>().Single(c => c.Text == "Tray Icon");
        void Change()
        {
            if (setting == "Appearance") appearance.SelectedIndex = (int)Appearance.Dark;
            else (setting == "Compact Monitor" ? compact : tray).Checked = false;
        }
        Change(); Assert.Equal(1, writes); Assert.Equal(new Preferences(), preferences);
        Assert.True(compact.Checked); Assert.True(tray.Checked); Assert.Equal((int)Appearance.Light, appearance.SelectedIndex);
        Assert.Contains(Labels(form), label => label.Visible && label.Text == "Settings could not be saved. Please try again.");
        reject = false; Change(); Assert.Equal(2, writes);
        Assert.Equal(setting != "Compact Monitor", preferences.CompactMonitor);
        Assert.Equal(setting != "Tray Icon", preferences.TrayIcon);
        Assert.Equal(setting == "Appearance" ? Appearance.Dark : Appearance.Light, preferences.Appearance);
        Assert.DoesNotContain(Labels(form), label => label.Visible && label.Text.Contains("could not be saved", StringComparison.Ordinal));
    });

    [Fact]
    public Task StartupRejectionAndUnavailableReadbackStayVisibleWithoutRepeatedWrites() => Sta(() =>
    {
        var startup = new RejectableStartup();
        using var form = FormAt(SetupStep.Preferences, () => [], _ => { }, () => new(), _ => { }, startup,
            () => { if (startup.TryRead(out var current)) startup.TrySet(!current); });
        form.Show(); var launch = Descendants(form).OfType<CheckBox>().Single(c => c.Text == "Launch at Startup");
        void Click() => typeof(CheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(launch, [EventArgs.Empty]);
        Click(); Assert.Equal(1, startup.Writes); Assert.False(launch.Checked); Assert.False(startup.Enabled);
        Assert.Contains(Labels(form), label => label.Visible && label.Text == "Startup setting could not be saved. Please try again.");
        startup.Reject = false; Click(); Assert.Equal(2, startup.Writes); Assert.True(launch.Checked); Assert.True(startup.Enabled);
        Assert.DoesNotContain(Labels(form), label => label.Visible && label.Text.Contains("could not be saved", StringComparison.Ordinal));
        startup.BecomeUnavailable = true; Click(); Assert.Equal(3, startup.Writes); Assert.False(launch.Enabled);
        Assert.False(launch.Checked);
        Assert.Contains(Labels(form), label => label.Visible && label.Text == "Startup setting is unavailable. Please try again.");
        form.RefreshStatuses(); Assert.Equal(3, startup.Writes);
    });

    [Fact]
    public Task StartupPolicyTransitionPreservesEnabledAuthoritativeValueWhileDisablingControl() => Sta(() =>
    {
        var startup = new RejectableStartup { Reject = false, EnabledByPolicyAfterWrite = true };
        using var form = FormAt(SetupStep.Preferences, () => [], _ => { }, () => new(), _ => { }, startup,
            () => { if (startup.TryRead(out var current)) startup.TrySet(!current); });
        form.Show(); var launch = Descendants(form).OfType<CheckBox>().Single(c => c.Text == "Launch at Startup");
        Assert.False(launch.Checked); Assert.True(launch.Enabled);
        typeof(CheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(launch, [EventArgs.Empty]);
        Assert.Equal(1, startup.Writes); Assert.True(startup.Enabled); Assert.True(launch.Checked); Assert.False(launch.Enabled);
        Assert.Contains(Labels(form), label => label.Visible && label.Text == "Startup setting is unavailable. Please try again.");
    });

    [Fact]
    public Task VerificationAccessibilityFollowsReadinessAndRetainsSafeStaleDetails() => Sta(() =>
    {
        ProviderState[] states = [new("Codex", ProviderStatus.Error, Failure: FailureKind.Malformed), new("Claude Code", ProviderStatus.Unavailable, Enabled: false)];
        using var form = FormAt(SetupStep.Verify, () => states, _ => { }, () => new(), _ => { });
        form.Show(); var next = Assert.IsAssignableFrom<Button>(form.AcceptButton);
        Assert.Equal("Finish Anyway", next.Text); Assert.Equal(next.Text, next.AccessibilityObject.Name);
        var status = Labels(form).Single(label => label.AccessibleName == "Codex setup status");
        Assert.Contains("Sign-in not verified", status.AccessibilityObject.Description);
        Assert.Contains("Allowance format not supported", status.AccessibilityObject.Description);
        var cached = Ready("Codex");
        states[0] = cached with { Snapshot = cached.Snapshot! with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-5), IsCached = true },
            Failure = FailureKind.Network, Detail = "private@example.test" };
        form.RefreshStatuses();
        Assert.Equal("Continue", next.Text); Assert.Equal(next.Text, next.AccessibilityObject.Name);
        Assert.Contains("Signed in", status.AccessibilityObject.Description); Assert.Contains("stale", status.AccessibilityObject.Description);
        Assert.Contains("cached reading", status.AccessibilityObject.Description); Assert.Contains("last retrieval failed", status.AccessibilityObject.Description);
        Assert.DoesNotContain("private@example.test", status.AccessibilityObject.Description);
        states[0] = states[0] with { Status = ProviderStatus.Loading }; form.RefreshStatuses();
        Assert.Equal("Checking…", status.Text); Assert.Contains("Signed in", status.AccessibilityObject.Description);
        Assert.Contains("Checking allowances", status.AccessibilityObject.Description);
        states[0] = states[0] with { Enabled = false }; form.RefreshStatuses();
        Assert.Equal("Monitoring off", status.Text); Assert.Equal(status.Text, status.AccessibilityObject.Description);
        Assert.Equal("Finish Anyway", next.AccessibilityObject.Name);
    });

    [Fact]
    public Task CompletionDoesNotReadProviderStateAndRequiresSuccessfulSaveBeforeClosing() => Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Llumi-completion-fixture-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "blocked"); File.WriteAllText(blocker, "fixture");
        try
        {
            var completion = new SetupCompletionStore(Path.Combine(blocker, "setup.json"));
            var flow = new SetupFlow(completion); while (flow.Step != SetupStep.Done) flow.Next();
            var reads = 0; var checks = 0; var finished = 0;
            using var form = new SetupForm(flow, () => { reads++; return []; }, _ => checks++, () => new(), _ => { }, new Startup(), () => { }, () => finished++);
            form.Show(); var next = Assert.IsAssignableFrom<Button>(form.AcceptButton);
            Assert.Equal("Start Llumi", next.AccessibilityObject.Name);
            for (var pass = 0; pass < 3; pass++) form.RefreshStatuses();
            Assert.Equal(0, reads); Assert.Equal(0, checks);
            next.PerformClick(); Assert.False(completion.IsComplete()); Assert.Equal(0, finished); Assert.False(form.IsDisposed);
            Assert.Contains(Labels(form), label => label.Visible && label.Text.Contains("Setup completion could not be saved", StringComparison.Ordinal));
            File.Delete(blocker); next.PerformClick(); Assert.True(completion.IsComplete()); Assert.Equal(1, finished); Assert.True(form.IsDisposed);
        }
        finally { Directory.Delete(directory, true); }
    });

    private static ProviderState Ready(string name) => new(name, ProviderStatus.Ready,
        new([new("five_hour", "synthetic", 20, DateTimeOffset.UtcNow.AddHours(5))], DateTimeOffset.UtcNow, "fixture"), Authentication: AuthenticationStatus.Verified);
    private static SetupForm FormAt(SetupStep step, Func<IReadOnlyList<ProviderState>> states, Action<string?> refresh,
        Func<Preferences> preferences, Action<Preferences> save, IStartupRegistration? startup = null, Action? toggleStartup = null)
    {
        var flow = new SetupFlow(new(Path.Combine(Path.GetTempPath(), "Llumi-setup-fixture-" + Guid.NewGuid(), "completion.json")));
        while (flow.Step != step) flow.Next();
        return new(flow, states, refresh, preferences, save, startup ?? new Startup(), toggleStartup ?? (() => { }), () => { });
    }
    private static Button Button(Form form, string text) => Descendants(form).OfType<Button>().Single(b => b.Text == text);
    private static Panel Panel(Form form, string name) => Descendants(form).OfType<Panel>().Single(panel => panel.Name == name);
    private static void AssertNavigationFollowsContent(Form form)
    {
        var content = Panel(form, "setupContent"); var navigation = Panel(form, "setupNavigation"); var page = Panel(form, "setupPage");
        var next = Assert.IsAssignableFrom<Button>(form.AcceptButton); var back = Button(form, "Back");
        Assert.True(navigation.Visible); Assert.Same(page, content.Parent);
        var footer = Panel(form, "setupFixedNavigation");
        if (footer.Visible)
        {
            Assert.Same(footer, navigation.Parent);
            Assert.True(footer.ClientRectangle.Contains(navigation.Bounds));
            Assert.True(form.ClientRectangle.Contains(footer.Bounds));
        }
        else
        {
            Assert.Same(page, navigation.Parent);
            Assert.InRange(navigation.Top - content.Bottom, 0, (int)Math.Round(40 * form.DeviceDpi / 96d));
            Assert.True(page.ClientRectangle.Contains(navigation.Bounds));
        }
        Assert.True(navigation.ClientRectangle.Contains(next.Bounds)); Assert.True(navigation.ClientRectangle.Contains(back.Bounds));
        Assert.Equal(back.Top, next.Top); Assert.Equal(back.Height, next.Height);
        Assert.Equal(next.Text, next.AccessibilityObject.Name);
    }
    private static void AssertControlsReachable(Panel viewport)
    {
        foreach (var control in Descendants(viewport).Where(c => c.Visible && c is System.Windows.Forms.Button or CheckBox or TextBox or ComboBox or LinkLabel))
        {
            viewport.ScrollControlIntoView(control);
            Assert.True(viewport.ClientRectangle.Contains(viewport.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))),
                "Unreachable setup control: " + (control.AccessibleName ?? control.Text) + $" bounds={viewport.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))}, scroll={viewport.AutoScrollPosition}, padding={viewport.Padding}, view={viewport.ClientSize}, display={viewport.DisplayRectangle}");
            if (control.Enabled && control.TabStop) { control.Select(); Assert.True(control.Focused); }
        }
    }
    private static CheckBox Switch(Form form, string provider) => Descendants(form).OfType<CheckBox>().Single(c => c.AccessibleName == "Monitor " + provider);
    private static IEnumerable<Label> Labels(Control root) => Descendants(root).OfType<Label>();
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private sealed class Startup : IStartupRegistration
    {
        public bool TryRead(out bool enabled) { enabled = false; return true; }
        public bool TrySet(bool enabled) => true;
    }
    private sealed class RejectableStartup : IStartupRegistration
    {
        internal bool Enabled;
        internal bool Reject = true;
        internal bool Available = true;
        internal bool BecomeUnavailable;
        internal bool EnabledByPolicyAfterWrite;
        internal int Writes;
        public bool TryRead(out bool enabled) { enabled = Enabled; return Available; }
        public bool TrySet(bool enabled)
        {
            Writes++; if (BecomeUnavailable) Available = false;
            if (EnabledByPolicyAfterWrite) { Enabled = true; Available = false; return false; }
            if (Reject) return false; Enabled = enabled; return true;
        }
    }
    private static async Task Sta(Action action, TimeSpan? timeout = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(8));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
}
