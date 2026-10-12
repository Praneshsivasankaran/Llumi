using System.Drawing.Imaging;
using System.Text.Json;
using AgentMeter;
using AgentMeter.Core;
using AppAppearance = AgentMeter.Appearance;

// Explicit development-only review: no coordinator, provider, activity collector,
// production settings, startup registration, or alert ledger is constructed.
internal static class AlertFeatureReview
{
    private const int ObservationMilliseconds = 8000;

    internal static int Run(string directory)
    {
        string profile;
        try { profile = LiveFirstRunReview.PrepareProfile(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return 1; }

        var receipt = new ReviewReceipt();
        var preferences = new PreferenceStore(Path.Combine(profile, "synthetic-preferences.json"));
        var fixture = new Preferences(CompactMonitor: false, Appearance: AppAppearance.Light,
            StartInBackground: true, Alerts: new UsageAlertSettings(Enabled: true,
                Thresholds: [new(75), new(90, false), new(95)], SnoozedUntil: DateTimeOffset.UtcNow.AddHours(1)));
        using var icon = AppIcon.Load();
        using var usage = new UsageForm(["Codex", "Claude Code"], icon)
        {
            AllowExit = false,
            Text = "Llumi — synthetic alerts review"
        };
        AlertNotifications? notifications = null;
        NativeAlertNotificationTransport? transport = null;
        var activationCount = 0;
        var exitCode = 0;
        var startupEnabled = false;
        usage.PreferencesChanged += value =>
        {
            fixture = value;
            if (!preferences.Save(value)) receipt.Errors.Add("SyntheticPreferenceSaveFailed");
        };
        usage.StartupToggleRequested += () =>
        {
            startupEnabled = !startupEnabled;
            usage.SetStartupState(startupEnabled, true);
        };
        usage.TestNotificationRequested += () =>
        {
            if (notifications is null) return;
            receipt.Notification.TestRequests++;
            receipt.Notification.SubmissionResult = notifications.ShowTest().ToString();
            receipt.Notification.StatusAfterSubmission = notifications.Status.ToString();
            usage.SetAlertStatus(NotificationStatus(receipt.Notification.SubmissionResult, notifications.Status));
        };
        usage.Shown += async (_, _) =>
        {
            try
            {
                if (!preferences.Save(fixture)) throw new InvalidOperationException("SyntheticPreferenceSaveFailed");
                fixture = preferences.Load();
                usage.SetStartupState(false, true);
                usage.SetAlertStatus("Synthetic preview. Native notification submission will be checked after these captures.");
                foreach (var appearance in new[] { AppAppearance.Light, AppAppearance.Dark })
                foreach (var narrow in new[] { false, true })
                {
                    fixture = fixture with { Appearance = appearance };
                    Palette.Apply(appearance);
                    usage.SetPreferences(fixture);
                    usage.Render(SyntheticStates(), false, false);
                    usage.ShowSettings();
                    int S(int value) => (int)Math.Round(value * usage.DeviceDpi / 96d);
                    usage.ClientSize = new Size(S(narrow ? 380 : 640), S(440));
                    var name = appearance.ToString().ToLowerInvariant() + (narrow ? "-narrow" : "-standard");
                    await CaptureSections(usage, profile, name, receipt.Captures);
                }

                transport = NativeAlertNotificationTransport.ForReview(Environment.ProcessPath!);
                notifications = new AlertNotifications(transport);
                notifications.OpenRequested += (_, _) => Interlocked.Increment(ref activationCount);
                var testButton = Find<Button>(usage, "Send test notification");
                // Exercise the existing Settings event and real native transport once.
                testButton.PerformClick();
                receipt.Notification.ObservationStartedUtc = DateTimeOffset.UtcNow;
                await Task.Delay(ObservationMilliseconds);
                receipt.Notification.ObservationCompletedUtc = DateTimeOffset.UtcNow;
                receipt.Notification.StatusAfterObservation = notifications.Status.ToString();
                receipt.Notification.DiagnosticStage = transport.DiagnosticStage;
                receipt.Notification.DiagnosticHResult = transport.DiagnosticHResult is { } hr ? $"0x{hr:X8}" : null;
                receipt.Notification.HasPackageIdentity = transport.DiagnosticHasPackageIdentity;
                receipt.Notification.ActivationCallbackCount = Volatile.Read(ref activationCount);
                usage.SetAlertStatus(NotificationStatus(receipt.Notification.SubmissionResult, notifications.Status));
                await CaptureAt(usage, Find<Button>(usage, "Send test notification"), profile,
                    "dark-narrow-native-notification-result.png", receipt.Captures);
            }
            catch (Exception ex)
            {
                // Category only: no provider content, account data, exception message or paths.
                receipt.Errors.Add(ex.GetType().Name);
                exitCode = 1;
            }
            finally
            {
                try
                {
                    notifications?.Dispose();
                    receipt.Notification.StatusAfterDisposal = notifications?.Status.ToString();
                    receipt.Notification.DisposalCompleted = notifications is not null;
                }
                catch (Exception ex) { receipt.Errors.Add(ex.GetType().Name); exitCode = 1; }
                receipt.CompletedUtc = DateTimeOffset.UtcNow;
                try
                {
                    File.WriteAllText(Path.Combine(profile, "alerts-review-receipt.json"),
                        JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { exitCode = 1; }
                usage.AllowExit = true;
                usage.Close();
            }
        };
        Application.Run(usage);
        return exitCode;
    }

    private static async Task CaptureSections(UsageForm usage, string profile, string name, List<CaptureReceipt> captures)
    {
        var alerts = Descendants(usage).OfType<AlertSettingsView>().Single();
        var sections = new (string Name, Control Target)[]
        {
            ("background", Find<Control>(usage, "Start in background")),
            ("alerts", Find<Control>(alerts, "Usage alerts")),
            ("thresholds", alerts.Controls.OfType<Label>().Single(label => label.Text == "Thresholds (% used)")),
            ("snooze", Find<Control>(alerts, "Alert snooze status")),
            ("notification", Find<Button>(alerts, "Send test notification"))
        };
        foreach (var section in sections)
            await CaptureAt(usage, section.Target, profile, name + "-" + section.Name + ".png", captures);
    }

    private static async Task CaptureAt(UsageForm usage, Control target, string profile, string fileName, List<CaptureReceipt> captures)
    {
        var settings = (Panel)usage.Controls.Find("settings", true).Single();
        var position = settings.PointToClient(target.PointToScreen(Point.Empty));
        var absoluteY = position.Y - settings.AutoScrollPosition.Y;
        var margin = (int)Math.Round(12 * usage.DeviceDpi / 96d);
        settings.AutoScrollPosition = new Point(0, Math.Max(0, absoluteY - margin));
        await Task.Delay(100); // Let native scrolling, layout and paint complete on the STA loop.
        usage.Refresh();
        var targetBounds = settings.RectangleToClient(target.RectangleToScreen(target.ClientRectangle));
        var visible = settings.ClientRectangle.Contains(targetBounds);
        if (!visible || settings.HorizontalScroll.Visible || settings.AutoScrollPosition.X != 0)
            throw new InvalidOperationException("ReviewSettingsGeometryFailed");
        using var bitmap = new Bitmap(usage.Width, usage.Height);
        usage.DrawToBitmap(bitmap, new Rectangle(Point.Empty, usage.Size));
        bitmap.Save(Path.Combine(profile, fileName), ImageFormat.Png);
        captures.Add(new(fileName, usage.DeviceDpi, usage.ClientSize.Width, usage.ClientSize.Height,
            -settings.AutoScrollPosition.Y, target.AccessibleName ?? target.Text, visible));
    }

    private static string NotificationStatus(string? result, AlertNotificationStatus status) =>
        $"Native test: {result ?? "not requested"}. Windows status: {status}. Submission does not verify a visible banner; Do Not Disturb may suppress it.";

    private static T Find<T>(Control parent, string accessibleName) where T : Control =>
        Descendants(parent).OfType<T>().Single(control => control.AccessibleName == accessibleName);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static ProviderState[] SyntheticStates()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new("Codex", ProviderStatus.Ready,
                new UsageSnapshot([new("codex/primary", "Codex", 76, now.AddHours(3), 300),
                    new("codex/secondary", "Codex", 49, now.AddDays(4), 10080)], now, "Synthetic review fixture"),
                Authentication: AuthenticationStatus.Verified),
            new("Claude Code", ProviderStatus.Ready,
                new UsageSnapshot([new("five_hour", "Claude", 24, now.AddHours(2), 300),
                    new("seven_day", "Claude", 62, now.AddDays(5), 10080)], now, "Synthetic review fixture"),
                Authentication: AuthenticationStatus.Verified)
        ];
    }

    private sealed class ReviewReceipt
    {
        public string EvidenceKind { get; } = "Synthetic native WinForms rendering and native notification transport smoke check";
        public string CaptureMethod { get; } = "DrawToBitmap; not physical screen captures";
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; set; }
        public int ProviderRequests { get; } = 0;
        public bool ProductionSettingsAccessed { get; } = false;
        public bool StartupRegistrationAccessed { get; } = false;
        public List<CaptureReceipt> Captures { get; } = [];
        public NotificationReceipt Notification { get; } = new();
        public List<string> Errors { get; } = [];
    }

    private sealed record CaptureReceipt(string File, int Dpi, int ClientWidthPixels, int ClientHeightPixels,
        int ScrollOffsetPixels, string Anchor, bool AnchorFullyVisible);

    private sealed class NotificationReceipt
    {
        public string Identity { get; } = "Llumi.LocalAlertReview";
        public int TestRequests { get; set; }
        public string? SubmissionResult { get; set; }
        public string? StatusAfterSubmission { get; set; }
        public DateTimeOffset? ObservationStartedUtc { get; set; }
        public DateTimeOffset? ObservationCompletedUtc { get; set; }
        public string? StatusAfterObservation { get; set; }
        public string? DiagnosticStage { get; set; }
        public string? DiagnosticHResult { get; set; }
        public bool? HasPackageIdentity { get; set; }
        public int ActivationCallbackCount { get; set; }
        public string BannerVisibility { get; } = "Not verified; OS submission does not establish that a banner was shown or seen";
        public bool DisposalCompleted { get; set; }
        public string? StatusAfterDisposal { get; set; }
        public string CleanupScope { get; } = "Dispose requests removal of this review transport's owned toasts and exact review shortcut";
    }
}
