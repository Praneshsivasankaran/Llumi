using System.Reflection;
using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class BackgroundLaunchTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task ColdAndLoginLaunchRespectIndependentBackgroundPreference(bool loginLaunch, bool background, bool visible)
    {
        await Run(new(StartInBackground: background), loginLaunch, true, fixture =>
        {
            Assert.Equal(visible, fixture.Popup.Visible);
            Assert.Equal(FormWindowState.Normal, fixture.Popup.WindowState);
            Assert.True(Field<NotifyIcon>(fixture.Context, "tray").Visible);
            Assert.True(Field<System.Windows.Forms.Timer>(fixture.Context, "poll").Enabled);
            Assert.False(Field<MonitorForm>(fixture.Context, "monitor").Visible);
            Assert.Equal(0, fixture.Startup.Writes);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task QuietLaunchWithoutTrayKeepsMinimizedTaskbarEntry(bool loginLaunch, bool background)
    {
        await Run(new(TrayIcon: false, StartInBackground: background), loginLaunch, true, fixture =>
        {
            Assert.True(fixture.Popup.Visible); Assert.True(fixture.Popup.ShowInTaskbar);
            Assert.Equal(FormWindowState.Minimized, fixture.Popup.WindowState);
            Assert.False(Field<NotifyIcon>(fixture.Context, "tray").Visible);
            var handle = fixture.Popup.Handle;
            fixture.Context.OpenPanel();
            Assert.Equal(FormWindowState.Normal, fixture.Popup.WindowState);
            Assert.Equal(handle, fixture.Popup.Handle);
            Assert.Equal(0, fixture.Startup.Writes);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteFirstRunSetupStaysVisibleEvenWhenBackgroundIsRequested(bool loginLaunch)
    {
        await Run(new(StartInBackground: true), loginLaunch, false, fixture =>
        {
            var setup = Field<SetupForm>(fixture.Context, "setupWindow");
            Assert.True(setup.Visible); Assert.Equal(FormWindowState.Normal, setup.WindowState);
            Assert.True(Field<bool>(fixture.Context, "needsSetup")); Assert.False(fixture.Popup.Visible);
            Assert.False(fixture.Setup.IsComplete());
            Assert.True(fixture.Preferences.Load().StartInBackground);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ExplicitSecondLaunchSignalRestoresTheSameBackgroundInstance()
    {
        await Run(new(StartInBackground: true), false, true, async fixture =>
        {
            Assert.False(fixture.Popup.Visible);
            var handle = fixture.Popup.Handle;
            fixture.ShowEvent.Set();
            await Until(() => fixture.Popup.Visible);
            Assert.Equal(handle, fixture.Popup.Handle); Assert.Equal(FormWindowState.Normal, fixture.Popup.WindowState);
            Assert.Single(Application.OpenForms.OfType<UsageForm>());
            fixture.Popup.Close(); Assert.False(fixture.Popup.Visible); Assert.False(fixture.Popup.IsDisposed);
            fixture.ShowEvent.Set();
            await Until(() => fixture.Popup.Visible);
            Assert.Equal(handle, fixture.Popup.Handle); Assert.Single(Application.OpenForms.OfType<UsageForm>());
            Assert.Equal(0, fixture.Startup.Writes);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosingMainWindowLeavesPollingAliveWithOrWithoutTray(bool tray)
    {
        await Run(new(TrayIcon: tray), false, true, async fixture =>
        {
            await Field<Task>(fixture.Context, "activeRefresh");
            Assert.True(fixture.Popup.Visible); Assert.Equal(1, fixture.Provider.Calls);
            fixture.Popup.Close(); Assert.False(fixture.Popup.IsDisposed);
            if (tray) Assert.False(fixture.Popup.Visible);
            else { Assert.True(fixture.Popup.Visible); Assert.Equal(FormWindowState.Minimized, fixture.Popup.WindowState); }
            var poll = Field<System.Windows.Forms.Timer>(fixture.Context, "poll");
            Assert.True(poll.Enabled);
            fixture.Clock.Advance(TimeSpan.FromSeconds(31)); poll.Interval = 50;
            await Until(() => fixture.Provider.Calls >= 2);
            await Field<Task>(fixture.Context, "activeRefresh");
            Assert.Equal(ProviderStatus.Ready, fixture.Coordinator.States[0].Status);
            Assert.True(poll.Enabled); Assert.False(fixture.Popup.IsDisposed);
            if (tray) Assert.False(fixture.Popup.Visible);
            else Assert.Equal(FormWindowState.Minimized, fixture.Popup.WindowState);
        });
    }

    [Fact]
    public async Task ChangingBackgroundPreferencePersistsWithoutHidingOpenWindowOrChangingStartupRegistration()
    {
        await Run(new(CompactMonitor: false), false, true, fixture =>
        {
            var handle = fixture.Popup.Handle;
            fixture.Popup.ShowSettings();
            var toggle = Descendants(fixture.Popup).OfType<ToggleSwitch>().Single(c => c.AccessibleName == "Start in background");
            toggle.Checked = true;
            Assert.True(fixture.Preferences.Load().StartInBackground);
            Assert.True(fixture.Popup.Visible); Assert.Equal(FormWindowState.Normal, fixture.Popup.WindowState);
            Assert.Equal(handle, fixture.Popup.Handle); Assert.False(fixture.Preferences.Load().CompactMonitor);
            toggle.Checked = false;
            Assert.False(fixture.Preferences.Load().StartInBackground); Assert.True(fixture.Popup.Visible);
            Assert.Equal(0, fixture.Startup.Writes);
            return Task.CompletedTask;
        });
    }

    private static async Task Run(Preferences preferences, bool loginLaunch, bool setupComplete, Func<Fixture, Task> scenario)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = Path.Combine(Path.GetTempPath(), "Llumi-background-launch-fixture-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            TrayContext? context = null; Exception? failure = null;
            try
            {
                using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                var preferenceStore = new PreferenceStore(Path.Combine(directory, "preferences.json"));
                var setupStore = new SetupCompletionStore(Path.Combine(directory, "setup.json"));
                Assert.True(preferenceStore.Save(preferences)); Assert.True(setupStore.Save(setupComplete));
                var startup = new MemoryStartup(); var clock = new FixtureClock();
                var provider = new FixtureProvider(clock);
                var coordinator = new RefreshCoordinator([provider], clock: clock, minimumInterval: TimeSpan.Zero);
                var ledger = new UnusedLedger(); var notifications = new UnusedNotifications();
                context = new TrayContext(coordinator, new DiagnosticLog(directory), showEvent,
                    new MonitorPositionStore(Path.Combine(directory, "position.json")), startup,
                    captureActivity: () => ActivitySnapshot.Empty, preferenceStore: preferenceStore, setupStore: setupStore,
                    alertService: new UsageAlertService(ledger, notifications), startupLaunch: loginLaunch);
                var popup = Field<UsageForm>(context, "popup");
                using var watchdog = new System.Threading.Timer(_ =>
                {
                    failure ??= new TimeoutException("Background lifecycle fixture exceeded its deadline.");
                    try { popup.BeginInvoke(() => context.ExitThread()); } catch (InvalidOperationException) { }
                }, null, TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
                popup.BeginInvoke(async () =>
                {
                    try
                    {
                        Field<System.Windows.Forms.Timer>(context, "activityTimer").Stop();
                        await Field<Task>(context, "activityTask");
                        await scenario(new(context, popup, coordinator, provider, clock, showEvent, preferenceStore, setupStore, startup));
                        Assert.Equal(0, ledger.Accesses); Assert.Equal(0, notifications.Submissions);
                    }
                    catch (Exception exception) { failure = exception; }
                    finally
                    {
                        try { await context.ExitAsync(); }
                        catch (Exception exception) { failure ??= exception; context.ExitThread(); }
                    }
                });
                Application.Run(context);
                Assert.True(Field<Task>(context, "activeRefresh").IsCompleted);
                Assert.True(Field<CancellationTokenSource>(context, "lifetime").IsCancellationRequested);
                Assert.False(Field<System.Windows.Forms.Timer>(context, "poll").Enabled);
                Assert.False(Field<NotifyIcon>(context, "tray").Visible);
                context.Dispose(); context = null; Assert.True(popup.IsDisposed);
            }
            catch (Exception exception) { failure ??= exception; }
            finally
            {
                try { context?.Dispose(); } catch (Exception exception) { failure ??= exception; }
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failure ??= exception; }
                if (failure is null) finished.TrySetResult(); else finished.TrySetException(failure);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(16));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(condition(), "Expected the native message-loop condition before its deadline.");
    }
    private static T Field<T>(TrayContext context, string name) =>
        (T)typeof(TrayContext).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private sealed record Fixture(TrayContext Context, UsageForm Popup, RefreshCoordinator Coordinator,
        FixtureProvider Provider, FixtureClock Clock, EventWaitHandle ShowEvent,
        PreferenceStore Preferences, SetupCompletionStore Setup, MemoryStartup Startup);
    private sealed class MemoryStartup : IStartupRegistration
    {
        internal int Writes;
        public bool TryRead(out bool enabled) { enabled = false; return true; }
        public bool TrySet(bool enabled) { Writes++; return true; }
    }
    private sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow; private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan by) { now += by; timestamp += by.Ticks; }
    }
    private sealed class FixtureProvider(FixtureClock clock) : IUsageProvider
    {
        private int calls; public string Name => "Codex"; internal int Calls => Volatile.Read(ref calls);
        public Task<ProviderResult> QueryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref calls);
            return Task.FromResult(new ProviderResult(new UsageSnapshot(
                [new UsageWindow("codex/primary", "5 hours", 42, clock.GetUtcNow().AddHours(2), 300)],
                clock.GetUtcNow(), "synthetic background fixture")));
        }
    }
    private sealed class UnusedLedger : IAlertLedgerStore
    {
        internal int Accesses;
        public AlertLedgerStoreStatus Status => AlertLedgerStoreStatus.NotLoaded;
        public bool TryLoad(out AlertLedger ledger) { Accesses++; ledger = new(); return true; }
        public bool TrySave(AlertLedger ledger) { Accesses++; return true; }
        public string Pseudonymize(string value) { Accesses++; return new string('A', 64); }
        public void Dispose() { }
    }
    private sealed class UnusedNotifications : IAlertNotifications
    {
        internal int Submissions;
        public AlertNotificationStatus Status => AlertNotificationStatus.NotInitialized;
        public event EventHandler? OpenRequested { add { } remove { } }
        public AlertNotificationResult Show(UsageAlertNotice notice) { Submissions++; return AlertNotificationResult.Submitted; }
        public AlertNotificationResult ShowTest() { Submissions++; return AlertNotificationResult.Submitted; }
        public void Dispose() { }
    }
}
