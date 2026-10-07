using System.Text.Json;

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
    [InlineData("{\"Appearance\":2,\"LightAppearanceMigrated\":\"true\"}")]
    [InlineData("{\"LightAppearanceMigrated\":true}")]
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
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ExistingAppearanceResetsToLightExactlyOnceWhileOtherPreferencesSurvive(int code)
    {
        Directory.CreateDirectory(directory); var path = PathFor("preferences.json");
        File.WriteAllText(path, $"{{\"Appearance\":{code},\"CompactMonitor\":false,\"TrayIcon\":false,\"CodexEnabled\":false,\"ClaudeEnabled\":false}}");
        var loaded = new PreferenceStore(path).Load();
        Assert.Equal(new Preferences(false, false, Appearance.Light, false, false), loaded);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(document.RootElement.GetProperty(PreferenceStore.LightMigrationKey).GetBoolean());
        Assert.Equal((int)Appearance.Light, document.RootElement.GetProperty("Appearance").GetInt32());
        var migratedContent = File.ReadAllText(path);
        Assert.Equal(loaded, new PreferenceStore(path).Load());
        Assert.Equal(migratedContent, File.ReadAllText(path));
        foreach (var chosen in new[] { Appearance.Dark, Appearance.System, Appearance.Light })
        {
            var requested = loaded with { Appearance = chosen };
            Assert.True(new PreferenceStore(path).Save(requested));
            Assert.Equal(requested, new PreferenceStore(path).Load());
            Assert.True(new PreferenceStore(path).Save(requested with { TrayIcon = true }));
            Assert.Equal(requested with { TrayIcon = true }, new PreferenceStore(path).Load());
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void FreshExplicitChoiceIsSavedWithMigrationAndSurvivesRestarts(int appearance)
    {
        var path = PathFor("preferences.json"); var desired = new Preferences(Appearance: (Appearance)appearance);
        Assert.True(new PreferenceStore(path).Save(desired));
        Assert.Equal(desired, new PreferenceStore(path).Load());
        Assert.Equal(desired, new PreferenceStore(path).Load());
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void FailedMigrationDoesNotPersistCompletionOrPreventALaterExplicitChoice(int appearance)
    {
        Directory.CreateDirectory(directory); var path = PathFor("preferences.json");
        const string original = "{\"Appearance\":2,\"CompactMonitor\":false,\"CodexEnabled\":false}";
        File.WriteAllText(path, original);
        Directory.CreateDirectory(path + ".tmp");
        var light = new PreferenceStore(path).Load();
        Assert.Equal(new Preferences(CompactMonitor: false, CodexEnabled: false), light);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(light, new PreferenceStore(path).Load());
        var desired = light with { Appearance = (Appearance)appearance };
        Assert.False(new PreferenceStore(path).Save(desired));
        Assert.Equal(original, File.ReadAllText(path));
        Directory.Delete(path + ".tmp");
        Assert.True(new PreferenceStore(path).Save(desired));
        Assert.Equal(desired, new PreferenceStore(path).Load());
    }

    [Fact]
    public void FailedMigrationRetriesAsOneAtomicWriteAndAFailedLaterSaveKeepsMigratedChoice()
    {
        Directory.CreateDirectory(directory); var path = PathFor("preferences.json");
        const string original = "{\"Appearance\":0,\"TrayIcon\":false}";
        File.WriteAllText(path, original);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(new Preferences(TrayIcon: false), new PreferenceStore(path).Load());
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        var store = new PreferenceStore(path); var migrated = store.Load();
        Assert.Equal(new Preferences(TrayIcon: false), migrated);
        Assert.True(store.Save(migrated with { Appearance = Appearance.Dark }));
        var darkContent = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");
        Assert.False(store.Save(migrated with { Appearance = Appearance.System }));
        Assert.Equal(darkContent, File.ReadAllText(path));
        Assert.Equal(Appearance.Dark, new PreferenceStore(path).Load().Appearance);
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
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void LegacyOrCurrentAppearanceMigratesToLightThenRetainsLaterChoices(int code, bool currentChoice)
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
        Assert.Equal(Appearance.Light, loaded.Appearance); Assert.False(loaded.CompactMonitor);
        Assert.True(new PreferenceStore(currentPath).Save(loaded with { Appearance = (Appearance)code }));
        LegacyPreferences.Migrate(legacy, current);
        Assert.Equal((Appearance)code, new PreferenceStore(currentPath).Load().Appearance);
        Assert.Equal(oldContent, File.ReadAllText(oldPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LegacyMigrationKeepsLightMigrationMarkerWithItsOwningAppearance(bool currentHasMigratedChoice)
    {
        var legacy = PathFor("legacy"); var current = PathFor("current");
        Directory.CreateDirectory(legacy); Directory.CreateDirectory(current);
        Assert.True(new PreferenceStore(Path.Combine(legacy, "v2-preferences.json")).Save(new(Appearance: Appearance.System)));
        var path = Path.Combine(current, "v2-preferences.json");
        if (currentHasMigratedChoice) Assert.True(new PreferenceStore(path).Save(new(Appearance: Appearance.Dark)));
        else File.WriteAllText(path, "{\"Appearance\":2}");
        LegacyPreferences.Migrate(legacy, current);
        Assert.Equal(currentHasMigratedChoice ? Appearance.Dark : Appearance.Light, new PreferenceStore(path).Load().Appearance);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
