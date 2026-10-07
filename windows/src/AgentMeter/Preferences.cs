using System.Text.Json;

namespace AgentMeter;

internal enum Appearance { System, Light, Dark }
internal sealed record Preferences(bool CompactMonitor = true, bool TrayIcon = true, Appearance Appearance = Appearance.Light,
    bool CodexEnabled = true, bool ClaudeEnabled = true);
internal sealed class PreferenceStore(string path)
{
    internal const string LightMigrationKey = "LightAppearanceMigrated";
    internal static PreferenceStore Default() => new(Path.Combine(PackagedEnvironment.DataDirectory, "v2-preferences.json"));
    internal bool HasValidExistingPreferences()
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 4096) return false;
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!ValidRoot(root)) return false;
            return root.EnumerateObject().Any(p =>
                (p.Name is "CompactMonitor" or "TrayIcon" or "CodexEnabled" or "ClaudeEnabled" && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                || (p.Name == "Appearance" && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n) && Enum.IsDefined((Appearance)n)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }
    internal Preferences Load()
    {
        try
        {
            Preferences value;
            bool migrated;
            using (var file = File.OpenRead(path))
            {
                if (file.Length > 4096) return new();
                using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 4 });
                var root = document.RootElement;
                if (!ValidRoot(root) || !HasPreference(root)) return new();
                // Missing provider flags in older preferences preserve existing monitoring.
                value = root.Deserialize<Preferences>() ?? new();
                migrated = root.TryGetProperty(LightMigrationKey, out var marker) && marker.GetBoolean();
            }
            if (migrated) return value;
            value = value with { Appearance = Appearance.Light };
            // Close the source handle before the atomic replacement on Windows.
            // A failed write leaves both the old choice and migration marker intact;
            // this session uses Light and a later load can retry the migration.
            Save(value);
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private static bool ValidRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>();
        if (root.EnumerateObject().Any(p => !names.Add(p.Name))) return false;
        foreach (var key in new[] { "CompactMonitor", "TrayIcon", "CodexEnabled", "ClaudeEnabled", LightMigrationKey })
            if (root.TryGetProperty(key, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        return !root.TryGetProperty("Appearance", out var appearance) ||
            appearance.ValueKind == JsonValueKind.Number && appearance.TryGetInt32(out var number) && Enum.IsDefined((Appearance)number);
    }
    private static bool HasPreference(JsonElement root) => root.EnumerateObject().Any(p =>
        p.Name is "CompactMonitor" or "TrayIcon" or "CodexEnabled" or "ClaudeEnabled" or "Appearance");
    internal bool Save(Preferences value)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = File.Create(temporary))
            {
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    foreach (var property in JsonSerializer.SerializeToElement(value).EnumerateObject()) property.WriteTo(writer);
                    // An explicit choice and completion of the one-time reset must
                    // succeed together, including the first save of a fresh profile.
                    writer.WriteBoolean(LightMigrationKey, true);
                    writer.WriteEndObject();
                }
                output.Flush(true);
            }
            File.Move(temporary, path, true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

// Portable-only, bounded migration. Packaged LocalState remains OS-owned and unchanged.
internal static class LegacyPreferences
{
    internal static void Migrate(string legacyDirectory, string currentDirectory)
    {
        var marker = Path.Combine(currentDirectory, "llumi-migration.json");
        try
        {
            if (File.Exists(marker)) return;
            var old = ReadObject(Path.Combine(legacyDirectory, "v2-preferences.json"));
            var current = ReadObject(Path.Combine(currentDirectory, "v2-preferences.json"));
            var values = new Dictionary<string, object>();
            foreach (var key in new[] { "CompactMonitor", "TrayIcon", "Appearance", "CodexEnabled", "ClaudeEnabled" })
            {
                var value = Value(current, key) ?? Value(old, key);
                if (value is not null) values[key] = value;
            }
            // Completion belongs to the appearance's source, never to a different
            // preference domain whose old marker could skip the reset accidentally.
            var appearanceSource = Value(current, "Appearance") is not null ? current : old;
            if (values.ContainsKey("Appearance") && Value(appearanceSource, PreferenceStore.LightMigrationKey) is true)
                values[PreferenceStore.LightMigrationKey] = true;
            Directory.CreateDirectory(currentDirectory);
            if (values.Count > 0) Write(Path.Combine(currentDirectory, "v2-preferences.json"), JsonSerializer.Serialize(values));
            var completed = ReadBoolean(Path.Combine(currentDirectory, "setup-completed.json"))
                ?? ReadBoolean(Path.Combine(legacyDirectory, "setup-completed.json"));
            if (completed is null && values.Count > 0) completed = true;
            if (completed is not null) Write(Path.Combine(currentDirectory, "setup-completed.json"), completed.Value ? "true" : "false");
            Write(marker, "true");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private static JsonElement? ReadObject(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 4096) return null;
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>();
            foreach (var entry in document.RootElement.EnumerateObject()) if (!names.Add(entry.Name)) return null;
            return document.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static object? Value(JsonElement? root, string key)
    {
        if (root is not { } obj || !obj.TryGetProperty(key, out var value)) return null;
        if (key == "Appearance") return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
            && Enum.IsDefined((Appearance)n) ? n : null;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }
    private static bool? ReadBoolean(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 32) return null;
            using var document = JsonDocument.Parse(file);
            return document.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False ? document.RootElement.GetBoolean() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static void Write(string path, string value)
    {
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, value); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
