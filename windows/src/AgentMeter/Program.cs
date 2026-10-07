using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMeter.Core;

namespace AgentMeter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var instanceName = "Local\\Llumi.V1." + Environment.UserName;
        if (args.Contains("--quit", StringComparer.Ordinal))
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(instanceName + ".Quit", out var quitSignal))
                { using (quitSignal) quitSignal.Set(); }
                return 0;
            }
            catch (UnauthorizedAccessException) { return 1; }
        }
        ApplicationConfiguration.Initialize();
        using var singleton = new Mutex(true, instanceName, out var isFirst);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
        if (!isFirst)
        {
            if (!args.Contains("--startup", StringComparer.Ordinal)) showEvent.Set();
            return 0;
        }
        // Hold the historical mutex too: an old AgentMeter cannot start polling alongside Llumi.
        using var legacy = new Mutex(true, "Local\\AgentMeter.V0.1." + Environment.UserName, out var legacyFirst);
        if (!legacyFirst) { singleton.ReleaseMutex(); return 0; }
        if (!PackagedEnvironment.HasIdentity)
            LegacyPreferences.Migrate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentMeter"), PackagedEnvironment.DataDirectory);
        var log = new DiagnosticLog(Path.Combine(PackagedEnvironment.DataDirectory, "logs"));
        if (args.Length == 2 && args[0] == "--probe")
        {
            var coordinator = new RefreshCoordinator(CreateProviders(log.Write), log.Write);
            var preferences = PreferenceStore.Default().Load();
            coordinator.SetEnabled("Codex", preferences.CodexEnabled);
            coordinator.SetEnabled("Claude Code", preferences.ClaudeEnabled);
            coordinator.RefreshAsync().GetAwaiter().GetResult();
            File.WriteAllText(args[1], JsonSerializer.Serialize(coordinator.States, new JsonSerializerOptions
            { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
            return coordinator.States.Any(s => s.Status == ProviderStatus.Ready) ? 0 : 1;
        }

        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Quit");
        try
        {
            log.Write("application.started");
            Application.ThreadException += (_, _) => log.Write("application.ui-error");
            using var context = new TrayContext(new RefreshCoordinator(CreateProviders(log.Write), log.Write), log, showEvent, quitEvent: quitEvent);
            if (!args.Contains("--startup", StringComparer.Ordinal)) context.OpenPanel();
            Application.Run(context);
            log.Write("application.exited");
            return 0;
        }
        finally { legacy.ReleaseMutex(); singleton.ReleaseMutex(); }
    }

    private static IUsageProvider[] CreateProviders(Action<string> log) => [new CodexProvider(clientVersion: typeof(Program).Assembly.GetName().Version?.ToString(3)), new ClaudeProvider(log)];
}
