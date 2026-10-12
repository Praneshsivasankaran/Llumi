using AgentMeter.Core;

namespace AgentMeter;

// Runs on the application UI thread, after coordinator changes or explicit settings
// changes. Display countdowns never invoke this service. The reservation is committed
// before notification delivery; a crash or failed Windows submission cannot replay it.
internal sealed class UsageAlertService(IAlertLedgerStore store, IAlertNotifications notifications,
    Action<string>? log = null) : IDisposable
{
    private AlertLedger ledger = new();
    private bool loaded, disposed;
    private string? problem;
    private string? deliveryMessage;

    internal event EventHandler? OpenRequested
    {
        add => notifications.OpenRequested += value;
        remove => notifications.OpenRequested -= value;
    }

    internal void Observe(IReadOnlyList<ProviderState> states, UsageAlertSettings settings, DateTimeOffset now)
    {
        if (disposed || !settings.Enabled) return;
        try
        {
            if (!loaded)
            {
                if (!store.TryLoad(out var restored)) { StorageFailure(); return; }
                ledger = restored; loaded = true;
            }
            var evaluation = UsageAlertEngine.Evaluate(states, settings, now, ledger, store.Pseudonymize);
            if (evaluation.Failure != AlertEvaluationFailure.None)
            {
                problem = "Automatic alerts are unavailable because their saved state or settings could not be verified. Monitoring continues.";
                log?.Invoke("alerts.state-invalid");
                return;
            }
            if (evaluation.Changed)
            {
                if (!store.TrySave(evaluation.Ledger)) { StorageFailure(); return; }
                ledger = evaluation.Ledger;
                problem = null;
            }
            foreach (var notice in evaluation.Notices)
            {
                AlertNotificationResult result;
                try { result = notifications.Show(notice); }
                catch (Exception) { result = AlertNotificationResult.Failed; }
                deliveryMessage = DeliveryText(result, test: false);
                log?.Invoke(result == AlertNotificationResult.Submitted ? "alerts.submitted" : "alerts.not-submitted");
            }
        }
        catch (Exception)
        {
            // No exception text may reveal account-derived inputs, state or paths.
            problem = "Automatic alerts could not be processed safely. Monitoring continues; try again from Settings.";
            log?.Invoke("alerts.processing-failed");
        }
    }

    internal void SendTest()
    {
        if (disposed) return;
        try
        {
            var result = notifications.ShowTest();
            deliveryMessage = DeliveryText(result, test: true);
            log?.Invoke(result == AlertNotificationResult.Submitted ? "alerts.test-submitted" : "alerts.test-not-submitted");
        }
        catch (Exception)
        {
            deliveryMessage = "The test notification could not be sent. Check Windows notification settings and try again.";
            log?.Invoke("alerts.test-failed");
        }
    }

    internal string StatusMessage(UsageAlertSettings settings)
    {
        if (problem is not null && settings.Enabled) return problem;
        if (notifications.Status == AlertNotificationStatus.Failed)
            return "Windows reported that notification delivery failed. Monitoring continues; send a test after checking Windows Settings.";
        if (notifications.Status is AlertNotificationStatus.DisabledForApplication or AlertNotificationStatus.DisabledForUser or
            AlertNotificationStatus.DisabledByPolicy or AlertNotificationStatus.DisabledByManifest)
            return DeliveryText(AlertNotificationResult.Disabled, test: false);
        if (deliveryMessage is not null) return deliveryMessage;
        if (!settings.Enabled) return "Usage alerts are off. You can still send a test notification.";
        return "Alerts use fresh, verified usage with a reported reset. Send a test to check Windows notifications.";
    }

    private void StorageFailure()
    {
        loaded = false; // A later verified event can retry reading the last committed state.
        problem = "Automatic alerts are paused because their local state could not be read or saved. Monitoring continues; check local storage and retry.";
        log?.Invoke("alerts.storage-unavailable");
    }

    private static string DeliveryText(AlertNotificationResult result, bool test) => result switch
    {
        AlertNotificationResult.Submitted => test
            ? "Test submitted to Windows. Windows notification settings or Do Not Disturb may hide the banner."
            : "Notifications submitted to Windows. Do Not Disturb may hide banners.",
        AlertNotificationResult.Disabled => "Windows notifications are disabled for Llumi or by system policy. Enable them in Windows Settings, then send a test.",
        AlertNotificationResult.Unavailable => "Windows notifications are unavailable for this installation. Monitoring continues.",
        _ => "Windows could not accept the notification. Check notification settings and send a test."
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        notifications.Dispose();
        store.Dispose();
    }
}
