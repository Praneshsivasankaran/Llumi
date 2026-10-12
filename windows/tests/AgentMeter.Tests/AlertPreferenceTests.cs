using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class AlertPreferenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Llumi-alert-preferences-fixture-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "preferences.json");

    [Fact]
    public void OlderPreferencesRetainChoicesAndLeaveBackgroundAndAlertsOff()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{\"CompactMonitor\":false,\"TrayIcon\":false,\"Appearance\":2,\"CodexEnabled\":false,\"LightAppearanceMigrated\":true}");
        var value = new PreferenceStore(FilePath).Load();
        Assert.False(value.StartInBackground); Assert.False(value.EffectiveAlerts.Enabled); Assert.Null(value.Alerts);
        Assert.False(value.CompactMonitor); Assert.False(value.TrayIcon); Assert.False(value.CodexEnabled);
        Assert.Equal(Appearance.Dark, value.Appearance);
        Assert.Equal(new[] { 75, 90, 95 }, value.EffectiveAlerts.EffectiveThresholds.Select(t => t.Percent));
    }

    [Fact]
    public void RestartAndUnrelatedEditsKeepAlertConfigurationAndAbsoluteSnoozeInstant()
    {
        var until = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.FromHours(5.5));
        var requested = new Preferences(StartInBackground: true, Alerts: new(true, false, true,
            [new(65), new(88, false), new(99)], until));
        var store = new PreferenceStore(FilePath);
        Assert.True(store.Save(requested));
        Assert.True(store.HasValidExistingPreferences());
        var loaded = store.Load();
        Assert.True(store.Save(loaded with { CompactMonitor = false, Appearance = Appearance.Dark }));
        var restarted = new PreferenceStore(FilePath).Load();
        Assert.True(restarted.StartInBackground); Assert.False(restarted.CompactMonitor); Assert.Equal(Appearance.Dark, restarted.Appearance);
        Assert.True(restarted.EffectiveAlerts.Enabled); Assert.False(restarted.EffectiveAlerts.CodexEnabled);
        Assert.Equal(until, restarted.EffectiveAlerts.SnoozedUntil);
        Assert.Equal(requested.EffectiveAlerts.EffectiveThresholds, restarted.EffectiveAlerts.EffectiveThresholds);
        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.False(json.RootElement.TryGetProperty("EffectiveAlerts", out _));
        var alerts = json.RootElement.GetProperty("Alerts");
        Assert.False(alerts.TryGetProperty("EffectiveThresholds", out _)); Assert.False(alerts.TryGetProperty("IsValid", out _));
    }

    [Theory]
    [InlineData("{\"Enabled\":true,\"Enabled\":false}")]
    [InlineData("{\"Enabled\":\"true\"}")]
    [InlineData("{\"CodexEnabled\":null}")]
    [InlineData("{\"SnoozedUntil\":\"tomorrow\"}")]
    [InlineData("{\"Unknown\":true}")]
    [InlineData("{\"Thresholds\":[]}")]
    [InlineData("{\"Thresholds\":[null]}")]
    [InlineData("{\"Thresholds\":[{\"Percent\":90},{\"Percent\":90,\"Enabled\":false}]}")]
    [InlineData("{\"Thresholds\":[{\"Percent\":90,\"Percent\":95}]}")]
    [InlineData("{\"Thresholds\":[{\"Percent\":75.5}]}")]
    [InlineData("{\"Thresholds\":[{\"Percent\":0}]}")]
    [InlineData("{\"Thresholds\":[{\"Percent\":101}]}")]
    [InlineData("{\"Thresholds\":[{\"Enabled\":true}]}")]
    public void InvalidAlertSettingsNeverOptInAndNeverRewriteInvalidInput(string alerts)
    {
        Directory.CreateDirectory(directory); var content = "{\"StartInBackground\":true,\"Alerts\":" + alerts + "}";
        File.WriteAllText(FilePath, content); var store = new PreferenceStore(FilePath);
        Assert.False(store.HasValidExistingPreferences()); Assert.False(store.Load().EffectiveAlerts.Enabled);
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    [Fact]
    public void InvalidSaveAndOversizedThresholdArrayPreserveLastGoodPreferences()
    {
        var store = new PreferenceStore(FilePath); Assert.True(store.Save(new(StartInBackground: true)));
        var original = File.ReadAllText(FilePath);
        Assert.False(store.Save(new(Alerts: new(true, Thresholds: [new(90), new(90, false)]))));
        Assert.Equal(original, File.ReadAllText(FilePath));
        var many = string.Join(',', Enumerable.Range(1, 17).Select(n => "{\"Percent\":" + n + "}"));
        File.WriteAllText(FilePath, "{\"Alerts\":{\"Enabled\":true,\"Thresholds\":[" + many + "]}}");
        Assert.False(store.HasValidExistingPreferences()); Assert.False(store.Load().EffectiveAlerts.Enabled);
    }

    [Fact]
    public void LegacyMigrationPreservesNewCurrentPreferences()
    {
        var legacy = Path.Combine(directory, "legacy"); var current = Path.Combine(directory, "current");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "v2-preferences.json"), "{\"CompactMonitor\":false}");
        var path = Path.Combine(current, "v2-preferences.json");
        var alerts = new UsageAlertSettings(true, Thresholds: [new(60), new(80), new(95)]);
        Assert.True(new PreferenceStore(path).Save(new(StartInBackground: true, Alerts: alerts)));
        LegacyPreferences.Migrate(legacy, current);
        var value = new PreferenceStore(path).Load();
        Assert.True(value.StartInBackground); Assert.True(value.EffectiveAlerts.Enabled);
        Assert.Equal(alerts.EffectiveThresholds, value.EffectiveAlerts.EffectiveThresholds);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
