using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using AgentMeter.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AgentMeter;

internal enum AlertNotificationStatus
{
    NotInitialized, Available, DisabledForApplication, DisabledForUser, DisabledByPolicy,
    DisabledByManifest, SettingsUnavailable, Unavailable, Failed, Disposed
}

internal enum AlertNotificationResult { Submitted, Disabled, Unavailable, Failed }

internal interface IAlertNotifications : IDisposable
{
    AlertNotificationStatus Status { get; }
    event EventHandler? OpenRequested;
    AlertNotificationResult Show(UsageAlertNotice notice);
    AlertNotificationResult ShowTest();
}

internal interface IAlertNotificationTransport : IDisposable
{
    AlertNotificationStatus Status { get; }
    event EventHandler? OpenRequested;
    AlertNotificationResult Show(string title, string body);
}

internal sealed class AlertNotifications : IAlertNotifications
{
    private readonly IAlertNotificationTransport _transport;
    internal AlertNotifications(IAlertNotificationTransport? transport = null)
    {
        _transport = transport ?? new NativeAlertNotificationTransport();
        _transport.OpenRequested += OnOpenRequested;
    }
    public AlertNotificationStatus Status => _transport.Status;
    public event EventHandler? OpenRequested;
    public AlertNotificationResult Show(UsageAlertNotice notice)
    {
        var provider = notice.Provider.Equals("Codex", StringComparison.OrdinalIgnoreCase) ? "Codex" :
            UsagePresentation.IsClaude(notice.Provider) ? "Claude Code" : null;
        if (provider is null || string.IsNullOrWhiteSpace(notice.WindowLabel) || notice.WindowLabel.Length > 160 ||
            notice.WindowLabel.Any(char.IsControl) || notice.ThresholdPercent is < 1 or > 100 ||
            !double.IsFinite(notice.UsedPercent) || notice.UsedPercent is < 0 or > 100)
            return AlertNotificationResult.Failed;
        // The threshold is exact; avoid presenting invented fractional precision in a banner.
        var body = $"{notice.WindowLabel}: {notice.ThresholdPercent}% used threshold reached. " +
            $"Resets {notice.ResetsAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}. " +
            "Open Llumi for current usage.";
        return _transport.Show($"Llumi — {provider} usage", body);
    }
    public AlertNotificationResult ShowTest() => _transport.Show("Llumi — test notification",
        "Usage alerts are connected. Windows notification settings or Do Not Disturb may hide banners. This test does not change your usage alerts.");
    private void OnOpenRequested(object? sender, EventArgs args) => OpenRequested?.Invoke(this, EventArgs.Empty);
    public void Dispose() { _transport.OpenRequested -= OnOpenRequested; _transport.Dispose(); }
}

// Uses the inbox local-toast API already present in the Windows target framework.
// The desktop callback opens the running instance; it does not register a background
// COM server or add executable command-line entry points. Owned toasts are removed
// on clean exit. Activation following an unexpected process exit is not guaranteed.
internal sealed class NativeAlertNotificationTransport : IAlertNotificationTransport
{
    private readonly object _sync = new();
    private readonly string _appId;
    private readonly string _shortcutName;
    private readonly string? _executable;
    private readonly bool _review;
    private readonly List<ToastNotification> _toasts = [];
    private ToastNotifier? _notifier;
    private bool _packaged;
    private string? _ownedShortcut;
    private bool _disposed;
    private AlertNotificationStatus _status = AlertNotificationStatus.NotInitialized;
    internal string DiagnosticStage { get; private set; } = "NotInitialized";
    internal int? DiagnosticHResult { get; private set; }
    internal bool DiagnosticHasPackageIdentity => PackagedEnvironment.HasIdentity;

    internal NativeAlertNotificationTransport() : this("Llumi.Desktop.Alerts", "Llumi Alerts.lnk", null, false) { }
    private NativeAlertNotificationTransport(string appId, string shortcutName, string? executable, bool review)
    { _appId = appId; _shortcutName = shortcutName; _executable = executable; _review = review; }

    // Only the non-shipping ReviewHost can reach this through InternalsVisibleTo.
    // Native smoke checks use a separate identity and remove their exact owned link.
    internal static NativeAlertNotificationTransport ForReview(string executable) =>
        new("Llumi.LocalAlertReview", "Llumi Alert Review.lnk", executable, true);

    public event EventHandler? OpenRequested;
    public AlertNotificationStatus Status
    {
        get
        {
            lock (_sync)
            {
                if (_disposed) return AlertNotificationStatus.Disposed;
                if (_notifier is not null && _status != AlertNotificationStatus.Failed)
                {
                    try { _status = ReadSetting(() => _notifier.Setting, !_packaged); }
                    catch (Exception ex) when (IsNativeFailure(ex)) { _status = AlertNotificationStatus.Unavailable; }
                }
                return _status;
            }
        }
    }

    public AlertNotificationResult Show(string title, string body)
    {
        lock (_sync)
        {
            if (_disposed) return AlertNotificationResult.Unavailable;
            if (title.Length > 200 || body.Length > 1024 || title.Any(char.IsControl) || body.Any(char.IsControl))
                return AlertNotificationResult.Failed;
            try
            {
                if (_notifier is null)
                {
                    if (!_review && PackagedEnvironment.HasIdentity)
                    {
                        DiagnosticStage = "PackagedNotifier";
                        _notifier = ToastNotificationManager.CreateToastNotifier();
                        _packaged = true;
                    }
                    else
                    {
                        var executable = _executable ?? Environment.ProcessPath;
                        // Tests/library hosts cannot silently create production shell state.
                        if (executable is null || !Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
                            (!_review && !Path.GetFileName(executable).Equals("Llumi.exe", StringComparison.OrdinalIgnoreCase)))
                        { _status = AlertNotificationStatus.Unavailable; return AlertNotificationResult.Unavailable; }
                        DiagnosticStage = "ShortcutRegistration";
                        _ownedShortcut = AlertToastShortcut.Ensure(executable, _appId, _shortcutName,
                            _review ? "Llumi local review — synthetic" : "Llumi usage alerts");
                        DiagnosticStage = "DesktopNotifier";
                        _notifier = ToastNotificationManager.CreateToastNotifier(_appId);
                    }
                }
                DiagnosticStage = "NotificationSetting";
                _status = ReadSetting(() => _notifier.Setting, !_packaged);
                if (_status is not (AlertNotificationStatus.Available or AlertNotificationStatus.SettingsUnavailable))
                    return IsDisabled(_status) ? AlertNotificationResult.Disabled : AlertNotificationResult.Unavailable;
                DiagnosticStage = "ToastContent";
                var toast = CreateToast(_review ? "Llumi local review — synthetic" : title, body, DateTimeOffset.UtcNow);
                toast.Activated += OnActivated;
                toast.Failed += OnFailed;
                _toasts.Add(toast);
                // Bound native handles and notification history without losing the ledger.
                if (_toasts.Count > 32) RemoveToast(_toasts[0]);
                DiagnosticStage = "ToastSubmission";
                _notifier.Show(toast);
                DiagnosticStage = "Submitted";
                DiagnosticHResult = null;
                return AlertNotificationResult.Submitted;
            }
            catch (Exception ex) when (IsNativeFailure(ex))
            { DiagnosticHResult = ex.HResult; _status = AlertNotificationStatus.Failed; return AlertNotificationResult.Failed; }
        }
    }

    internal static XmlDocument CreateXml(string title, string body)
    {
        var xml = new XmlDocument();
        xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text/><text/></binding></visual></toast>");
        var text = xml.GetElementsByTagName("text");
        text[0].AppendChild(xml.CreateTextNode(title));
        text[1].AppendChild(xml.CreateTextNode(body));
        return xml;
    }

    internal static ToastNotification CreateToast(string title, string body, DateTimeOffset now) => new(CreateXml(title, body))
    {
        ExpirationTime = now.AddHours(12),
        ExpiresOnReboot = true,
        NotificationMirroring = NotificationMirroring.Disabled,
        // Random shell-only IDs never expose the account/window reservation key.
        Tag = Guid.NewGuid().ToString("N")[..16],
        Group = "llumi-usage"
    };

    internal static AlertNotificationStatus MapSetting(NotificationSetting setting) => setting switch
    {
        NotificationSetting.Enabled => AlertNotificationStatus.Available,
        NotificationSetting.DisabledForApplication => AlertNotificationStatus.DisabledForApplication,
        NotificationSetting.DisabledForUser => AlertNotificationStatus.DisabledForUser,
        NotificationSetting.DisabledByGroupPolicy => AlertNotificationStatus.DisabledByPolicy,
        NotificationSetting.DisabledByManifest => AlertNotificationStatus.DisabledByManifest,
        _ => AlertNotificationStatus.Unavailable
    };
    internal static AlertNotificationStatus ReadSetting(Func<NotificationSetting> read, bool desktop)
    {
        try { return MapSetting(read()); }
        // A new desktop identity can lack a settings entry before its first toast.
        // This is unknown permission, not Enabled. Show remains subject to the
        // Windows broker's notification policy; never override a known block.
        // Microsoft's ToastNotifierCompat preregisters identityless apps before
        // reading Setting for this reason. Submit the user's actual requested
        // toast instead of creating an extra synthetic registration notification.
        // https://github.com/CommunityToolkit/WindowsCommunityToolkit/blob/main/Microsoft.Toolkit.Uwp.Notifications/Toasts/Compat/ToastNotifierCompat.cs
        catch (COMException ex) when (desktop && ex.HResult == unchecked((int)0x80070490))
        { return AlertNotificationStatus.SettingsUnavailable; }
    }
    private static bool IsDisabled(AlertNotificationStatus status) => status is
        AlertNotificationStatus.DisabledForApplication or AlertNotificationStatus.DisabledForUser or
        AlertNotificationStatus.DisabledByPolicy or AlertNotificationStatus.DisabledByManifest;
    private static bool IsNativeFailure(Exception ex) => ex is COMException or InvalidOperationException or
        UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or TypeLoadException;

    private void OnActivated(ToastNotification sender, object args)
    {
        lock (_sync) { if (_disposed) return; RemoveToast(sender); }
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }
    private void OnFailed(ToastNotification sender, ToastFailedEventArgs args)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                DiagnosticStage = "ToastFailedCallback";
                DiagnosticHResult = args.ErrorCode.HResult;
                _status = AlertNotificationStatus.Failed;
                RemoveToast(sender);
            }
        }
    }
    private void RemoveToast(ToastNotification toast)
    {
        toast.Activated -= OnActivated;
        toast.Failed -= OnFailed;
        try { _notifier?.Hide(toast); }
        catch (Exception ex) when (IsNativeFailure(ex)) { }
        try
        {
            if (_packaged) ToastNotificationManager.History.Remove(toast.Tag, toast.Group);
            else ToastNotificationManager.History.Remove(toast.Tag, toast.Group, _appId);
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { }
        _toasts.Remove(toast);
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var toast in _toasts.ToArray()) RemoveToast(toast);
            if (_review && _ownedShortcut is not null && _executable is not null)
                AlertToastShortcut.RemoveOwned(_ownedShortcut, _executable, _appId);
            _notifier = null;
            _status = AlertNotificationStatus.Disposed;
        }
    }
}

// Microsoft desktop toasts require a Start Menu shortcut carrying the AUMID.
// This narrowly owns one separate shortcut; existing user/installer links are not edited.
internal static class AlertToastShortcut
{
    private static readonly PropertyKey AppIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    internal static string Ensure(string executable, string appId, string name, string description)
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Start Menu is unavailable.");
        var path = Path.Combine(directory, name);
        if (File.Exists(path) && !HasAppId(path, appId))
            throw new InvalidOperationException("Notification shortcut is already owned.");
        if (File.Exists(path) && Matches(path, executable, appId)) return path;
        Directory.CreateDirectory(directory);
        var link = (IShellLink)new ShellLink();
        try
        {
            link.SetPath(executable);
            link.SetArguments("");
            link.SetDescription(description);
            link.SetIconLocation(executable, 0);
            var properties = (IPropertyStore)link;
            var value = PropVariant.FromString(appId);
            var key = AppIdKey;
            try { properties.SetValue(ref key, ref value); properties.Commit(); }
            finally { value.Dispose(); }
            ((IPersistFile)link).Save(path, true);
            return path;
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    internal static void RemoveOwned(string path, string executable, string appId)
    {
        try { if (File.Exists(path) && Matches(path, executable, appId)) File.Delete(path); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException) { }
    }
    private static bool HasAppId(string path, string appId) => Read(path).AppId == appId;
    private static bool Matches(string path, string executable, string appId)
    {
        var actual = Read(path);
        return actual.AppId == appId && string.Equals(actual.Path, executable, StringComparison.OrdinalIgnoreCase) && actual.Arguments.Length == 0;
    }
    private static (string Path, string AppId, string Arguments) Read(string path)
    {
        var link = (IShellLink)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var key = AppIdKey;
            ((IPropertyStore)link).GetValue(ref key, out var value);
            using (value)
            {
                var target = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, 0, 0);
                var arguments = new StringBuilder(32768);
                link.GetArguments(arguments, arguments.Capacity);
                return (target.ToString(), value.Text ?? "", arguments.ToString());
            }
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, nint data, uint flags);
        void GetIDList(out nint value);
        void SetIDList(nint value);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short value);
        void SetHotkey(short value);
        void GetShowCmd(out int value);
        void SetShowCmd(int value);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { internal Guid Format = format; internal uint Id = id; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, [Out] out PropVariant value);
        void SetValue(ref PropertyKey key, [In] ref PropVariant value);
        void Commit();
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort _type;
        [FieldOffset(8)] private nint _value;
        internal string? Text => _type == 31 ? Marshal.PtrToStringUni(_value) : null;
        internal static PropVariant FromString(string text) => new() { _type = 31, _value = Marshal.StringToCoTaskMemUni(text) };
        public void Dispose() { PropVariantClear(ref this); }
    }
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear([In, Out] ref PropVariant value);
}
