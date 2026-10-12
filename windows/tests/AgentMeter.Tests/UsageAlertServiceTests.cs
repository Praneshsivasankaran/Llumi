using System.Security.Cryptography;
using System.Text;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class UsageAlertServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly UsageAlertSettings Enabled = new(Enabled: true);

    [Fact]
    public void OffDoesNotLoadWriteOrRegisterNotificationsAndTestIsIndependent()
    {
        var store = new MemoryStore(); var notifications = new Notifications();
        using var service = new UsageAlertService(store, notifications);
        service.Observe([State(96)], new(), Now);
        Assert.Equal(0, store.Loads); Assert.Equal(0, store.Saves); Assert.Empty(notifications.Notices);
        service.SendTest();
        Assert.Equal(1, notifications.Tests); Assert.Equal(0, store.Loads);
    }

    [Fact]
    public void ReservationIsCommittedBeforeDispatchAndRestartDoesNotRepeat()
    {
        var store = new MemoryStore(); var notifications = new Notifications();
        notifications.BeforeShow = () => Assert.Contains(store.Ledger.EffectiveEntries, e => e.ConsumedThresholds.Contains(95));
        using (var service = new UsageAlertService(store, notifications))
        {
            service.Observe([State(96)], Enabled, Now);
            service.Observe([State(96)], Enabled, Now);
            Assert.Single(notifications.Notices);
        }
        var laterNotifications = new Notifications();
        using var restarted = new UsageAlertService(store, laterNotifications);
        restarted.Observe([State(99, observed: Now.AddSeconds(30))], Enabled, Now.AddSeconds(30));
        Assert.Empty(laterNotifications.Notices);
    }

    [Fact]
    public void FailedSaveNeverNotifiesAndRecoversOnlyAfterSuccessfulCommit()
    {
        var store = new MemoryStore { RejectSave = true }; var notifications = new Notifications();
        using var service = new UsageAlertService(store, notifications);
        service.Observe([State(96)], Enabled, Now);
        Assert.Empty(notifications.Notices); Assert.Empty(store.Ledger.EffectiveEntries);
        Assert.Contains("local state", service.StatusMessage(Enabled));
        service.Observe([State(96) with { Status = ProviderStatus.Error, Failure = FailureKind.Network }], Enabled, Now);
        Assert.Contains("local state", service.StatusMessage(Enabled));
        store.RejectSave = false;
        service.Observe([State(96)], Enabled, Now);
        Assert.Single(notifications.Notices); Assert.DoesNotContain("local state", service.StatusMessage(Enabled));
    }

    [Fact]
    public void UnreadableStateFailsClosedAndCannotBeResetByTestNotification()
    {
        var store = new MemoryStore { RejectLoad = true }; var notifications = new Notifications();
        using var service = new UsageAlertService(store, notifications);
        service.Observe([State(96)], Enabled, Now);
        service.SendTest();
        Assert.Empty(notifications.Notices); Assert.Equal(0, store.Saves); Assert.Equal(1, notifications.Tests);
        Assert.Contains("local state", service.StatusMessage(Enabled));
    }

    [Theory]
    [InlineData((int)AlertNotificationResult.Disabled)]
    [InlineData((int)AlertNotificationResult.Failed)]
    [InlineData((int)AlertNotificationResult.Unavailable)]
    public void FailedOrDeniedSubmissionDoesNotReplayReservedWarnings(int resultValue)
    {
        var store = new MemoryStore(); var notifications = new Notifications { Result = (AlertNotificationResult)resultValue };
        using var service = new UsageAlertService(store, notifications);
        service.Observe([State(96)], Enabled, Now);
        notifications.Result = AlertNotificationResult.Submitted;
        service.Observe([State(96, observed: Now.AddSeconds(30))], Enabled, Now.AddSeconds(30));
        Assert.Single(notifications.Notices);
    }

    [Fact]
    public void OneThrowingSubmissionDoesNotDiscardOtherReservedWindows()
    {
        var store = new MemoryStore(); var notifications = new Notifications { ThrowFirst = true };
        using var service = new UsageAlertService(store, notifications);
        service.Observe([State(96), State(91, "Claude Code")], Enabled, Now);
        Assert.Equal(2, notifications.Notices.Count);
        Assert.Equal(2, store.Ledger.EffectiveEntries.Length);
    }

    [Fact]
    public void LateWindowsFailureIsReportedEvenAfterSubmissionWasAccepted()
    {
        var notifications = new Notifications();
        using var service = new UsageAlertService(new MemoryStore(), notifications);
        service.SendTest();
        Assert.Contains("submitted", service.StatusMessage(Enabled));
        notifications.Status = AlertNotificationStatus.Failed;
        Assert.Contains("delivery failed", service.StatusMessage(Enabled));
    }

    [Fact]
    public void SnoozeSurvivesServiceRestartAndFirstFreshPostSnoozeBaseline()
    {
        var store = new MemoryStore(); var first = new Notifications(); var settings = Enabled with { SnoozedUntil = Now.AddHours(1) };
        var reset = Now.AddHours(5);
        using (var service = new UsageAlertService(store, first))
            service.Observe([State(70, reset: reset)], settings, Now);
        var resumed = new Notifications(); using var restarted = new UsageAlertService(store, resumed);
        restarted.Observe([State(92, observed: Now.AddHours(2), reset: reset)], settings, Now.AddHours(2));
        Assert.Empty(resumed.Notices);
        restarted.Observe([State(96, observed: Now.AddHours(2).AddMinutes(1), reset: reset)], settings, Now.AddHours(2).AddMinutes(1));
        Assert.Equal(95, Assert.Single(resumed.Notices).ThresholdPercent);
    }

    [Fact]
    public void LogsAreCategoriesAndNotificationClickDoesNotTouchLedger()
    {
        var store = new MemoryStore(); var notifications = new Notifications(); var log = new List<string>();
        using var service = new UsageAlertService(store, notifications, log.Add);
        var opened = 0; service.OpenRequested += (_, _) => opened++;
        service.Observe([State(96)], Enabled, Now); var saves = store.Saves;
        notifications.Activate();
        Assert.Equal(1, opened); Assert.Equal(saves, store.Saves);
        Assert.All(log, value => Assert.Matches("^alerts\\.[a-z-]+$", value));
    }

    private static ProviderState State(double percent, string provider = "Codex", DateTimeOffset? observed = null, DateTimeOffset? reset = null) =>
        new(provider, ProviderStatus.Ready, new UsageSnapshot([new(provider == "Codex" ? "codex/primary" : "five_hour", "synthetic",
            percent, reset ?? Now.AddHours(5), 300)], observed ?? Now, "synthetic") { Binding = new("synthetic account context") },
            Authentication: AuthenticationStatus.Verified);

    private sealed class MemoryStore : IAlertLedgerStore
    {
        internal AlertLedger Ledger = new();
        internal int Loads, Saves;
        internal bool RejectLoad, RejectSave;
        public AlertLedgerStoreStatus Status => AlertLedgerStoreStatus.Ready;
        public bool TryLoad(out AlertLedger ledger) { Loads++; ledger = Ledger; return !RejectLoad; }
        public bool TrySave(AlertLedger ledger) { Saves++; if (RejectSave) return false; Ledger = ledger; return true; }
        public string Pseudonymize(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        public void Dispose() { }
    }
    private sealed class Notifications : IAlertNotifications
    {
        internal readonly List<UsageAlertNotice> Notices = [];
        internal int Tests;
        internal bool ThrowFirst;
        internal Action? BeforeShow;
        internal AlertNotificationResult Result = AlertNotificationResult.Submitted;
        public AlertNotificationStatus Status { get; set; } = AlertNotificationStatus.Available;
        public event EventHandler? OpenRequested;
        public AlertNotificationResult Show(UsageAlertNotice notice)
        {
            BeforeShow?.Invoke(); Notices.Add(notice);
            if (ThrowFirst && Notices.Count == 1) throw new InvalidOperationException("synthetic failure");
            return Result;
        }
        public AlertNotificationResult ShowTest() { Tests++; return Result; }
        internal void Activate() => OpenRequested?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }
}
