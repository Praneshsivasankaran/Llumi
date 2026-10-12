using AgentMeter.Core;
using System.Runtime.InteropServices;
using Windows.UI.Notifications;

namespace AgentMeter.Tests;

public sealed class AlertNotificationsTests
{
    [Fact]
    public void NoticeContainsProviderWindowThresholdAndResetWithoutReservationIdentity()
    {
        var transport = new FakeTransport();
        using var notifications = new AlertNotifications(transport);
        var result = notifications.Show(new("Codex", "5-hour limit", 90, 96.123, DateTimeOffset.UtcNow.AddHours(2), "synthetic-private-reservation"));
        Assert.Equal(AlertNotificationResult.Submitted, result);
        Assert.Contains("Codex", transport.Title);
        Assert.Contains("5-hour limit", transport.Body);
        Assert.Contains("90% used threshold", transport.Body);
        Assert.Contains("Resets", transport.Body);
        Assert.DoesNotContain("96.123", transport.Body);
        Assert.DoesNotContain("synthetic-private-reservation", transport.Body);
    }

    [Fact]
    public void ExplicitTestAndActivationUseTransportWithoutLedgerOrProviderCalls()
    {
        var transport = new FakeTransport();
        using var notifications = new AlertNotifications(transport);
        var opened = 0;
        notifications.OpenRequested += (_, _) => opened++;
        Assert.Equal(AlertNotificationResult.Submitted, notifications.ShowTest());
        Assert.Contains("test notification", transport.Title);
        transport.Activate();
        Assert.Equal(1, opened);
        notifications.Dispose();
        transport.Activate();
        Assert.Equal(1, opened);
    }

    [Theory]
    [InlineData((int)AlertNotificationResult.Disabled)]
    [InlineData((int)AlertNotificationResult.Unavailable)]
    [InlineData((int)AlertNotificationResult.Failed)]
    public void NativeFailureOrDenialIsNotReportedAsSuccessfulDelivery(int value)
    {
        var result = (AlertNotificationResult)value;
        var transport = new FakeTransport { Result = result };
        using var notifications = new AlertNotifications(transport);
        Assert.Equal(result, notifications.ShowTest());
    }

    [Fact]
    public void InvalidNoticeNeverReachesTransport()
    {
        var transport = new FakeTransport();
        using var notifications = new AlertNotifications(transport);
        Assert.Equal(AlertNotificationResult.Failed, notifications.Show(new("Unknown", "Window", 90, 95, DateTimeOffset.UtcNow, "opaque")));
        Assert.Equal(AlertNotificationResult.Failed, notifications.Show(new("Codex", "Window\r\nunsafe", 90, 95, DateTimeOffset.UtcNow, "opaque")));
        Assert.Equal(AlertNotificationResult.Failed, notifications.Show(new("Codex", "Window", 90, double.NaN, DateTimeOffset.UtcNow, "opaque")));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(NotificationSetting.Enabled, (int)AlertNotificationStatus.Available)]
    [InlineData(NotificationSetting.DisabledForApplication, (int)AlertNotificationStatus.DisabledForApplication)]
    [InlineData(NotificationSetting.DisabledForUser, (int)AlertNotificationStatus.DisabledForUser)]
    [InlineData(NotificationSetting.DisabledByGroupPolicy, (int)AlertNotificationStatus.DisabledByPolicy)]
    [InlineData(NotificationSetting.DisabledByManifest, (int)AlertNotificationStatus.DisabledByManifest)]
    [InlineData((NotificationSetting)999, (int)AlertNotificationStatus.Unavailable)]
    public void NativeSettingMappingPreservesEveryBlockedState(NotificationSetting setting, int expected) =>
        Assert.Equal((AlertNotificationStatus)expected, NativeAlertNotificationTransport.MapSetting(setting));

    [Fact]
    public void OnlyUnregisteredDesktopSettingsRemainUnknownBeforeSubmission()
    {
        static NotificationSetting Missing() => throw new COMException("Synthetic", unchecked((int)0x80070490));
        Assert.Equal(AlertNotificationStatus.SettingsUnavailable, NativeAlertNotificationTransport.ReadSetting(Missing, desktop: true));
        Assert.Throws<COMException>(() => NativeAlertNotificationTransport.ReadSetting(Missing, desktop: false));
        Assert.Throws<COMException>(() => NativeAlertNotificationTransport.ReadSetting(
            () => throw new COMException("Synthetic", unchecked((int)0x80070005)), desktop: true));
        Assert.Equal(AlertNotificationStatus.DisabledForApplication,
            NativeAlertNotificationTransport.ReadSetting(() => NotificationSetting.DisabledForApplication, desktop: true));
    }

    [Fact]
    public void ToastContentIsTextNotInterpretedMarkup()
    {
        var xml = NativeAlertNotificationTransport.CreateXml("<synthetic>&title", "<audio src='remote'/>");
        Assert.Equal("<synthetic>&title", xml.GetElementsByTagName("text")[0].InnerText);
        Assert.Empty(xml.GetElementsByTagName("audio"));
    }

    [Fact]
    public void NativeToastCannotMirrorAndHasBoundedAnonymousHistoryIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var toast = NativeAlertNotificationTransport.CreateToast("Synthetic test", "No provider data", now);
        var second = NativeAlertNotificationTransport.CreateToast("Synthetic test", "No provider data", now);
        Assert.Equal(NotificationMirroring.Disabled, toast.NotificationMirroring);
        Assert.True(toast.ExpiresOnReboot);
        Assert.Equal(now.AddHours(12), toast.ExpirationTime);
        Assert.Equal("llumi-usage", toast.Group);
        Assert.Equal(16, toast.Tag.Length);
        Assert.NotEqual(toast.Tag, second.Tag);
    }

    [Fact]
    public void ConstructingNativeTransportDoesNotRegisterOrSubmitNotifications()
    {
        using var transport = new NativeAlertNotificationTransport();
        Assert.Equal(AlertNotificationStatus.NotInitialized, transport.Status);
    }

    private sealed class FakeTransport : IAlertNotificationTransport
    {
        public AlertNotificationStatus Status => AlertNotificationStatus.Available;
        public event EventHandler? OpenRequested;
        internal AlertNotificationResult Result { get; init; } = AlertNotificationResult.Submitted;
        internal string Title { get; private set; } = "";
        internal string Body { get; private set; } = "";
        internal int Calls { get; private set; }
        public AlertNotificationResult Show(string title, string body) { Title = title; Body = body; Calls++; return Result; }
        internal void Activate() => OpenRequested?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }
}
