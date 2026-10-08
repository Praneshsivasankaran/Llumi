using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ActivityParityTests
{
    [Theory]
    [InlineData("Codex", "codex.exe", true)]
    [InlineData("Codex", "codex.exe --no-alt-screen --sandbox workspace-write", true)]
    [InlineData("Codex", "codex.exe --search --oss", true)]
    [InlineData("Codex", "codex.exe app-server", false)]
    [InlineData("Codex", "codex.exe --version", false)]
    [InlineData("Codex", "codex.exe exec", false)]
    [InlineData("Codex", "codex.exe --sandbox", false)]
    [InlineData("Codex", "codex.exe --sandbox unknown", false)]
    [InlineData("Claude Code", "claude.exe", true)]
    [InlineData("Claude Code", "claude.exe --safe-mode --effort high", true)]
    [InlineData("Claude Code", "claude.exe --verbose --no-chrome", true)]
    [InlineData("Claude Code", "claude.exe auth status", false)]
    [InlineData("Claude Code", "claude.exe --print", false)]
    [InlineData("Claude Code", "claude.exe --help", false)]
    [InlineData("Claude Code", "claude.exe private-prompt", false)]
    [InlineData("Claude Code", "claude.exe --unknown", false)]
    public void InteractiveGrammarFailsClosedForHelperAndUnknownModes(string provider, string invocation, bool expected) =>
        Assert.Equal(expected, ActivityPolicy.Interactive(provider, invocation, provider == "Codex" ? "codex.exe" : "claude.exe"));

    [Fact]
    public void StartupReadFailureMustNotHideSecondProviderForItsEntireLifetime()
    {
        var cache = new ActivityModeCache();
        var key = (42, 123L);
        Assert.False(cache.Read(key, () => false)); // process parameters not ready
        Assert.True(cache.Read(key, () => true)); // next ordinary sample
        Assert.True(cache.Read(key, () => throw new Exception("verified mode is cached")));
        cache.Retain([]);
        Assert.False(cache.Read(key, () => false));
        Assert.False(cache.Read((42, 124), () => false)); // PID reuse
        for (var i = 0; i < 3; i++) Assert.False(cache.Read((7, 1), () => false)); // helper never guessed active
    }

    [Fact]
    public void BoundedReaderStopsBeforeReadingPossiblePromptContents()
    {
        const string command = "codex.exe PRIVATE-PROMPT-MUST-NOT-BE-COLLECTED";
        var maxRead = 0;
        Assert.False(ActivityPolicy.Interactive("Codex", i => { maxRead = Math.Max(i, maxRead); return command[i]; }, "codex.exe", command.Length));
        Assert.Equal("codex.exe ".Length, maxRead);
        Assert.True(ActivityPolicy.Interactive("Codex", "\"C:\\Program Files\\Codex\\codex.exe\" --search", @"C:\Program Files\Codex\codex.exe"));
    }

    [Fact]
    public void DesktopSwitchMinimizeAndIndependentCliComposeWithoutDuplicates()
    {
        Assert.True(ActivityPolicy.DesktopActive(true, true, false));
        Assert.False(ActivityPolicy.DesktopActive(false, true, false));
        Assert.False(ActivityPolicy.DesktopActive(true, true, true));
        Assert.False(ActivityPolicy.DesktopActive(true, false, false));
        Assert.Equal(["Codex", "Claude Code"], new ActivitySnapshot(new(true, true), new(true, true)).Providers);
        Assert.Equal(["Codex"], new ActivitySnapshot(new(true, false), new(false, false)).Providers);
        Assert.Empty(ActivitySnapshot.Empty.Providers);
    }

    [Fact]
    public void DesktopIdentityDoesNotConfuseChatGptOrArbitraryExecutableWithCodex()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        Assert.Equal("Codex", WindowsActivitySource.DesktopProvider(Path.Combine(root, "OpenAI.Codex_1_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")));
        Assert.Null(WindowsActivitySource.DesktopProvider(Path.Combine(root, "OpenAI.ChatGPT-Desktop_1_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")));
        Assert.Null(WindowsActivitySource.DesktopProvider(@"C:\untrusted\Codex.exe"));
        Assert.Null(WindowsActivitySource.DesktopProvider(Path.Combine(root, "Claude_1_x64__wrong", "app", "Claude.exe")));
    }

    [Fact]
    public void PreferencesRoundTripAndMalformedDataFallsBackWithoutAccountState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentMeter.PreferenceTest." + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "preferences.json"); var store = new PreferenceStore(path);
        try
        {
            Assert.Equal(new Preferences(), store.Load());
            var desired = new Preferences(false, false, Appearance.Light);
            Assert.True(store.Save(desired)); Assert.Equal(desired, store.Load());
            Assert.DoesNotContain("account", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(path, "{malformed}"); Assert.Equal(new Preferences(), store.Load());
            File.WriteAllText(path, "{\"Appearance\":999}"); Assert.Equal(new Preferences(), store.Load());
        }
        finally { File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    [Fact]
    public void ProviderFlagsMigrateFromOldPreferencesAndRoundTripAsTypedBooleans()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Llumi.ProviderPreferenceTest." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json"); var store = new PreferenceStore(path);
        try
        {
            File.WriteAllText(path, "{\"CompactMonitor\":false,\"TrayIcon\":true,\"Appearance\":1}");
            var old = store.Load();
            Assert.False(old.CompactMonitor); Assert.Equal(Appearance.Light, old.Appearance);
            Assert.True(old.CodexEnabled); Assert.True(old.ClaudeEnabled);
            var selected = old with { CodexEnabled = false, ClaudeEnabled = false };
            Assert.True(store.Save(selected)); Assert.Equal(selected, store.Load());
            Assert.True(store.HasValidExistingPreferences());
            foreach (var invalid in new[] { "{\"CodexEnabled\":\"false\"}", "{\"CompactMonitor\":true,\"CodexEnabled\":\"false\"}", "{\"ClaudeEnabled\":0}", "{\"CodexEnabled\":false,\"CodexEnabled\":true}", new string(' ', 4097) })
            { File.WriteAllText(path, invalid); Assert.Equal(new Preferences(), store.Load()); Assert.False(store.HasValidExistingPreferences()); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DisabledActivityProviderIsNeverDiscoveredOrScannedAndEnableClearsDiscoveryCache()
    {
        var cx = 0; var cl = 0; var scans = 0;
        var source = new WindowsActivitySource(() => { cx++; return "cx"; }, () => { cl++; return "cl"; },
            (codex, claude, cxPath, clPath) =>
            {
                scans++;
                Assert.Equal(codex ? "cx" : null, cxPath); Assert.Equal(claude ? "cl" : null, clPath);
                return new(new(true, true), new(true, true));
            });
        source.SetEnabled(false, true);
        Assert.Equal(new[] { "Claude Code" }, source.Capture().Providers);
        Assert.Equal(0, cx); Assert.Equal(1, cl);
        source.Capture(); Assert.Equal(1, cl);
        source.SetEnabled(false, false);
        Assert.Empty(source.Capture().Providers); Assert.Equal(2, scans);
        source.SetEnabled(true, false);
        Assert.Equal(new[] { "Codex" }, source.Capture().Providers);
        Assert.Equal(1, cx); Assert.Equal(1, cl);
    }

    [Fact]
    public async Task DisablingSynchronizesWithRunningActivityCapture()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disableStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new WindowsActivitySource(() => "cx", () => "cl", (_, _, _, _) =>
        { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(3)); return new(new(true), new(true)); });
        var capture = Task.Run(source.Capture);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var disabled = Task.Run(() => { disableStarted.TrySetResult(); source.SetEnabled(false, false); });
            await disableStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(30); Assert.False(disabled.IsCompleted);
            release.Set(); await capture; await disabled;
            Assert.Empty(source.Capture().Providers);
        }
        finally { release.Set(); await capture; }
    }

    [Theory]
    [InlineData("codex", false, false, true, false)]
    [InlineData("ChatGPT", true, false, true, false)]
    [InlineData("claude", true, true, false, false)]
    [InlineData("codex", false, true, false, true)]
    [InlineData("ChatGPT", false, true, false, false)]
    [InlineData("ChatGPT", true, true, false, true)]
    [InlineData("claude", false, false, true, true)]
    public void DisabledProcessCandidatesAreExcludedBeforePathAndModeRead(string name, bool foreground, bool codex, bool claude, bool expected) =>
        Assert.Equal(expected, WindowsActivitySource.CandidateProcess(name, foreground, codex, claude));

    [Fact]
    public async Task OwnedUsageHelperNeverCountsAsInteractiveAndIsUnregisteredAfterCleanup()
    {
        var host = Path.Combine(AppContext.BaseDirectory, "AgentMeter.ProcessHost.exe");
        var process = ProviderProcess.Start(host, []);
        var pid = process.ProcessId; Assert.True(ProviderProcess.IsOwned(pid));
        await process.DisposeAsync(); Assert.False(ProviderProcess.IsOwned(pid));
    }
}
