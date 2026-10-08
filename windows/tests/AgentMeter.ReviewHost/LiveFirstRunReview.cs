using System.ComponentModel;
using System.Text;
using AgentMeter;
using AgentMeter.Core;

// This development entry point bypasses production migration and injects every
// Llumi store. Real provider authentication remains entirely provider-owned.
internal static class LiveFirstRunReview
{
    internal const string MarkerName = ".llumi-local-review";
    internal const string MarkerContent = "Llumi isolated local review profile v1\n";

    internal static int Run(string directory, bool resume = false)
    {
        try
        {
            var profile = ValidateProfile(directory, resume);
            var instanceName = "Local\\Llumi.V1." + Environment.UserName;
            using var singleton = new Mutex(true, instanceName, out var isFirst);
            if (!isFirst) return AlreadyRunning(resume);
            try
            {
                using var legacy = new Mutex(true, "Local\\AgentMeter.V0.1." + Environment.UserName, out var legacyFirst);
                if (!legacyFirst) return AlreadyRunning(resume);
                try
                {
                    // Nothing above this point starts a collector, scans activity,
                    // writes review settings, or signals an installed app.
                    PrepareProfile(profile, resume);
                    using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
                    using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Quit");
                    var log = new DiagnosticLog(Path.Combine(profile, "logs"));
                    ThreadExceptionEventHandler error = (_, _) => log.Write("review.ui-error");
                    Application.ThreadException += error;
                    try
                    {
                        var providers = new IUsageProvider[]
                        {
                            new CodexProvider(clientVersion: typeof(TrayContext).Assembly.GetName().Version?.ToString(3)),
                            new ClaudeProvider(log.Write)
                        };
                        using var context = new TrayContext(new RefreshCoordinator(providers, log.Write), log, showEvent,
                            positions: new MonitorPositionStore(Path.Combine(profile, "monitor-position.json"), log.Write),
                            startup: new ReviewStartupRegistration(log.Write), quitEvent: quitEvent, captureActivity: null,
                            preferenceStore: new PreferenceStore(Path.Combine(profile, "v2-preferences.json")),
                            setupStore: new SetupCompletionStore(Path.Combine(profile, "setup-completed.json")),
                            reviewTitle: $"v{typeof(TrayContext).Assembly.GetName().Version?.ToString(3)} local " + (resume ? "review" : "first-time review"));
                        log.Write(resume ? "review.resumed.started" : "review.first-run.started");
                        context.OpenPanel();
                        Application.Run(context);
                        log.Write(resume ? "review.resumed.exited" : "review.first-run.exited");
                        return 0;
                    }
                    finally { Application.ThreadException -= error; }
                }
                finally { legacy.ReleaseMutex(); }
            }
            finally { singleton.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            System.Security.SecurityException or Win32Exception or WaitHandleCannotBeOpenedException)
        {
            MessageBox.Show(resume
                    ? "The local review could not resume. Choose an existing, marked local review profile outside Llumi, AgentMeter, and Microsoft Store app data, with no linked files or folders. Existing settings were not reset."
                    : "The local review could not start. Choose a new, empty directory on a local drive, outside Llumi, AgentMeter, and Microsoft Store app data. Existing settings were not reset.",
                resume ? "Llumi local review" : "Llumi local first-time review", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
    }

    private static int AlreadyRunning(bool resume)
    {
        MessageBox.Show("Llumi or AgentMeter is already running. Quit it normally from its tray menu, then " +
                (resume ? "resume the local review" : "start the local first-time review") + " again. The installed app has not been stopped or changed.",
            resume ? "Llumi local review" : "Llumi local first-time review", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 2;
    }

    internal static string PrepareProfile(string directory, bool resume = false)
    {
        var profile = ValidateProfile(directory, resume);
        if (resume) return profile;
        Directory.CreateDirectory(profile);
        ValidateProfile(profile);
        using (var marker = new FileStream(Path.Combine(profile, MarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            marker.Write(Encoding.UTF8.GetBytes(MarkerContent));
        return ValidateProfile(profile, resume: true);
    }

    internal static string ValidateProfile(string directory, bool resume = false)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute review directory is required.");
        var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var root = Path.GetPathRoot(profile);
        if (root is null || root.Length != 3 || root[1] != ':' || new DriveInfo(root).DriveType == DriveType.Network)
            throw new ArgumentException("The review profile must be on a local drive.");
        foreach (var appData in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) })
        foreach (var name in new[] { "Llumi", "AgentMeter", "Packages" })
        {
            var production = Path.Combine(appData, name);
            if (profile.Equals(production, StringComparison.OrdinalIgnoreCase) ||
                profile.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Production application data is not a review profile.");
        }
        if (File.Exists(profile)) throw new ArgumentException("The review profile must be a directory.");
        for (DirectoryInfo? ancestor = new(profile); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Linked directories are not review profiles.");
        if (resume)
        {
            if (!Directory.Exists(profile)) throw new ArgumentException("An existing marked review profile is required.");
            RejectLinkedChildren(new DirectoryInfo(profile));
            var expected = Encoding.UTF8.GetBytes(MarkerContent);
            using var marker = new FileStream(Path.Combine(profile, MarkerName), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (marker.Length != expected.Length) throw new ArgumentException("This directory is not a marked review profile.");
            Span<byte> actual = stackalloc byte[expected.Length];
            marker.ReadExactly(actual);
            if (!actual.SequenceEqual(expected)) throw new ArgumentException("This directory is not a marked review profile.");
        }
        else if (Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any())
            throw new ArgumentException("Use an empty review profile; existing files are preserved.");
        return profile;
    }

    private static void RejectLinkedChildren(DirectoryInfo directory)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var child in current.EnumerateFileSystemInfos())
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("Linked files and folders are not allowed in review profiles.");
                if (child is DirectoryInfo nested) pending.Push(nested);
            }
        }
    }
}

// A review checkbox changes only this process's state. It never reads/writes the
// Run registry key, Windows StartupTask, or an existing application's startup.
internal sealed class ReviewStartupRegistration(Action<string> log) : IStartupRegistration
{
    private bool enabled;
    public bool TryRead(out bool value) { value = enabled; return true; }
    public bool TrySet(bool value)
    { enabled = value; log(value ? "review.startup.simulated-on" : "review.startup.simulated-off"); return true; }
}
