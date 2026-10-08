using System.Reflection;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class TrayContextTests
{
    [Fact]
    public async Task RealMessageLoopStartsHiddenAndKeepsRefreshesIndependent()
    {
        var releaseSlow = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = NewSignal();
        var fastReady = NewSignal();
        var fastUpdated = NewSignal();
        var firstFinished = NewSignal();
        var secondFinished = NewSignal();
        var slow = new FakeProvider("Claude Code", async token =>
        {
            slowStarted.TrySetResult();
            return await releaseSlow.Task.WaitAsync(token);
        });
        var fast = new FakeProvider("Codex", _ => Task.FromResult(GoodResult()));
        var clock = new TrayClock();
        var coordinator = new RefreshCoordinator([fast, slow], minimumInterval: TimeSpan.Zero, clock: clock);
        coordinator.Changed += () =>
        {
            if (coordinator.States[0].Status == ProviderStatus.Ready) fastReady.TrySetResult();
            if (coordinator.States[0].Status == ProviderStatus.Ready && fast.Calls >= 2) fastUpdated.TrySetResult();
            if (!coordinator.IsRefreshing && fast.Calls == 2) firstFinished.TrySetResult();
            if (!coordinator.IsRefreshing && fast.Calls >= 3) secondFinished.TrySetResult();
        };

        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fastReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(popup.Visible);
            Assert.True(popup.ShowInTaskbar);
            Assert.True(Field<NotifyIcon>(context, "tray").Visible);
            Assert.DoesNotContain(Field<ContextMenuStrip>(context, "menu").Items.Cast<ToolStripItem>(),
                item => item.Text?.Contains("Setup Llumi", StringComparison.Ordinal) == true);
            Assert.True(Field<System.Windows.Forms.Timer>(context, "poll").Enabled);
            Assert.False(Field<System.Windows.Forms.Timer>(context, "display").Enabled);
            Assert.False(Field<Task>(context, "activeRefresh").IsCompleted);
            Assert.Equal(ProviderStatus.Ready, coordinator.States[0].Status);
            Assert.Equal(ProviderStatus.Loading, coordinator.States[1].Status);

            var dispatched = NewSignal();
            popup.BeginInvoke(() => dispatched.TrySetResult());
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Click(popup, "Refresh usage");
            await fastUpdated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, fast.Calls);
            Assert.Equal(1, slow.Calls);

            releaseSlow.TrySetResult(ProviderResult.Fail(FailureKind.Network));
            await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(ProviderStatus.Ready, coordinator.States[0].Status);
            Assert.Equal(FailureKind.Network, coordinator.States[1].Failure);
            // Exercise the actual periodic Tick wiring with a short test-only interval.
            var poll = Field<System.Windows.Forms.Timer>(context, "poll");
            Assert.Equal(1000, poll.Interval);
            clock.Advance(TimeSpan.FromSeconds(31));
            poll.Interval = 100;
            await secondFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            poll.Stop();
            Assert.Equal(3, fast.Calls);
            Assert.Equal(2, slow.Calls);
            Assert.False(popup.Visible);
        });
    }

    [Fact]
    public async Task SettingsRetryChecksAnEligibleProviderWhileTheOtherQueryRemainsInFlight()
    {
        var slowStarted = NewSignal(); var fastReady = NewSignal(); var fastRetried = NewSignal();
        var slow = new FakeProvider("Claude Code", async token =>
        {
            slowStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return GoodResult();
        });
        var fast = new FakeProvider("Codex", _ => Task.FromResult(GoodResult()));
        var coordinator = new RefreshCoordinator([fast, slow], minimumInterval: TimeSpan.Zero);
        coordinator.Changed += () =>
        {
            if (coordinator.States[0].Status != ProviderStatus.Ready) return;
            fastReady.TrySetResult();
            if (fast.Calls >= 2) fastRetried.TrySetResult();
        };
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fastReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            context.OpenPanel(); popup.ShowSettings();
            var retry = Descendants(popup).OfType<Button>().Single(button => button.AccessibleName == "Retry provider checks");
            Assert.True(coordinator.IsRefreshing); Assert.True(retry.Enabled); Assert.Equal("Retry", retry.Text);
            retry.PerformClick();
            await fastRetried.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, fast.Calls); Assert.Equal(1, slow.Calls);
            Assert.Equal(ProviderStatus.Loading, coordinator.States[1].Status);
        });
    }

    [Fact]
    public async Task HiddenMonitorClearsCachedAccessibilityReadingsWhenProvidersAreDisabled()
    {
        var coordinator = new RefreshCoordinator([
            new FakeProvider("Codex", _ => Task.FromResult(GoodResult())),
            new FakeProvider("Claude Code", _ => Task.FromResult(GoodResult()))
        ]);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            var monitor = Field<MonitorForm>(context, "monitor");
            Assert.False(monitor.Visible);
            monitor.Render(coordinator.States);
            var codex = monitor.AccessibilityObject.GetChild(0)!;
            var claude = monitor.AccessibilityObject.GetChild(1)!;
            Assert.Equal("53%", codex.Value); Assert.Equal("53%", claude.Value);

            Change(context, new Preferences(CodexEnabled: false));
            Assert.False(monitor.Visible);
            Assert.Null(codex.Value); Assert.True(codex.State.HasFlag(AccessibleStates.Unavailable));
            Assert.Equal("53%", claude.Value);
            Assert.Equal(1, monitor.AccessibilityObject.GetChildCount());

            Change(context, new Preferences(CodexEnabled: false, ClaudeEnabled: false));
            Assert.False(monitor.Visible);
            Assert.Null(codex.Value); Assert.Null(claude.Value);
            Assert.True(claude.State.HasFlag(AccessibleStates.Unavailable));
            Assert.Equal(0, monitor.AccessibilityObject.GetChildCount());
        });
    }

    [Fact]
    public async Task SettingsStartupFailuresShowFeedbackRestoreTheSwitchAndRecover()
    {
        var coordinator = new RefreshCoordinator([new FakeProvider("Codex", _ => Task.FromResult(GoodResult()))]);
        await RunMessageLoop(coordinator, (context, popup) =>
        {
            var registration = (MemoryStartup)Field<IStartupRegistration>(context, "startup");
            context.OpenPanel(); popup.ShowSettings();
            var launch = Descendants(popup).OfType<ToggleSwitch>().Single(control => control.Text == "Launch at Startup");
            registration.RejectWrites = true;
            launch.Checked = true;
            Assert.Equal(1, registration.Writes); Assert.False(launch.Checked); Assert.True(launch.Enabled);
            var feedback = Descendants(popup).OfType<Label>().Single(label => label.Visible && label.Text == "Startup setting could not be saved. Please try again.");
            var panel = (Panel)feedback.Parent!;
            Assert.True(panel.ClientRectangle.Contains(feedback.Bounds), "Rejected startup feedback must be brought into the visible viewport.");
            Change(context, new Preferences(CompactMonitor: false));
            context.ResetMonitorPosition();
            Assert.True(feedback.Visible); Assert.Contains("Startup setting could not be saved", feedback.Text);
            registration.RejectWrites = false;
            launch.Checked = true;
            Assert.Equal(2, registration.Writes); Assert.True(launch.Checked); Assert.True(registration.Enabled);
            Assert.False(feedback.Visible); Assert.Equal("", feedback.Text);
            registration.BecomeUnavailableAfterWrite = true;
            launch.Checked = false;
            Assert.Equal(3, registration.Writes); Assert.False(launch.Checked); Assert.False(launch.Enabled);
            Assert.True(feedback.Visible); Assert.Equal("Startup setting is unavailable. Please try again.", feedback.Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SettingsPreferenceAndPositionErrorsClearOnlyAfterTheirOwnSuccessfulRetry()
    {
        var directory = "";
        var coordinator = new RefreshCoordinator([new FakeProvider("Codex", _ => Task.FromResult(GoodResult()))]);
        await RunMessageLoop(coordinator, (context, popup) =>
        {
            context.OpenPanel(); popup.ShowSettings();
            var compact = Descendants(popup).OfType<ToggleSwitch>().Single(control => control.Text == "Show monitor");
            var temporaryPreference = Path.Combine(directory, "preferences.json.tmp");
            Directory.CreateDirectory(temporaryPreference);
            compact.Checked = false;
            Assert.True(compact.Checked);
            var feedback = Descendants(popup).OfType<Label>().Single(label => label.Visible && label.Text.Contains("previous preferences remain active"));
            context.ResetMonitorPosition();
            Assert.True(feedback.Visible); Assert.Contains("previous preferences remain active", feedback.Text);
            Directory.Delete(temporaryPreference);
            compact.Checked = false;
            Assert.False(compact.Checked); Assert.False(feedback.Visible); Assert.Equal("", feedback.Text);

            var positionPath = Path.Combine(directory, "position.json");
            File.Delete(positionPath); Directory.CreateDirectory(positionPath);
            context.ResetMonitorPosition();
            Assert.True(feedback.Visible); Assert.Equal("Monitor position could not be saved. Try again.", feedback.Text);
            compact.Checked = true;
            var launch = Descendants(popup).OfType<ToggleSwitch>().Single(control => control.Text == "Launch at Startup");
            launch.Checked = true;
            Assert.True(launch.Checked); Assert.True(compact.Checked);
            Assert.True(feedback.Visible); Assert.Equal("Monitor position could not be saved. Try again.", feedback.Text);
            Directory.Delete(positionPath);
            context.ResetMonitorPosition();
            Assert.False(feedback.Visible); Assert.Equal("", feedback.Text);
            Assert.NotNull(Field<MonitorPositionStore>(context, "positions").Load());
            return Task.CompletedTask;
        }, storageReady: path => directory = path);
    }

    [Fact]
    public async Task RecoveryBurstsDebounceToOneRefreshAndKeepBothWindowsHidden()
    {
        var recovered = NewSignal();
        var provider = new FakeProvider("Codex", _ => Task.FromResult(GoodResult()));
        var clock = new TrayClock();
        var coordinator = new RefreshCoordinator([provider], minimumInterval: TimeSpan.Zero, clock: clock);
        coordinator.Changed += () => { if (!coordinator.IsRefreshing && provider.Calls == 2) recovered.TrySetResult(); };
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            var timer = Field<System.Windows.Forms.Timer>(context, "recovery");
            Assert.Equal(5_000, timer.Interval);
            clock.Advance(TimeSpan.FromSeconds(31));
            timer.Interval = 100;
            for (var i = 0; i < 20; i++) context.ScheduleRecovery();
            Assert.True(timer.Enabled);
            Assert.Equal(1, provider.Calls);
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(timer.Enabled);
            Assert.Equal(2, provider.Calls);
            Assert.False(popup.Visible);
            Assert.False(Field<MonitorForm>(context, "monitor").Visible);
        });
    }

    [Fact]
    public async Task StartupMenuUsesAuthoritativePersistedStateAndDoesNotClaimFailedWrites()
    {
        var coordinator = new RefreshCoordinator([new FakeProvider("Codex", _ => Task.FromResult(GoodResult()))]);
        await RunMessageLoop(coordinator, (context, popup) =>
        {
            var registration = (MemoryStartup)Field<IStartupRegistration>(context, "startup");
            var menu = Field<ToolStripMenuItem>(context, "startupMenu");
            Assert.False(menu.Checked);
            menu.PerformClick();
            Assert.True(registration.Enabled);
            Assert.True(menu.Checked);
            registration.RejectWrites = true;
            menu.PerformClick();
            Assert.True(registration.Enabled);
            Assert.True(menu.Checked);
            Assert.False(popup.Visible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WiredQuitCancelsCooperativeProviderAndEndsRealMessageLoop()
    {
        var started = NewSignal();
        var stopped = NewSignal();
        var wasCancelled = false;
        var provider = new FakeProvider("Codex", async token =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return GoodResult();
            }
            finally
            {
                wasCancelled = token.IsCancellationRequested;
                stopped.TrySetResult();
            }
        });
        var coordinator = new RefreshCoordinator([provider]);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(coordinator.IsRefreshing);
            Assert.False(Field<Task>(context, "activeRefresh").IsCompleted);
            Assert.False(popup.Visible);
            // Harness invokes the real hidden Quit button's Click handler next.
        });
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(wasCancelled);
        Assert.False(coordinator.IsRefreshing);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ContextualMonitorSharesRefreshAndRepeatedOpenReusesMainWindow()
    {
        var first = NewSignal(); var second = NewSignal();
        var count = 0;
        var provider = new FakeProvider("Codex", _ => Task.FromResult(new ProviderResult(new UsageSnapshot(
            [new UsageWindow("codex/primary", "Core", Interlocked.Increment(ref count) * 10, DateTimeOffset.UtcNow.AddDays(1), 10080)],
            DateTimeOffset.UtcNow, "fixture"))));
        var coordinator = new RefreshCoordinator([provider], minimumInterval: TimeSpan.Zero);
        coordinator.Changed += () =>
        {
            if (!coordinator.IsRefreshing && provider.Calls >= 1) first.TrySetResult();
            if (!coordinator.IsRefreshing && provider.Calls >= 2) second.TrySetResult();
        };
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Field<Task>(context, "activeRefresh").WaitAsync(TimeSpan.FromSeconds(3));
            var monitor = Field<MonitorForm>(context, "monitor");
            context.OpenPanel();
            Assert.True(popup.Visible);
            context.ApplyActivity(new(new(true), new()));
            Assert.True(popup.Visible);
            Assert.True(monitor.Visible);
            Assert.True(monitor.TopMost);
            Assert.False(monitor.ShowInTaskbar);
            Assert.True(monitor.UsesPerPixelTransparency);
            Assert.Equal("90%", Assert.Single(monitor.RowValues));
            Assert.True(Field<System.Windows.Forms.Timer>(context, "display").Enabled);
            var work = Screen.FromControl(monitor).WorkingArea;
            monitor.Location = new Point(work.Left + 90, work.Top + 75);
            monitor.CommitPosition();
            var saved = Field<MonitorPositionStore>(context, "positions").Load();
            Assert.NotNull(saved);
            Assert.Equal(Screen.FromControl(monitor).DeviceName, saved.Display);
            var refresh = Assert.Single(monitor.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>(), item => item.Text == "Refresh");
            refresh.PerformClick();
            await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // Provider completion precedes the asynchronously posted UI notification.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (monitor.RowValues.Single() != "80%" && DateTime.UtcNow < deadline) await Task.Delay(10);
            var rendered = NewSignal();
            popup.BeginInvoke(() => rendered.TrySetResult());
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("80%", Assert.Single(monitor.RowValues));
            // The same handler is used by the cross-process single-instance show signal.
            context.OpenPanel();
            Assert.True(monitor.Visible);
            Assert.True(monitor.TopMost);
            Assert.True(popup.Visible);
            popup.HidePanel();
            context.ApplyActivity(ActivitySnapshot.Empty);
            await Settle(monitor);
            Assert.False(Field<System.Windows.Forms.Timer>(context, "display").Enabled);
            context.ApplyActivity(new(new(true), new()));
            Assert.True(monitor.Visible);
            Assert.Equal(new Point(work.Left + 90, work.Top + 75), monitor.Location);
            context.UnpinMonitor();
            Assert.False(popup.Visible);
            Assert.False(monitor.Visible);
        });
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task QuitWhilePinnedPersistsLocationAndStopsBothWindows()
    {
        var coordinator = new RefreshCoordinator([new FakeProvider("Codex", _ => Task.FromResult(GoodResult()))]);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            context.ApplyActivity(new(new(true), new()));
            var monitor = Field<MonitorForm>(context, "monitor");
            Assert.True(monitor.Visible);
            Assert.False(popup.Visible);
            var done = NewSignal();
            coordinator.Changed += () => { if (!coordinator.IsRefreshing) done.TrySetResult(); };
            if (!coordinator.IsRefreshing) done.TrySetResult();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var quit = Assert.Single(monitor.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>(), item => item.Text == "Quit");
            quit.PerformClick();
            Assert.NotNull(Field<MonitorPositionStore>(context, "positions").Load());
            // Harness awaits the message-loop exit; ExitAsync is idempotent.
        });
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task BothTransitionOrdersReachActualOverlayIncludingDesktopAndDeduplication(bool reverse, bool desktop)
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new RefreshCoordinator([
            new FakeProvider("Codex", _ => Task.FromResult(new ProviderResult(new UsageSnapshot([new("codex/primary", "7 days", 15, now.AddDays(2), 10080)], now, "fixture")))),
            new FakeProvider("Claude Code", _ => Task.FromResult(new ProviderResult(new UsageSnapshot([new("five_hour", "5 hours", 3, now.AddHours(2))], now, "fixture"))))]);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            var monitor = Field<MonitorForm>(context, "monitor");
            monitor.MotionAllowed = () => true;
            // This scenario supplies hover explicitly. Keep the user's real pointer
            // from sending MouseLeave while the native test window changes size.
            // Rendering, animation, activity reconciliation and assertions stay live.
            monitor.Enabled = false;
            var handle = monitor.Handle;
            ActivitySnapshot Snapshot(bool cx, bool cl) => new(
                new(cx && (!desktop || reverse), cx && desktop && !reverse),
                new(cl && (!desktop || !reverse), cl && desktop && reverse));
            foreach (var (cx, cl) in new[] { (false, false), (!reverse, reverse), (true, true), (reverse, !reverse), (false, false) })
            {
                var state = Snapshot(cx, cl);
                context.ApplyActivity(state); await Settle(monitor);
                Assert.Equal(cx || cl, monitor.Visible);
                if (cx || cl)
                {
                    Assert.Equal(state.Providers, monitor.ProviderNames);
                    Assert.Equal(cx && cl ? new[] { "85%", "97%" } : cx ? ["85%"] : ["97%"], monitor.RowValues);
                    Assert.Equal(state.Providers.Length, monitor.AccessibilityObject.GetChildCount());
                    Assert.Equal(MonitorForm.SizeForDpi(monitor.DeviceDpi, state.Providers.Length), monitor.ClientSize);
                }
                Assert.Equal(handle, monitor.Handle);
            }
            context.ApplyActivity(new(new(true, true), new(true, true))); await Settle(monitor);
            Assert.Equal(new[] { "Codex", "Claude Code" }, monitor.ProviderNames);
            Assert.Equal(new[] { "85%", "97%" }, monitor.RowValues);
            monitor.SetExpanded(true); await Settle(monitor);
            context.ApplyActivity(Snapshot(!reverse, reverse)); await Settle(monitor);
            Assert.True(monitor.Expanded); Assert.Single(monitor.RowValues);
            context.ApplyActivity(Snapshot(true, true)); await Settle(monitor);
            Assert.True(monitor.Expanded); Assert.Equal(2, monitor.RowValues.Count);
            context.ApplyActivity(ActivitySnapshot.Empty); await Settle(monitor);
            Assert.False(monitor.Visible); Assert.False(monitor.Expanded); Assert.False(monitor.IsAnimating);
            // An exit interrupted by new activity must not later hide the new state.
            context.ApplyActivity(Snapshot(true, true)); await Settle(monitor);
            context.ApplyActivity(ActivitySnapshot.Empty);
            context.ApplyActivity(Snapshot(!reverse, reverse)); await Settle(monitor);
            Assert.True(monitor.Visible); Assert.Single(monitor.RowValues);
            var version = monitor.RenderVersion;
            await Task.Delay(60);
            Assert.Equal(version, monitor.RenderVersion); // no permanent render loop
        });
    }

    [Fact]
    public async Task BothIncludesUnavailableProviderAndSurvivesIndependentRefresh()
    {
        var ready = new ProviderResult(new UsageSnapshot([new("codex/primary", "7 days", 15, DateTimeOffset.UtcNow.AddDays(1), 10080)], DateTimeOffset.UtcNow, "fixture"));
        var coordinator = new RefreshCoordinator([
            new FakeProvider("Codex", _ => Task.FromResult(ready)),
            new FakeProvider("Claude Code", _ => Task.FromResult(ProviderResult.Fail(FailureKind.Malformed)))]);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            var monitor = Field<MonitorForm>(context, "monitor");
            context.ApplyActivity(new(new(false, true), new(true)));
            await Settle(monitor);
            Assert.Equal(new[] { "85%", "—" }, monitor.RowValues);
            Assert.Equal(2, monitor.ProviderNames.Count);
            monitor.SetExpanded(true, false);
            Assert.Equal(2, monitor.AccessibilityObject.GetChildCount());
            context.ApplyActivity(new(new(), new(true)));
            await Settle(monitor);
            Assert.Equal("—", Assert.Single(monitor.RowValues));
            Assert.True(monitor.Visible);
        });
    }

    private static async Task Settle(MonitorForm monitor)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (monitor.IsAnimating && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.False(monitor.IsAnimating);
    }

    private static async Task RunMessageLoop(RefreshCoordinator coordinator,
        Func<TrayContext, UsageForm, Task> scenario, Preferences? initialPreferences = null,
        Func<ActivitySnapshot>? captureActivity = null, bool waitInitialActivity = true, bool rejectPreferenceWrites = false,
        bool setupComplete = true, string? reviewTitle = null, Action<string>? storageReady = null)
    {
        var completed = NewSignal();
        var logDirectory = Path.Combine(Path.GetTempPath(), "AgentMeter.TrayTests." + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            TrayContext? context = null;
            Exception? failure = null;
            try
            {
                using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                var setupStore = new SetupCompletionStore(Path.Combine(logDirectory, "setup.json"));
                if (setupComplete) setupStore.Save(true);
                var preferenceStore = new PreferenceStore(rejectPreferenceWrites ? logDirectory : Path.Combine(logDirectory, "preferences.json"));
                if (initialPreferences is not null) Assert.True(preferenceStore.Save(initialPreferences));
                context = new TrayContext(coordinator, new DiagnosticLog(logDirectory), showEvent, new MonitorPositionStore(Path.Combine(logDirectory, "position.json")), new MemoryStartup(), captureActivity: captureActivity ?? (() => ActivitySnapshot.Empty), preferenceStore: preferenceStore, setupStore: setupStore, reviewTitle: reviewTitle);
                storageReady?.Invoke(logDirectory);
                var popup = Field<UsageForm>(context, "popup");
                var tray = Field<NotifyIcon>(context, "tray");
                using var watchdog = new System.Threading.Timer(_ =>
                {
                    failure ??= new TimeoutException("Tray test did not exit within its deadline.");
                    try { popup.BeginInvoke(() => context.ExitThread()); }
                    catch (InvalidOperationException) { }
                }, null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
                popup.BeginInvoke(async () =>
                {
                    try
                    {
                        // Scenarios inject explicit activity snapshots. Finish the constructor's
                        // sample and stop periodic Empty samples from hiding the monitor mid-test.
                        Field<System.Windows.Forms.Timer>(context, "activityTimer").Stop();
                        if (waitInitialActivity) await Field<Task>(context, "activityTask");
                        await scenario(context, popup);
                    }
                    catch (Exception exception) { failure = exception; }
                    finally
                    {
                        try { if (!popup.IsDisposed) PopupTests.QuitMenu(popup).PerformClick(); }
                        catch (Exception exception) { failure ??= exception; context.ExitThread(); }
                    }
                });
                Application.Run(context);
                Assert.True(Field<Task>(context, "activeRefresh").IsCompleted);
                Assert.True(Field<CancellationTokenSource>(context, "lifetime").IsCancellationRequested);
                Assert.False(tray.Visible);
                Assert.False(Field<System.Windows.Forms.Timer>(context, "poll").Enabled);
                Assert.False(Field<System.Windows.Forms.Timer>(context, "display").Enabled);
                Assert.False(Field<System.Windows.Forms.Timer>(context, "recovery").Enabled);
                context.Dispose();
                context = null;
                Assert.True(popup.IsDisposed);
                Assert.False(tray.Visible);
            }
            catch (Exception exception) { failure ??= exception; }
            finally
            {
                try { context?.Dispose(); }
                catch (Exception exception) { failure ??= exception; }
                try { if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, true); }
                catch (IOException exception) { failure ??= exception; }
                if (failure is null) completed.TrySetResult();
                else completed.TrySetException(failure);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(18));
    }

    [Fact]
    public async Task ClosingSetupWithTrayDisabledRestoresReachableMainWindow()
    {
        var coordinator = new RefreshCoordinator([new FakeProvider("Codex", _ => Task.FromResult(GoodResult()))]);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            typeof(TrayContext).GetMethod("OpenSetup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null);
            typeof(TrayContext).GetMethod("ChangePreferences", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(context, [new Preferences(false, false, Appearance.Light)]);
            Assert.False(Field<NotifyIcon>(context, "tray").Visible);
            Field<SetupForm>(context, "setupWindow").Close();
            Assert.True(popup.Visible);
            Assert.Equal(FormWindowState.Normal, popup.WindowState);
        });
    }

    [Fact]
    public async Task FreshReviewContextTraversesEveryNativeSetupStepAndCompletesOnlyAtFinish()
    {
        var binding = new AccountBinding("synthetic-first-run-account");
        ProviderResult SignedIn(ProviderResult value) => value with { Authentication = AuthenticationStatus.Verified, VerifiedBinding = binding };
        var coordinator = new RefreshCoordinator([
            new FakeProvider("Codex", _ => Task.FromResult(SignedIn(GoodResult()))),
            new FakeProvider("Claude Code", _ => Task.FromResult(SignedIn(new(new UsageSnapshot([new("five_hour", "fixture", 10, DateTimeOffset.UtcNow.AddHours(2))], DateTimeOffset.UtcNow, "synthetic first-run fixture")))))
        ], minimumInterval: TimeSpan.Zero);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            var setup = Field<SetupForm>(context, "setupWindow");
            var flow = (SetupFlow)typeof(SetupForm).GetField("flow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(setup)!;
            var completion = Field<SetupCompletionStore>(context, "setupStore");
            var saved = Field<PreferenceStore>(context, "preferenceStore");
            var startup = Assert.IsType<MemoryStartup>(Field<IStartupRegistration>(context, "startup"));
            Assert.True(setup.Visible); Assert.False(popup.Visible);
            Assert.Equal(SetupStep.Welcome, flow.Step); Assert.Equal(Enum.GetValues<SetupStep>(), flow.Steps);
            Assert.False(completion.IsComplete()); Assert.False(saved.HasValidExistingPreferences());
            Assert.Equal(new Preferences(), Field<Preferences>(context, "preferences"));
            Assert.Equal(Appearance.Light, Field<Preferences>(context, "preferences").Appearance);
            Assert.Contains("local first-time test", setup.Text); Assert.Contains("local first-time test", popup.Text);
            Assert.Contains("local first-time test", Field<MonitorForm>(context, "monitor").Text);
            var visited = new List<SetupStep>();
            foreach (var step in Enum.GetValues<SetupStep>())
            {
                Assert.Equal(step, flow.Step); visited.Add(step);
                Assert.False(completion.IsComplete());
                Assert.True(Palette.IsLight); Assert.Equal(Color.FromArgb(247, 248, 250), setup.BackColor);
                if (step == SetupStep.Providers)
                {
                    var codex = Descendants(setup).OfType<CheckBox>().Single(c => c.AccessibleName == "Monitor Codex");
                    codex.Checked = false; Assert.False(saved.Load().CodexEnabled);
                    Assert.False(coordinator.States[0].Enabled);
                    Assert.False(completion.RecognizeExisting(saved.HasValidExistingPreferences()));
                    codex.Checked = true; await Field<Task>(context, "activeRefresh");
                    Assert.True(saved.Load().CodexEnabled); Assert.True(coordinator.States[0].Enabled);
                }
                if (step == SetupStep.Preferences)
                {
                    var compact = Descendants(setup).OfType<CheckBox>().Single(c => c.Text == "Compact Monitor");
                    compact.Checked = false; Assert.False(saved.Load().CompactMonitor);
                    var launch = Descendants(setup).OfType<CheckBox>().Single(c => c.Text == "Launch at Startup");
                    typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(launch, [EventArgs.Empty]);
                    Assert.True(startup.Enabled); Assert.True(launch.Checked); Assert.Equal(1, startup.Writes);
                    var appearance = Assert.Single(Descendants(setup).OfType<ComboBox>());
                    Assert.Equal((int)Appearance.Light, appearance.SelectedIndex);
                    Assert.Equal(Appearance.Light, saved.Load().Appearance);
                }
                if (step != SetupStep.Done)
                {
                    var next = Assert.IsAssignableFrom<Button>(setup.AcceptButton);
                    Assert.True(next.Enabled); next.PerformClick();
                }
            }
            Assert.Equal(Enum.GetValues<SetupStep>(), visited);
            Assert.True(Field<bool>(context, "needsSetup"));
            var finish = Descendants(setup).OfType<Button>().Single(b => b.Text == "Start Llumi");
            finish.PerformClick();
            Assert.True(completion.IsComplete()); Assert.False(Field<bool>(context, "needsSetup"));
            Assert.True(popup.Visible); Assert.True(setup.IsDisposed);
            Assert.Equal(new Preferences(CompactMonitor: false, Appearance: Appearance.Light), saved.Load());
            Assert.True(startup.Enabled); Assert.Equal(1, startup.Writes);
            Assert.True(Palette.IsLight); Assert.Equal(Color.FromArgb(247, 248, 250), popup.BackColor);
            popup.ShowSettings();
            var appAppearance = Descendants(popup).OfType<ComboBox>().Single(c => c.AccessibleName == "Appearance");
            Assert.Equal((int)Appearance.Light, appAppearance.SelectedIndex);
            appAppearance.SelectedIndex = (int)Appearance.Dark;
            Assert.Equal(Appearance.Dark, saved.Load().Appearance); Assert.False(Palette.IsLight);
            Assert.Equal(Color.FromArgb(32, 33, 36), popup.BackColor);
        }, setupComplete: false, reviewTitle: "local first-time test");
    }

    [Fact]
    public async Task SavedDisabledProvidersAreAppliedBeforeAnyQueryOrActivityCapture()
    {
        var codex = new FakeProvider("Codex", _ => Task.FromResult(GoodResult()));
        var claude = new FakeProvider("Claude Code", _ => Task.FromResult(GoodResult()));
        var captures = 0;
        var coordinator = new RefreshCoordinator([codex, claude]);
        await RunMessageLoop(coordinator, (context, popup) =>
        {
            Assert.Equal(0, codex.Calls); Assert.Equal(0, claude.Calls); Assert.Equal(0, captures);
            Assert.All(coordinator.States, state => Assert.False(state.Enabled));
            context.ApplyActivity(new(new(true, true), new(true, true)));
            Assert.False(Field<MonitorForm>(context, "monitor").Visible);
            Assert.Empty(Field<MonitorForm>(context, "monitor").ProviderNames);
            return Task.CompletedTask;
        }, new Preferences(CodexEnabled: false, ClaudeEnabled: false),
            () => { Interlocked.Increment(ref captures); return new(new(true), new(true)); });
    }

    [Fact]
    public async Task DisablingCancelsQueryFiltersPendingActivityAndEnableStartsFreshQuery()
    {
        var querying = NewSignal(); var cancelled = NewSignal(); var captureStarted = NewSignal();
        using var releaseActivity = new ManualResetEventSlim();
        var captures = 0;
        var calls = 0;
        var provider = new FakeProvider("Codex", async token =>
        {
            if (Interlocked.Increment(ref calls) > 1) return GoodResult();
            querying.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (token.IsCancellationRequested) cancelled.TrySetResult(); }
            return GoodResult();
        });
        var coordinator = new RefreshCoordinator([provider], minimumInterval: TimeSpan.Zero);
        try
        {
            await RunMessageLoop(coordinator, async (context, popup) =>
            {
                await querying.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Change(context, new Preferences(CodexEnabled: false, ClaudeEnabled: false));
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                releaseActivity.Set();
                await Field<Task>(context, "activityTask");
                Assert.Empty(Field<ActivitySnapshot>(context, "activity").Providers);
                Assert.False(Field<MonitorForm>(context, "monitor").Visible);
                Assert.Null(coordinator.States[0].Snapshot);
                await Field<Task>(context, "activeRefresh");
                Change(context, new Preferences(CodexEnabled: true, ClaudeEnabled: false));
                await Field<Task>(context, "activeRefresh");
                await Field<Task>(context, "activityTask");
                Assert.Equal(2, provider.Calls);
                Assert.True(coordinator.States[0].Enabled);
                Assert.Equal(ProviderStatus.Ready, coordinator.States[0].Status);
                Assert.Equal(new[] { "Codex" }, Field<MonitorForm>(context, "monitor").ProviderNames);
            }, captureActivity: () =>
            {
                if (Interlocked.Increment(ref captures) == 1)
                { captureStarted.TrySetResult(); releaseActivity.Wait(TimeSpan.FromSeconds(3)); }
                return new(new(true), new(true));
            }, waitInitialActivity: false);
        }
        finally { releaseActivity.Set(); }
    }

    [Fact]
    public async Task FailedPreferenceSaveLeavesProviderQueriesAndActivityEnabled()
    {
        var provider = new FakeProvider("Codex", _ => Task.FromResult(GoodResult()));
        var coordinator = new RefreshCoordinator([provider], minimumInterval: TimeSpan.Zero);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            Change(context, new Preferences(CodexEnabled: false, ClaudeEnabled: false));
            Assert.True(Field<Preferences>(context, "preferences").CodexEnabled);
            Assert.True(coordinator.States[0].Enabled);
            context.ApplyActivity(new(new(true), new()));
            Assert.True(Field<MonitorForm>(context, "monitor").Visible);
            Assert.Equal(new[] { "Codex" }, Field<MonitorForm>(context, "monitor").ProviderNames);
        }, rejectPreferenceWrites: true);
    }

    [Fact]
    public async Task ResetPositionRestoresDpiScaledDefaultAndPersistsForNextOpen()
    {
        var coordinator = new RefreshCoordinator([new FakeProvider("Codex", _ => Task.FromResult(GoodResult()))]);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            context.ApplyActivity(new(new(true), new()));
            var monitor = Field<MonitorForm>(context, "monitor");
            monitor.MotionAllowed = () => false;
            await Settle(monitor);
            var screen = Screen.FromControl(monitor);
            monitor.Location = new(screen.WorkingArea.Left + 175, screen.WorkingArea.Top + 140);
            monitor.CommitPosition();
            var reset = Assert.Single(monitor.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>(), item => item.Text == "Reset Position");
            reset.PerformClick();
            var saved = Field<MonitorPositionStore>(context, "positions").Load();
            Assert.NotNull(saved); Assert.Equal(20, saved.Left); Assert.Equal(20, saved.Top);
            var expected = MonitorPosition.Restore(saved, screen.DeviceName, screen.WorkingArea, monitor.Size, monitor.DeviceDpi);
            Assert.Equal(expected, monitor.Location);
            context.ApplyActivity(ActivitySnapshot.Empty);
            context.ApplyActivity(new(new(true), new()));
            Assert.Equal(expected, monitor.Location);
        });
    }

    [Fact]
    public async Task SleepResumePreservesProviderRateLimitEmbargo()
    {
        var clock = new TrayClock();
        var provider = new FakeProvider("Codex", _ => Task.FromResult(ProviderResult.Fail(FailureKind.RateLimited) with { RetryAfter = TimeSpan.FromMinutes(2) }));
        var coordinator = new RefreshCoordinator([provider], minimumInterval: TimeSpan.Zero, clock: clock);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await Field<Task>(context, "activeRefresh");
            var power = typeof(TrayContext).GetMethod("OnPowerModeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            power.Invoke(context, [context, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend)]);
            // Power events marshal through the same message loop as the actual native callback.
            await Task.Delay(20);
            Assert.False(Field<System.Windows.Forms.Timer>(context, "poll").Enabled);
            Assert.False(Field<System.Windows.Forms.Timer>(context, "activityTimer").Enabled);
            clock.Advance(TimeSpan.FromSeconds(30));
            power.Invoke(context, [context, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume)]);
            await Task.Delay(20);
            Click(popup, "Refresh usage");
            await Field<Task>(context, "activeRefresh");
            Assert.Equal(1, provider.Calls);
            Assert.NotNull(coordinator.States[0].RetryAt);
        });
    }

    [Fact]
    public async Task SleepCancelsTheActiveProviderThroughNativePowerEventWiring()
    {
        var started = NewSignal(); var cancelled = NewSignal();
        var provider = new FakeProvider("Codex", async token =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { if (token.IsCancellationRequested) cancelled.TrySetResult(); }
            return GoodResult();
        });
        var coordinator = new RefreshCoordinator([provider]);
        await RunMessageLoop(coordinator, async (context, popup) =>
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var power = typeof(TrayContext).GetMethod("OnPowerModeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            power.Invoke(context, [context, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend)]);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Field<Task>(context, "activeRefresh");
            Assert.False(coordinator.IsRefreshing); Assert.Null(coordinator.States[0].Snapshot);
            Assert.False(Field<System.Windows.Forms.Timer>(context, "poll").Enabled);
            Assert.False(Field<System.Windows.Forms.Timer>(context, "activityTimer").Enabled);
        });
    }

    private static void Change(TrayContext context, Preferences value) =>
        typeof(TrayContext).GetMethod("ChangePreferences", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, [value]);

    private static T Field<T>(TrayContext context, string name) =>
        (T)typeof(TrayContext).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;

    private static void Click(Control root, string accessibleName)
    {
        var button = Assert.Single(Descendants(root).OfType<Button>(), b => b.AccessibleName == accessibleName);
        // PerformClick requires a visible parent; exercise the real Click wiring
        // directly so the real tray lifetime can be tested without showing its panel.
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TrayClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private long stamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan interval) { now += interval; stamp += interval.Ticks; }
    }
    private static ProviderResult GoodResult() => new(new UsageSnapshot([
        new UsageWindow("codex/primary", "Codex 7 days", 47, DateTimeOffset.UtcNow.AddDays(1), 10080)
    ], DateTimeOffset.UtcNow, "test fixture"));

    private sealed class FakeProvider(string name, Func<CancellationToken, Task<ProviderResult>> query) : IUsageProvider
    {
        private int calls;
        public string Name => name;
        public int Calls => Volatile.Read(ref calls);
        public Task<ProviderResult> QueryAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return query(cancellationToken);
        }
    }

    private sealed class MemoryStartup : IStartupRegistration
    {
        internal bool Enabled, RejectWrites;
        internal bool Available = true, BecomeUnavailableAfterWrite;
        internal int Writes;
        public bool TryRead(out bool enabled) { enabled = Enabled; return Available; }
        public bool TrySet(bool enabled)
        {
            Writes++; if (RejectWrites) return false;
            Enabled = enabled;
            if (BecomeUnavailableAfterWrite) Available = false;
            return true;
        }
    }
}
