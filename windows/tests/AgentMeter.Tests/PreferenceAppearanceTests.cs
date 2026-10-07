namespace AgentMeter.Tests;

public sealed class PreferenceAppearanceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Llumi-appearance-fixture-" + Guid.NewGuid().ToString("N"));
    private string PathFor(string name) => Path.Combine(directory, name);

    [Fact]
    public void FreshPreferencesAndMissingStoreChooseLightRatherThanSystemAppearance()
    {
        Assert.Equal(Appearance.Light, new Preferences().Appearance);
        var store = new PreferenceStore(PathFor("preferences.json"));
        Assert.Equal(Appearance.Light, store.Load().Appearance);
        Assert.False(store.HasValidExistingPreferences());
        Assert.False(File.Exists(PathFor("preferences.json")));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Appearance\":99}")]
    [InlineData("{\"Appearance\":\"Dark\"}")]
    [InlineData("{\"Appearance\":0,\"Appearance\":2}")]
    [InlineData("{\"Appearance\":2,\"CodexEnabled\":null}")]
    public void InvalidOrUnrecognizedPreferencesUseLightWithoutTreatingThemAsAnExplicitAppearanceChoice(string content)
    {
        Directory.CreateDirectory(directory); var path = PathFor("preferences.json");
        File.WriteAllText(path, content);
        var store = new PreferenceStore(path);
        Assert.Equal(Appearance.Light, store.Load().Appearance);
        Assert.False(store.HasValidExistingPreferences());
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{\"CompactMonitor\":false,\"TrayIcon\":true}", false, true)]
    [InlineData("{\"CodexEnabled\":false,\"ClaudeEnabled\":true}", true, false)]
    public void OlderPreferencesWithoutAppearanceChooseLightAndKeepTheirOtherSettings(string content, bool compactMonitor, bool codexEnabled)
    {
        Directory.CreateDirectory(directory); var path = PathFor("preferences.json");
        File.WriteAllText(path, content);
        var store = new PreferenceStore(path); var loaded = store.Load();
        Assert.Equal(Appearance.Light, loaded.Appearance);
        Assert.Equal(compactMonitor, loaded.CompactMonitor); Assert.Equal(codexEnabled, loaded.CodexEnabled);
        Assert.True(loaded.TrayIcon); Assert.True(loaded.ClaudeEnabled);
        Assert.True(store.HasValidExistingPreferences());
        Assert.True(store.Save(loaded with { ClaudeEnabled = false }));
        Assert.Equal(Appearance.Light, new PreferenceStore(path).Load().Appearance);
    }

    [Theory]
    [InlineData(0, "System")]
    [InlineData(1, "Light")]
    [InlineData(2, "Dark")]
    public void ExplicitAppearanceCodesSurviveReloadAndUnrelatedPreferenceEdits(int code, string expected)
    {
        Directory.CreateDirectory(directory); var path = PathFor("preferences.json");
        File.WriteAllText(path, $"{{\"Appearance\":{code},\"CodexEnabled\":false}}");
        var loaded = new PreferenceStore(path).Load();
        Assert.Equal(expected, loaded.Appearance.ToString()); Assert.False(loaded.CodexEnabled);
        Assert.True(new PreferenceStore(path).Save(loaded with { CompactMonitor = false, ClaudeEnabled = false }));
        var reloaded = new PreferenceStore(path).Load();
        Assert.Equal(expected, reloaded.Appearance.ToString()); Assert.False(reloaded.CompactMonitor);
        Assert.False(reloaded.CodexEnabled); Assert.False(reloaded.ClaudeEnabled);
    }

    [Fact]
    public void LegacyPreferencesWithoutAppearanceMigrateTheirSettingsAndUseLight()
    {
        var legacy = PathFor("legacy"); var current = PathFor("current"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "v2-preferences.json"), "{\"TrayIcon\":false,\"CodexEnabled\":false}");
        LegacyPreferences.Migrate(legacy, current);
        var store = new PreferenceStore(Path.Combine(current, "v2-preferences.json"));
        var loaded = store.Load();
        Assert.Equal(Appearance.Light, loaded.Appearance); Assert.False(loaded.TrayIcon); Assert.False(loaded.CodexEnabled);
        Assert.True(loaded.ClaudeEnabled); Assert.True(store.HasValidExistingPreferences());
    }

    [Theory]
    [InlineData(0, "System", false)] [InlineData(0, "System", true)]
    [InlineData(1, "Light", false)] [InlineData(1, "Light", true)]
    [InlineData(2, "Dark", false)] [InlineData(2, "Dark", true)]
    public void MigrationPreservesAnExplicitLegacyOrCurrentAppearance(int code, string expected, bool currentChoice)
    {
        var legacy = PathFor("legacy"); var current = PathFor("current");
        Directory.CreateDirectory(legacy); Directory.CreateDirectory(current);
        var oldCode = currentChoice ? (code + 1) % 3 : code;
        var oldContent = $"{{\"Appearance\":{oldCode},\"CompactMonitor\":false}}";
        var oldPath = Path.Combine(legacy, "v2-preferences.json"); File.WriteAllText(oldPath, oldContent);
        var currentPath = Path.Combine(current, "v2-preferences.json");
        if (currentChoice) File.WriteAllText(currentPath, $"{{\"Appearance\":{code}}}");
        LegacyPreferences.Migrate(legacy, current);
        var loaded = new PreferenceStore(currentPath).Load();
        Assert.Equal(expected, loaded.Appearance.ToString()); Assert.False(loaded.CompactMonitor);
        LegacyPreferences.Migrate(legacy, current);
        Assert.Equal(expected, new PreferenceStore(currentPath).Load().Appearance.ToString());
        Assert.Equal(oldContent, File.ReadAllText(oldPath));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
