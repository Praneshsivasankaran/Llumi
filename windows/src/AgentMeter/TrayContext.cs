using System.ComponentModel;
using System.Net.NetworkInformation;
using AgentMeter.Core;
using Microsoft.Win32;

namespace AgentMeter;

internal sealed class TrayContext : ApplicationContext
{
    private readonly RefreshCoordinator coordinator;
    private readonly DiagnosticLog log;
    private readonly UsageForm popup;
    private readonly MonitorForm monitor;
    private readonly MonitorPositionStore positions;
    private readonly IStartupRegistration startup;
    private readonly NotifyIcon tray;
    private readonly Icon icon = AppIcon.Load();
    private Icon trayIcon = AppIcon.LoadTray(SystemInformation.SmallIconSize.Width);
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem pinMenu;
    private readonly ToolStripMenuItem startupMenu = new("Start with Windows");
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer display = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer recovery = new() { Interval = 5_000 };
    private readonly System.Windows.Forms.Timer activityTimer = new() { Interval = 1000 };
    private readonly Func<ActivitySnapshot> captureActivity;
    private readonly WindowsActivitySource? activitySource;
    private readonly PreferenceStore preferenceStore;
    private Preferences preferences;
    private readonly SetupCompletionStore setupStore;
    private readonly string? reviewTitle;
    private SetupForm? setupWindow;
    private bool needsSetup;
    private ActivitySnapshot activity = ActivitySnapshot.Empty;
    private Task activityTask = Task.CompletedTask;
    private long activityGeneration;
    private readonly CancellationTokenSource lifetime = new();
    private readonly RegisteredWaitHandle showWait;
    private readonly RegisteredWaitHandle? quitWait;
    private readonly List<Task> refreshes = [];
    private Task activeRefresh = Task.CompletedTask;
    private bool exiting;
    private bool disposed;
    private bool suspended;

    public TrayContext(RefreshCoordinator coordinator, DiagnosticLog log, EventWaitHandle showEvent,
        MonitorPositionStore? positions = null, IStartupRegistration? startup = null, EventWaitHandle? quitEvent = null,
        Func<ActivitySnapshot>? captureActivity = null, PreferenceStore? preferenceStore = null, SetupCompletionStore? setupStore = null,
        string? reviewTitle = null)
    {
        this.coordinator = coordinator;
        this.log = log;
        this.reviewTitle = reviewTitle;
        this.positions = positions ?? MonitorPositionStore.Default(log.Write);
        this.startup = startup ?? (PackagedEnvironment.HasIdentity
            ? new PackagedStartupRegistration(new WindowsStartupTaskAccess(), log.Write)
            : new StartupRegistration(Environment.ProcessPath ?? Application.ExecutablePath, log.Write));
        if (captureActivity is null) { activitySource = new WindowsActivitySource(); this.captureActivity = activitySource.Capture; }
        else this.captureActivity = captureActivity;
        this.preferenceStore = preferenceStore ?? PreferenceStore.Default();
        this.setupStore = setupStore ?? SetupCompletionStore.Default();
        needsSetup = !this.setupStore.RecognizeExisting(this.preferenceStore.HasValidExistingPreferences());
        preferences = this.preferenceStore.Load();
        coordinator.SetEnabled("Codex", preferences.CodexEnabled);
        coordinator.SetEnabled("Claude Code", preferences.ClaudeEnabled);
        activitySource?.SetEnabled(preferences.CodexEnabled, preferences.ClaudeEnabled);
        Palette.Apply(preferences.Appearance);
        var names = coordinator.States.Select(s => s.Name).ToArray();
        popup = new UsageForm(names, icon);
        monitor = new MonitorForm(names, icon);
        if (reviewTitle is not null) { popup.Text = "Llumi — " + reviewTitle; monitor.Text = "Llumi monitor — " + reviewTitle; }
        tray = new NotifyIcon { Icon = trayIcon, Text = "Llumi — loading", ContextMenuStrip = menu, Visible = true };
        menu.Items.Add("Open Llumi", null, (_, _) => ShowPopup());
        pinMenu = new ToolStripMenuItem("Pin Monitor", null, (_, _) => { if (monitor.Visible) UnpinMonitor(); else OpenMonitor(); });
        menu.Items.Add("Refresh", null, (_, _) => StartRefresh());
        menu.Items.Add("Settings", null, (_, _) => { ShowPopup(); popup.ShowSettings(); });
        startupMenu.Click += (_, _) => ToggleStartup();
        menu.Opening += (_, _) => UpdateStartupState();
        menu.Items.Add("Quit", null, async (_, _) => await ExitAsync());
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPopup(); };
        popup.RefreshRequested += StartRefresh;
        popup.ExitRequested += async () => await ExitAsync();
        popup.PinRequested += OpenMonitor;
        popup.StartupToggleRequested += ToggleStartup;
        popup.MenuOpening += UpdateStartupState;
        popup.PreferencesChanged += ChangePreferences;
        popup.ResetPositionRequested += ResetMonitorPosition;
        popup.SetPreferences(preferences);
        tray.Visible = preferences.TrayIcon;
        monitor.OpenRequested += ShowPopup;
        monitor.UnpinRequested += UnpinMonitor;
        monitor.RefreshRequested += StartRefresh;
        monitor.ResetPositionRequested += ResetMonitorPosition;
        monitor.ExitRequested += async () => await ExitAsync();
        monitor.PositionCommitted += SavePosition;
        monitor.SurfaceFallbackUsed += () => log.Write("monitor.opaque-fallback");
        popup.VisibleChanged += (_, _) =>
        {
            UpdateDisplayTimer();
            log.Write(popup.Visible ? "panel.shown" : "panel.hidden");
        };
        monitor.VisibleChanged += (_, _) =>
        {
            UpdateDisplayTimer();
            pinMenu.Text = monitor.Visible ? "Unpin Monitor" : "Pin Monitor";
            log.Write(monitor.Visible ? "monitor.shown" : "monitor.hidden");
        };
        coordinator.Changed += OnChanged;
        poll.Tick += (_, _) => { StartRefresh(RefreshReason.Background); Render(); };
        display.Tick += (_, _) => Render();
        recovery.Tick += (_, _) =>
        {
            recovery.Stop();
            if (suspended || exiting) return;
            poll.Stop(); poll.Start();
            StartRefresh(RefreshReason.Reset);
        };
        activityTimer.Tick += (_, _) => SampleActivity();
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => OnUi(ShowPopup), null, Timeout.Infinite, false);
        if (quitEvent is not null)
            quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => OnUi(async () => await ExitAsync()), null, Timeout.Infinite, false);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        UpdateStartupState();
        poll.Start();
        StartRefresh();
        activityTimer.Start(); SampleActivity();
        if (needsSetup) OpenSetup();
        else if (!preferences.TrayIcon) ShowPopup();
        log.Write("tray.ready");
    }

    internal void OpenPanel() { if (needsSetup) OpenSetup(); else ShowPopup(); }
    private void OpenSetup()
    {
        if (setupWindow is null || setupWindow.IsDisposed)
        {
            setupWindow = new SetupForm(new SetupFlow(setupStore), () => coordinator.States, providerName => StartRefresh(RefreshReason.Manual, providerName),
                () => preferences, ChangePreferences, startup, ToggleStartup, () => { needsSetup = false; ShowPopup(); });
            if (reviewTitle is not null) setupWindow.Text = "Setup Llumi — " + reviewTitle;
            setupWindow.FormClosed += (_, _) => { if (!exiting && !preferences.TrayIcon) ShowPopup(); };
        }
        setupWindow.Show(); setupWindow.Activate();
    }
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => OnUi(() =>
    { Palette.Apply(preferences.Appearance); popup.ApplyTheme(); if (setupWindow is { IsDisposed: false }) setupWindow.ApplyTheme(); monitor.UpdateSurface(); });

    private void ChangePreferences(Preferences value)
    {
        if (!preferenceStore.Save(value)) { popup.SetPreferences(preferences); popup.PreferenceSaveFailed(); return; }
        var providerChange = preferences.CodexEnabled != value.CodexEnabled || preferences.ClaudeEnabled != value.ClaudeEnabled;
        var codexReenabled = !preferences.CodexEnabled && value.CodexEnabled;
        var claudeReenabled = !preferences.ClaudeEnabled && value.ClaudeEnabled;
        preferences = value; tray.Visible = value.TrayIcon;
        if (providerChange)
        {
            activityGeneration++;
            coordinator.SetEnabled("Codex", value.CodexEnabled);
            coordinator.SetEnabled("Claude Code", value.ClaudeEnabled);
            activitySource?.SetEnabled(value.CodexEnabled, value.ClaudeEnabled);
        }
        Palette.Apply(value.Appearance); popup.SetPreferences(value);
        if (setupWindow is { IsDisposed: false }) setupWindow.ApplyTheme();
        monitor.UpdateSurface(); ApplyActivity(activity);
        Render();
        if (codexReenabled) StartRefresh(RefreshReason.Enable, "Codex");
        if (claudeReenabled) StartRefresh(RefreshReason.Enable, coordinator.States.FirstOrDefault(s => UsagePresentation.IsClaude(s.Name))?.Name ?? "Claude Code");
        if (codexReenabled || claudeReenabled) SampleActivity();
    }

    private void SampleActivity()
    {
        if (exiting || suspended || !activityTask.IsCompleted || !preferences.CodexEnabled && !preferences.ClaudeEnabled) return;
        activityTask = SampleActivityAsync(activityGeneration);
    }
    private async Task SampleActivityAsync(long generation)
    {
        try
        {
            var snapshot = await Task.Run(captureActivity, lifetime.Token);
            if (!exiting && !suspended && generation == activityGeneration) ApplyActivity(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!exiting && !suspended && generation == activityGeneration) { log.Write("activity.unavailable"); ApplyActivity(ActivitySnapshot.Empty); } }
    }
    internal void ApplyActivity(ActivitySnapshot snapshot)
    {
        activity = new(preferences.CodexEnabled ? snapshot.Codex : new(), preferences.ClaudeEnabled ? snapshot.Claude : new());
        var names = activity.Providers;
        if (!preferences.CompactMonitor || names.Length == 0)
        {
            if (monitor.Visible)
            {
                SavePosition();
                monitor.HideMonitor(preferences.CompactMonitor && (preferences.CodexEnabled || preferences.ClaudeEnabled));
            }
            // A both-off change hides immediately before removing the previous rows.
            // Normal contextual exits retain their shape until the native exit completes.
            if (!preferences.CodexEnabled && !preferences.ClaudeEnabled) monitor.SetProviders([]);
            return;
        }
        monitor.SetProviders(names);
        monitor.Render(coordinator.States);
        if (!monitor.Visible || !monitor.DesiredVisible) OpenMonitor();
    }

    internal void OpenMonitor()
    {
        if (exiting || (monitor.Visible && monitor.DesiredVisible) || !preferences.CompactMonitor || activity.Providers.Length == 0) return;
        try
        {
            if (monitor.Visible) { monitor.ShowMonitor(monitor.Location); return; }
            var saved = positions.Load();
            var screen = MonitorScreen(saved?.Display);
            // Moving the existing hidden HWND first lets Windows apply the destination DPI.
            monitor.Location = screen.WorkingArea.Location;
            monitor.Render(coordinator.States);
            monitor.RestorePosition(saved, screen.DeviceName);
            monitor.ShowMonitor(monitor.Location, monitor.SavedPosition);
            log.Write("panel.pinned");
        }
        catch (Win32Exception) { MonitorFailed(); }
    }

    internal void UnpinMonitor() => ChangePreferences(preferences with { CompactMonitor = false });

    private void UpdateStartupState()
    {
        var available = startup.TryRead(out var enabled);
        startupMenu.Checked = enabled;
        startupMenu.Enabled = available;
        startupMenu.Text = available ? "Start with Windows" : "Start with Windows (unavailable)";
        popup.SetStartupState(enabled, available);
    }

    private void ToggleStartup()
    {
        if (startup.TryRead(out var enabled)) startup.TrySet(!enabled);
        UpdateStartupState();
    }

    private void ShowPopup()
    {
        if (exiting) return;
        popup.Render(coordinator.States, coordinator.IsRefreshing, log.WriteFailed);
        popup.ShowPanel(Cursor.Position);
    }

    private void SavePosition()
    {
        if (!monitor.Visible || monitor.IsDisposed) return;
        positions.Save(monitor.SavedPosition);
    }

    internal void ResetMonitorPosition()
    {
        if (exiting) return;
        var screen = monitor.Visible ? Screen.FromControl(monitor) : Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
        var position = new MonitorPosition(1, screen.DeviceName, 20, 20);
        // Save first: failed persistence must leave the current position active.
        if (!positions.Save(position)) { popup.PositionResetFailed(); return; }
        if (!monitor.Visible) return;
        try
        {
            monitor.SetExpanded(false, false);
            monitor.Location = screen.WorkingArea.Location;
            monitor.RestorePosition(position, screen.DeviceName);
        }
        catch (Win32Exception) { MonitorFailed(); }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        var replacement = AppIcon.LoadTray(SystemInformation.SmallIconSize.Width);
        tray.Icon = replacement;
        trayIcon.Dispose(); trayIcon = replacement;
        if (monitor.Visible)
        {
            var saved = monitor.SavedPosition;
            var screen = MonitorScreen(saved.Display);
            monitor.Location = screen.WorkingArea.Location;
            monitor.RestorePosition(saved, screen.DeviceName);
            SavePosition();
        }
        if (popup.Visible)
        {
            popup.Render(coordinator.States, coordinator.IsRefreshing, log.WriteFailed);
            popup.KeepOnScreen();
        }
    });

    private static Screen MonitorScreen(string? savedDisplay)
    {
        var screens = Screen.AllScreens;
        var primary = Screen.PrimaryScreen ?? screens[0];
        var display = MonitorPosition.SelectDisplay(savedDisplay, primary.DeviceName, screens.Select(screen => screen.DeviceName));
        return screens.FirstOrDefault(screen => string.Equals(screen.DeviceName, display, StringComparison.OrdinalIgnoreCase)) ?? primary;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => OnUi(() =>
    {
        if (e.Mode == PowerModes.Suspend)
        {
            suspended = true;
            activityGeneration++;
            coordinator.Suspend();
            poll.Stop(); recovery.Stop(); display.Stop(); activityTimer.Stop(); monitor.HideMonitor();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            suspended = false;
            coordinator.Resume();
            activityTimer.Start(); SampleActivity();
            ScheduleRecovery();
            UpdateDisplayTimer();
            log.Write("system.resumed");
        }
    });

    private void OnTimeChanged(object? sender, EventArgs e) => OnUi(ScheduleRecovery);
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    { if (e.IsAvailable) OnUi(ScheduleRecovery); }
    private void OnNetworkAddressChanged(object? sender, EventArgs e) => OnUi(ScheduleRecovery);

    internal void ScheduleRecovery()
    {
        if (exiting || suspended) return;
        Render(); // Re-evaluate age/reset immediately after a clock or environment change.
        recovery.Stop(); recovery.Start(); // One trailing-edge refresh after the environment settles.
        if (!poll.Enabled) poll.Start(); // Periodic retry survives a continually changing network.
    }

    private void UpdateDisplayTimer()
    {
        if (!exiting && !suspended && (popup.Visible || monitor.Visible)) display.Start(); else display.Stop();
    }

    private void OnChanged() => OnUi(Render);
    private void OnUi(Action action)
    {
        if (exiting || popup.IsDisposed) return;
        try { popup.BeginInvoke(action); }
        catch (InvalidOperationException) { log.Write("ui.dispatch-after-close"); }
    }

    private void Render()
    {
        if (exiting) return;
        var states = coordinator.States;
        if (setupWindow is { IsDisposed: false }) setupWindow.RefreshStatuses();
        if (popup.Visible) popup.Render(states, coordinator.IsRefreshing, log.WriteFailed);
        if (monitor.Visible)
        {
            try { monitor.Render(states); }
            catch (Win32Exception) { MonitorFailed(); }
        }
        tray.Text = UsageText.Tooltip(states, DateTimeOffset.UtcNow);
    }

    private void MonitorFailed()
    {
        log.Write("monitor.presentation-failed");
        monitor.HideMonitor();
        popup.Render(coordinator.States, coordinator.IsRefreshing, log.WriteFailed);
        popup.ShowPanel(Cursor.Position);
    }

    private void StartRefresh()
        => StartRefresh(RefreshReason.Manual);

    private void StartRefresh(RefreshReason reason, string? providerName = null)
    {
        if (exiting || suspended) return;
        refreshes.RemoveAll(task => task.IsCompleted);
        var refresh = RefreshAsync(reason, providerName);
        if (!refresh.IsCompleted) refreshes.Add(refresh);
        activeRefresh = Task.WhenAll(refreshes);
    }

    private async Task RefreshAsync(RefreshReason reason, string? providerName)
    {
        try { await coordinator.RefreshAsync(lifetime.Token, reason, providerName); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { log.Write("refresh.unexpected-error"); }
    }

    internal async Task ExitAsync()
    {
        if (exiting) return;
        SavePosition();
        exiting = true;
        poll.Stop();
        display.Stop();
        recovery.Stop();
        activityTimer.Stop();
        coordinator.Suspend();
        await lifetime.CancelAsync();
        await activeRefresh;
        try { await activityTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        await coordinator.DrainAsync();
        tray.Visible = false;
        popup.AllowExit = true;
        monitor.AllowExit = true;
        monitor.Close();
        popup.Close();
        setupWindow?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            SavePosition();
            exiting = true;
            coordinator.Suspend();
            lifetime.Cancel();
            coordinator.Changed -= OnChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.TimeChanged -= OnTimeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            showWait.Unregister(null);
            quitWait?.Unregister(null);
            poll.Dispose(); display.Dispose(); recovery.Dispose(); activityTimer.Dispose(); tray.Visible = false; tray.Dispose(); menu.Dispose();
            setupWindow?.Dispose();
            popup.AllowExit = true; popup.Dispose();
            monitor.AllowExit = true; monitor.Dispose();
            icon.Dispose(); trayIcon.Dispose(); lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
