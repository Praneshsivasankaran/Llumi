using System.Globalization;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class PopupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 16, 0, 0, TimeSpan.Zero);

    private static ProviderState Live(double? used = 47, DateTimeOffset? reset = null) =>
        new("Codex", ProviderStatus.Ready,
            new UsageSnapshot([new UsageWindow("codex/secondary", "Codex · 7 days", used, reset, 10080)], Now, "fixture"));

    [Fact]
    public void FreshValueBecomesStaleAfterAgeOrResetWithoutInventingNewAllowance()
    {
        var state = Live(reset: Now.AddMinutes(1));
        Assert.Equal("Live", PopupText.Status(state, Now));
        Assert.Equal("Stale", PopupText.Status(state, Now.AddMinutes(1)));
        Assert.Equal("53%", PopupText.Remaining(state.Snapshot!.Windows[0]));
        Assert.Equal("Reset passed · awaiting update", PopupText.Reset(state.Snapshot.Windows[0], Now.AddMinutes(1)));
        Assert.Equal("Stale", PopupText.Status(Live(), Now.AddMinutes(3)));
    }

    [Theory]
    [InlineData(FailureKind.NotInstalled, "Not installed")]
    [InlineData(FailureKind.LoggedOut, "Not signed in")]
    [InlineData(FailureKind.Unsupported, "Unsupported format")]
    [InlineData(FailureKind.Network, "Unavailable")]
    [InlineData(FailureKind.Timeout, "Unavailable")]
    [InlineData(FailureKind.Malformed, "Unsupported format")]
    public void MissingProviderStatusNeverClaimsLive(FailureKind failure, string expected)
    {
        var state = new ProviderState("provider", ProviderStatus.Unavailable, Failure: failure);
        Assert.Equal(expected, PopupText.Status(state, Now));
        Assert.DoesNotContain("fixture", PopupText.Summary(state, Now));
    }

    [Fact]
    public void RetainedDataShowsStaleOnFailureAndOriginalObservationAgeDuringRefresh()
    {
        var failed = Live() with { Status = ProviderStatus.Error, Failure = FailureKind.Network };
        Assert.Equal("Stale", PopupText.Status(failed, Now.AddSeconds(40)));
        Assert.Equal("Updated 40s ago", PopupText.Summary(failed, Now.AddSeconds(40)));
        var refreshing = failed with { Status = ProviderStatus.Loading };
        Assert.Equal("Refreshing…", PopupText.Status(refreshing, Now.AddSeconds(40)));
        Assert.Equal("Updated 40s ago", PopupText.Summary(refreshing, Now.AddSeconds(40)));
        Assert.Equal("Checking local provider", PopupText.Summary(new ProviderState("provider", ProviderStatus.Loading), Now));
    }

    [Fact]
    public void CachedAndFutureObservationsDoNotAppearLive()
    {
        var state = Live();
        Assert.Equal("Stale", PopupText.Status(state with { Snapshot = state.Snapshot! with { IsCached = true } }, Now));
        Assert.Equal("Stale", PopupText.Status(state with { Snapshot = state.Snapshot! with { ObservedAt = Now.AddMinutes(2) } }, Now));
        Assert.Equal("at an invalid future time", PopupText.Age(Now.AddMinutes(2), Now));
        Assert.Equal("0s ago", PopupText.Age(Now.AddSeconds(20), Now));
    }

    [Fact]
    public void ProviderNoticeKeepsObservationAgeVisible()
    {
        var state = Live() with { Detail = "Provider has a restriction." };
        Assert.Equal("Updated 3m ago · provider notice", PopupText.Summary(state, Now.AddMinutes(3)));
        Assert.Equal("Stale", PopupText.Status(state, Now.AddMinutes(3)));
    }

    [Theory]
    [InlineData(59, "59s ago")]
    [InlineData(60, "1m ago")]
    [InlineData(3599, "59m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(86399, "23h ago")]
    [InlineData(86400, "1d ago")]
    public void ObservationAgeUsesExplicitUnitsAtBoundaries(int seconds, string expected) =>
        Assert.Equal(expected, PopupText.Age(Now.AddSeconds(-seconds), Now));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1d, "—")]
    [InlineData(101d, "—")]
    [InlineData(0d, "100%")]
    [InlineData(100d, "0%")]
    [InlineData(47.5, "52.5%")]
    public void UnknownAndInvalidValuesNeverBecomeZeroOrFull(double? used, string expected) =>
        Assert.Equal(expected, PopupText.Remaining(new UsageWindow("id", "window", used, null)));

    [Fact]
    public void PercentDisplayIsCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("52.5%", PopupText.Remaining(new UsageWindow("id", "window", 47.5, null)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MissingAndExpiredResetHaveNoCountdown()
    {
        Assert.Equal("Reset unavailable", PopupText.Reset(new UsageWindow("id", "window", 12, null), Now));
        Assert.Equal("Reset passed · awaiting update", PopupText.Reset(new UsageWindow("id", "window", 12, Now.AddSeconds(-1)), Now));
    }

    [Theory]
    [InlineData(1, "Reset in 1m")]
    [InlineData(3600, "Reset in 1h 0m")]
    [InlineData(3660, "Reset in 1h 1m")]
    [InlineData(86400, "Reset in 1d 0h")]
    [InlineData(176400, "Reset in 2d 1h")]
    public void CountdownComparesInstantsAcrossTimezoneOffsets(int seconds, string expected)
    {
        var reset = Now.AddSeconds(seconds).ToOffset(TimeSpan.FromHours(5.5));
        Assert.Equal(expected, PopupText.Reset(new UsageWindow("id", "window", 12, reset), Now.ToOffset(TimeSpan.FromHours(-7))));
    }

    [Fact]
    public void LabelsUseConciseScopeNamesWithoutRawProviderGeneratedNames()
    {
        Assert.Equal("Weekly limit", PopupText.WindowName("Codex", new UsageWindow("one", "raw", 1, null, 10080, UsageScope.General)));
        Assert.Equal("Spark", PopupText.WindowName("Codex", new UsageWindow("two", "raw", 2, null, 10080, UsageScope.Model, "Spark")));
        Assert.Equal("Research limit", PopupText.WindowName("Codex", new UsageWindow("three", "raw", 3, null, 2880, UsageScope.Additional, "Research")));
        Assert.Equal("", PopupText.WindowName("Codex", new UsageWindow("four", "raw", null, null)));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1040, 1518, 528)] // Bottom taskbar.
    [InlineData(0, 0, 1920, 1080, 0, 40, 1920, 1040, 1518, 52)] // Top taskbar.
    [InlineData(0, 0, 1920, 1080, 40, 0, 1880, 1080, 52, 568)] // Left taskbar.
    [InlineData(0, 0, 1920, 1080, 0, 0, 1880, 1080, 1478, 568)] // Right taskbar.
    [InlineData(-1920, -200, 1920, 1080, -1920, -200, 1920, 1040, -402, 328)]
    [InlineData(1920, 0, 1920, 1080, 1920, 0, 1920, 1040, 3438, 528)]
    public void PopupAnchorsInsideCorrectMonitorWorkArea(int sx, int sy, int sw, int sh,
        int wx, int wy, int ww, int wh, int expectedX, int expectedY)
    {
        var screen = new Rectangle(sx, sy, sw, sh);
        var work = new Rectangle(wx, wy, ww, wh);
        var size = new Size(390, 500);
        var result = PopupPlacement.NearTray(screen, work, size, 12);
        Assert.Equal(new Point(expectedX, expectedY), result);
        Assert.True(work.Contains(new Rectangle(result, size)));
    }

    [Fact]
    public void DraggedPanelClampsToMonitorWithoutLosingNegativeCoordinates()
    {
        var work = new Rectangle(-1920, -200, 1920, 1040);
        var size = new Size(390, 500);
        Assert.Equal(new Point(-1920, -200), PopupPlacement.Clamp(new Point(-4000, -1000), size, work));
        Assert.Equal(new Point(-390, 340), PopupPlacement.Clamp(new Point(4000, 1000), size, work));
        Assert.Equal(new Point(-1200, 50), PopupPlacement.Clamp(new Point(-1200, 50), size, work));
    }

    [Fact]
    public void OversizedPanelKeepsItsHeaderOnScreenAndNeverThrows()
    {
        var work = new Rectangle(-800, 100, 300, 200);
        var size = new Size(390, 500);
        Assert.Equal(work.Location, PopupPlacement.Clamp(new Point(2000, -2000), size, work));
        Assert.Equal(work.Location, PopupPlacement.NearTray(work, work, size, 12));
    }

    [Fact]
    public void CloseHidesReusableMainWindowAndTrayDisabledCloseMinimizes() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application);
        Assert.False(form.Visible); Assert.True(form.ShowInTaskbar);
        form.ShowPanel(Screen.PrimaryScreen!.WorkingArea.Location);
        form.Close(); Assert.False(form.Visible); Assert.False(form.IsDisposed);
        form.SetPreferences(new(TrayIcon: false));
        form.ShowPanel(Screen.PrimaryScreen!.WorkingArea.Location);
        form.Close(); Assert.Equal(FormWindowState.Minimized, form.WindowState); Assert.False(form.IsDisposed);
    });
    [Fact]
    public void HiddenPanelRendersRealUnknownAndUnavailableRowsWithoutShowingWindow() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application);
        var codex = new ProviderState("Codex", ProviderStatus.Ready,
            new UsageSnapshot([
                new UsageWindow("codex/primary", "Codex · 5 hours", 47, null, 300),
                new UsageWindow("spark/secondary", "Spark · 7 days", null, Now.AddHours(1), 10080, UsageScope.Model, "Spark")
            ], DateTimeOffset.UtcNow, "fixture"));
        var claude = new ProviderState("Claude Code", ProviderStatus.Unavailable, Failure: FailureKind.Unsupported);
        form.Render([codex, claude], false, false);
        var labels = Descendants(form).OfType<Label>().Select(c => c.Text).ToArray();
        Assert.Contains("53% remaining", labels);
        Assert.Contains("—", labels);
        Assert.Contains("Spark", labels);
        Assert.Contains("Unsupported format", labels);
        Assert.False(form.Visible);
        form.Render([codex, claude], true, true);
        Assert.Contains(Descendants(form).OfType<Button>(), button => button.Text == "Refreshing…" && button.Enabled);
        Assert.Contains(Descendants(form).OfType<Label>(), label => label.Text == "Diagnostic log unavailable");
        Assert.False(form.Visible);
    });

    [Fact]
    public void FixturePanelsKeepImportantTextInsideBoundsAndOptionallyRenderArtifacts() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new UsageSnapshot([
            new UsageWindow("codex/primary", "Codex · 5 hours", 0, now.AddHours(5), 300),
            new UsageWindow("codex/secondary", "Codex · 7 days", 100, now.AddDays(3).AddHours(21), 10080),
            new UsageWindow("spark/primary", "Spark · 5 hours", null, null, 300, UsageScope.Model, "Spark"),
            new UsageWindow("spark/secondary", "Spark · 7 days", 47, now.AddDays(2), 10080, UsageScope.Model, "Spark")
        ], now.AddSeconds(-18), "sanitized test fixture");
        var claude = new ProviderState("Claude Code", ProviderStatus.Unavailable, Failure: FailureKind.Unsupported,
            Detail: ClaudeProvider.UnverifiedAccountMessage);
        var live = new ProviderState("Codex", ProviderStatus.Ready, snapshot);
        form.Render([live, claude], false, false);
        AssertImportantTextFits(form);
        SaveFixtureRender(form, "fixture-live-four-windows.png");
        var expiredWindows = snapshot.Windows.Select(window => new UsageWindow(window.Id, window.Name,
            window.UsedPercent, window.ResetsAt is null ? null : now.AddSeconds(-10), window.DurationMinutes, window.Scope, window.ScopeLabel, window.Bucket)).ToArray();
        var stale = live with
        {
            Status = ProviderStatus.Error, Failure = FailureKind.Network,
            Snapshot = snapshot with { ObservedAt = now.AddMinutes(-8), Windows = expiredWindows }
        };
        form.Render([stale, claude], false, false);
        AssertImportantTextFits(form);
        SaveFixtureRender(form, "fixture-stale-expired-four-windows.png");
        SaveFixtureRender(form, "fixture-compact-four-windows.png");
        Assert.False(form.Visible);
    });

    [Fact]
    public void HiddenButtonsDispatchTheirWiredActionsWithoutShowingPanel() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application);
        form.Render([Live()], false, false);
        var refresh = Assert.Single(Descendants(form).OfType<Button>(), button => button.AccessibleName == "Refresh usage");
        var refreshed = 0; var exited = 0;
        form.RefreshRequested += () => refreshed++;
        form.ExitRequested += () => exited++;
        DispatchHiddenClick(refresh); Assert.Equal(1, refreshed);
        QuitMenu(form).PerformClick(); Assert.Equal(1, exited);
        form.HidePanel(); Assert.False(form.Visible); Assert.False(form.IsDisposed);
    });

    internal static ToolStripMenuItem QuitMenu(UsageForm form)
    {
        var menus = typeof(UsageForm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(ContextMenuStrip))
            .Select(field => (ContextMenuStrip)field.GetValue(form)!);
        return Assert.Single(menus.SelectMany(menu => menu.Items.OfType<ToolStripMenuItem>()),
            item => item.Text is "Quit" or "Quit Llumi" || item.AccessibleName == "Quit Llumi");
    }
    private static void DispatchHiddenClick(Button button) => typeof(Button)
        .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(button, [EventArgs.Empty]);

    [Fact]
    public void MenuDismissalAfterExternalFocusKeepsMainWindowOpen() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex"], SystemIcons.Application);
        form.Render([Live()], false, false);
        form.ShowPanel(Screen.PrimaryScreen!.WorkingArea.Location);
        Application.DoEvents();
        var menuButton = Assert.Single(Descendants(form).OfType<Button>(), button => button.AccessibleName == "Llumi menu");
        DispatchHiddenClick(menuButton);
        var menu = (ContextMenuStrip)typeof(UsageForm).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        using var outside = new Form { ShowInTaskbar = false, Size = new Size(100, 100) };
        outside.Show(); outside.Activate();
        menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
        Application.DoEvents();
        Assert.True(form.Visible);
        Assert.False(form.IsDisposed);
    });

    [Fact]
    public void FiveRealisticWindowsStayCompactWithCohesiveCharcoalSurfaceAndFunctionalBlueBars() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        var now = DateTimeOffset.UtcNow;
        var states = new[] {
            new ProviderState("Codex", ProviderStatus.Ready, new UsageSnapshot([
                new("codex/primary", "Codex · 7 days", 71, now.AddDays(3), 10080),
                new("codex_bengalfox/primary", "GPT-5.3-Codex-Spark · 5 hours", 0, now.AddHours(5), 300, UsageScope.Model, "Spark"),
                new("codex_bengalfox/secondary", "GPT-5.3-Codex-Spark · 7 days", 0, now.AddDays(7), 10080, UsageScope.Model, "Spark")], now, "fixture")),
            new ProviderState("Claude", ProviderStatus.Ready, new UsageSnapshot([
                new("five_hour", "5 hours", 0, now.AddHours(4)), new("seven_day", "7 days", 8, now.AddHours(6))], now, "fixture")) };
        form.Render(states, false, false, new Rectangle(0, 0, 2400, 2400));
        Assert.InRange(form.Height * 96 / form.DeviceDpi, 400, 720);
        Assert.Equal(Palette.Background, form.BackColor);
        var bars = Descendants(form).OfType<UsageBar>().ToArray();
        Assert.Equal(7, bars.Length);
        Assert.All(bars, bar => Assert.Contains(bar.Accent, new[] { Palette.Codex, Palette.Claude }));
        Assert.DoesNotContain(Descendants(form).OfType<Label>(), label => label.Text.Contains("every minute", StringComparison.Ordinal));
        AssertImportantTextFits(form);
    });

    [Fact]
    public void MissingProvidersShowSmallOfficialSetupLinksWithoutAccountControls() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        form.Render([
            new("Codex", ProviderStatus.Unavailable, Failure: FailureKind.LoggedOut),
            new("Claude", ProviderStatus.Unavailable, Failure: FailureKind.NotInstalled)], false, false);
        var links = Descendants(form).OfType<LinkLabel>().ToArray();
        Assert.Equal(2, links.Length);
        Assert.All(links, link => Assert.Equal("Set up →", link.Text));
        Assert.Contains(Descendants(form).OfType<Label>(), label => label.Text == "Install Claude Code");
        Assert.Empty(Descendants(form).OfType<TextBox>());
        AssertImportantTextFits(form);
        SaveFixtureRender(form, "fixture-no-providers-setup.png");
        Assert.False(form.Visible);
    });

    [Fact]
    public void ProductPreviewsUseOnlySyntheticAllowanceInActualNativeRender() => RunSta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        ProviderState[] states = [
            new("Codex", ProviderStatus.Ready, new UsageSnapshot([
                new("codex/primary", "Codex · 5 hours", 71, now.AddHours(5).AddMinutes(12), 300),
                new("codex/secondary", "Codex · 7 days", 49, now.AddDays(3).AddHours(7), 10080)], now, "synthetic demo")),
            new("Claude", ProviderStatus.Ready, new UsageSnapshot([
                new("five_hour", "5 hours", 31, now.AddHours(1).AddMinutes(48)),
                new("seven_day", "7 days", 8, now.AddDays(2).AddHours(6))], now, "synthetic demo"))];
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        form.Render(states, false, false);
        AssertImportantTextFits(form);
        SaveFixtureRender(form, "product-popup.png");
        using var monitor = new MonitorForm(["Codex", "Claude"], SystemIcons.Application);
        monitor.Render(states, now);
        Assert.Equal(["29%", "69%"], monitor.RowValues);
        var directory = Environment.GetEnvironmentVariable("AGENTMETER_RENDER_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        using var image = monitor.CreatePreviewBitmap();
        using var labeled = new Bitmap(image.Width, image.Height + 26);
        using (var graphics = Graphics.FromImage(labeled))
        {
            graphics.Clear(Color.FromArgb(30, 33, 37));
            graphics.DrawImageUnscaled(image, 0, 0);
            using var font = new Font("Segoe UI", 8);
            graphics.DrawString("SYNTHETIC DEMO · native monitor render", font, Brushes.White, 8, image.Height + 5);
        }
        labeled.Save(Path.Combine(directory, "product-monitor.png"), ImageFormat.Png);
        Assert.False(form.Visible); Assert.False(monitor.Visible);
    });

    [Fact]
    public void SwitchingToShorterSameDpiWorkAreaResizesPanelAndKeepsActionsReachable() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application);
        var windows = Enumerable.Range(1, 8).Select(i =>
            new UsageWindow($"bucket-{i}/weekly", $"Bucket {i} · 7 days", i * 10, DateTimeOffset.UtcNow.AddDays(i), 10080, UsageScope.Additional, $"Bucket {i}")).ToArray();
        var states = new[]
        {
            new ProviderState("Codex", ProviderStatus.Ready, new UsageSnapshot(windows, DateTimeOffset.UtcNow, "fixture")),
            new ProviderState("Claude Code", ProviderStatus.Unavailable, Failure: FailureKind.Unsupported)
        };
        var tallWork = new Rectangle(0, 0, 1200, 900);
        var shortWork = new Rectangle(-1200, 0, 1200, 500);
        var gap = (int)Math.Round(24 * form.DeviceDpi / 96f);
        form.Render(states, false, false, tallWork);
        var tallHeight = form.Height;
        Assert.True(tallHeight <= tallWork.Height - gap);
        form.Render(states, false, false, shortWork);
        Assert.True(form.Height <= shortWork.Height - gap);
        Assert.True(form.Height < tallHeight);
        foreach (var button in Descendants(form).OfType<Button>().Where(b => b.Parent?.Name != "settings"))
            Assert.True(button.Parent!.ClientRectangle.Contains(button.Bounds), $"Action '{button.Text}' is outside the resized panel.");
        Assert.NotNull(QuitMenu(form));
        Assert.Contains(Descendants(form).OfType<Button>(), button => button.Text == "Refresh");
        SaveFixtureRender(form, "fixture-short-monitor-scroll.png");
        form.Render(states, false, false, tallWork);
        Assert.Equal(tallHeight, form.Height);
        Assert.False(form.Visible);
    });

    [Fact]
    public void InitialWindowUsesLogicalSizeAndProviderColumnsAdaptToAvailableWidth() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application);
        var work = Screen.FromControl(form).WorkingArea;
        var scale = form.DeviceDpi / 96f;
        Assert.InRange(form.ClientSize.Width, (int)Math.Min(630 * scale, work.Width - 60 * scale), (int)(641 * scale));
        form.Render([Live(), new("Claude Code", ProviderStatus.Unavailable, Failure: FailureKind.LoggedOut)], false, false);
        var cards = Descendants(form).OfType<ProviderCard>().ToArray();
        if (form.ClientSize.Width >= 600 * scale)
        {
            Assert.Equal(cards[0].Top, cards[1].Top);
            Assert.True(cards[0].Right < cards[1].Left);
        }
        form.ClientSize = new Size((int)(400 * scale), (int)(400 * scale));
        Assert.Equal(cards[0].Left, cards[1].Left);
        Assert.True(cards[0].Bottom < cards[1].Top);
    });

    [Fact]
    public void ClaudeCardHidesInternalRowsAndTooltipsWithoutDiscardingParsedWindows() => RunSta(() =>
    {
        using var hints = new ToolTip();
        using var card = new ProviderCard(hints) { Size = new Size(390, 280) };
        var windows = new UsageWindow[] {
            new("seven_day", "provider_generated_weekly_label", 30, null),
            new("iguana_necktie", "iguana_necktie", 1, null),
            new("model:synthetic_model", "synthetic_model", 2, null),
            new("five_hour", "provider_generated_primary_label", 20, null) };
        var state = new ProviderState("Claude Code", ProviderStatus.Ready, new(windows, Now, "fixture"));
        card.Render(state, Now, 1);
        var texts = card.Controls.Cast<Control>().Select(c => c.Text).ToArray();
        Assert.Contains("5-hour limit", texts); Assert.Contains("Weekly limit", texts);
        Assert.Equal(244, card.LogicalHeight);
        var content = string.Join("\n", card.Controls.Cast<Control>().Select(c => c.Text + hints.GetToolTip(c) + c.AccessibleName));
        foreach (var name in new[] { "iguana_necktie", "synthetic_model", "provider_generated_" })
            Assert.DoesNotContain(name, content);
        Assert.Equal(4, state.Snapshot!.Windows.Count);

        card.Render(state with { Snapshot = new([windows[1], windows[2]], Now, "fixture") }, Now, 1);
        Assert.Equal(116, card.LogicalHeight);
        Assert.Contains(card.Controls.Cast<Control>(), c => c.Text == "Unsupported format");
        Assert.DoesNotContain(card.Controls.Cast<Control>(), c => c.Text.Contains("%") || c.Text.Contains("iguana_necktie"));
    });

    [Fact]
    public void ProviderCardsHaveEqualHeightsSharedSwitchesAndBothOffSettingsAction() => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        var at = DateTimeOffset.UtcNow;
        ProviderState[] states = [new("Codex", ProviderStatus.Ready, new([
            new("codex/primary", "raw", 100, at.AddHours(5), 300),
            new("model", "raw", 0, at.AddDays(2), 10080, UsageScope.Model, "Spark")], at, "fixture")),
            new("Claude", ProviderStatus.Ready, new([new("seven_day", "raw", 20, at.AddDays(2))], at, "fixture"))];
        form.Render(states, false, false); form.Show();
        var cards = Descendants(form).OfType<ProviderCard>().ToArray(); Assert.Equal(cards[0].Height, cards[1].Height);
        Assert.All(cards.SelectMany(c => c.Controls.OfType<Label>()), label => Assert.Equal(Color.Transparent, label.BackColor));
        Assert.Contains(Descendants(form).OfType<Label>(), l => l.Text == "0% remaining");
        Preferences? saved = null; form.PreferencesChanged += value => { saved = value; form.SetPreferences(value); };
        form.ShowSettings(); var switches = Descendants(form).OfType<CheckBox>().ToArray();
        switches.Single(c => c.AccessibleName == "Monitor Codex").Checked = false;
        Assert.NotNull(saved); Assert.False(saved.CodexEnabled); Assert.True(saved.ClaudeEnabled);
        switches.Single(c => c.AccessibleName == "Monitor Claude Code").Checked = false;
        form.ShowUsage(); Assert.All(cards, card => Assert.False(card.Visible));
        var action = Descendants(form).OfType<Button>().Single(b => b.Text == "Open Settings"); Assert.True(action.Visible);
        action.PerformClick(); Assert.True(switches[0].Parent!.Visible);
        var resets = 0; form.ResetPositionRequested += () => resets++;
        Descendants(form).OfType<Button>().Single(b => b.Text == "Reset Position").PerformClick(); Assert.Equal(1, resets);
    });

    [Fact]
    public void TransparentLabelBackgroundPaintingPreservesAdjacentPixels() => RunSta(() =>
    {
        using var hints = new ToolTip();
        using var card = new ProviderCard(hints) { Size = new Size(300, 240) };
        using var bitmap = new Bitmap(300, 240);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Magenta);
        var clip = new Rectangle(100, 40, 40, 20);
        graphics.SetClip(clip);
        typeof(ProviderCard).GetMethod("OnPaintBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(card, [new PaintEventArgs(graphics, clip)]);
        Assert.Equal(Color.Magenta.ToArgb(), bitmap.GetPixel(30, 50).ToArgb());
        Assert.Equal(Palette.Card.ToArgb(), bitmap.GetPixel(110, 50).ToArgb());
    });

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ClaudeCardMissingFiveHourUsesGeneralWeeklyBeforeAnyModel(bool fiveHour, bool weekly) => RunSta(() =>
    {
        using var hints = new ToolTip();
        using var card = new ProviderCard(hints) { Size = new Size(390, 280) };
        var windows = new List<UsageWindow> { new("model:five_hour", "synthetic_internal", 0, null) };
        if (weekly) windows.Add(new("seven_day", "raw_weekly", 30, null));
        if (fiveHour) windows.Add(new("five_hour", "raw_primary", 20, null));
        card.Render(new("Claude Code", ProviderStatus.Ready, new(windows, Now, "fixture")), Now, 1);
        var texts = card.Controls.Cast<Control>().Select(c => c.Text).ToArray();
        Assert.Equal(fiveHour, texts.Contains("80% remaining"));
        Assert.Equal(!fiveHour && weekly, texts.Contains("70% remaining"));
        Assert.Equal(weekly, texts.Contains("Weekly limit"));
        var all = string.Join("\n", card.Controls.Cast<Control>().Select(c => c.Text + hints.GetToolTip(c) + c.AccessibleName));
        foreach (var raw in new[] { "model:", "synthetic_internal", "raw_weekly", "raw_primary" })
            Assert.DoesNotContain(raw, all);
        if (!fiveHour && !weekly) Assert.Contains("Unsupported format", texts);
    });

    private static void AssertImportantTextFits(UsageForm form)
    {
        foreach (var label in Descendants(form).OfType<Label>().Where(l => !string.IsNullOrEmpty(l.Text) && !l.Text.Contains("\n") && !l.AutoEllipsis && l.Parent?.Name != "settings"))
        {
            using var graphics = label.CreateGraphics();
            var measured = TextRenderer.MeasureText(graphics, label.Text, label.Font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            Assert.True(measured.Width <= label.ClientSize.Width,
                $"Label '{label.Text}' in {label.Parent?.Name}/{label.Parent?.GetType().Name} at {label.DeviceDpi} DPI requires {measured.Width}px, has {label.ClientSize.Width}px.");
            Assert.True(measured.Height <= label.ClientSize.Height,
                $"Label '{label.Text}' requires {measured.Height}px height, has {label.ClientSize.Height}px.");
            Assert.True(label.Parent!.ClientRectangle.Contains(label.Bounds),
                $"Label '{label.Text}' extends outside its direct parent.");
        }
    }

    private static void SaveFixtureRender(UsageForm form, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("AGENTMETER_RENDER_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Assert.True(Path.IsPathFullyQualified(directory), "Fixture render output must be an absolute directory.");
        Directory.CreateDirectory(directory);
        using var render = RenderHiddenControl(form);
        using var labeled = new Bitmap(form.Width, form.Height + 26);
        using (var graphics = Graphics.FromImage(labeled))
        {
            graphics.Clear(Color.FromArgb(30, 33, 37));
            graphics.DrawImageUnscaled(render, 0, 0);
            using var font = new Font("Segoe UI", 8);
            graphics.DrawString("TEST FIXTURE · synthetic usage · hidden WinForms render", font, Brushes.White, 8, form.Height + 5);
        }
        labeled.Save(Path.Combine(directory, filename), ImageFormat.Png);
        Assert.False(form.Visible);
    }

    private static Bitmap RenderHiddenControl(Control control)
    {
        var result = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(result, control.ClientRectangle);
        // DrawToBitmap skips descendants of a hidden parent. Compose each real
        // control's own rendering without changing native window visibility.
        using var graphics = Graphics.FromImage(result);
        foreach (var child in control.Controls.Cast<Control>().Reverse())
        {
            if (child.Width <= 0 || child.Height <= 0 || child.Name == "settings") continue;
            using var childImage = RenderHiddenControl(child);
            graphics.DrawImageUnscaled(childImage, child.Location);
        }
        return result;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Hidden WinForms test exceeded its deadline.");
        failure?.Throw();
    }
}
