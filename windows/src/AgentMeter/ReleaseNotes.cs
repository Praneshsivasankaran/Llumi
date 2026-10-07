using System.Text.Json;

namespace AgentMeter;

internal sealed record ReleaseSection(string Title, string[] Items);
internal sealed record ReleaseNotes(string Version, bool Preview, string Summary, ReleaseSection[] Sections)
{
    internal static string AppVersion => typeof(ReleaseNotes).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
    internal static ReleaseNotes? Load()
    {
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("AgentMeter.ReleaseNotes.json");
        if (stream is null) return null;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        // Bundled, reviewed content only. Never derive a URL or success notice from it.
        if (root.GetProperty("platform").GetString() != "windows" || root.GetProperty("version").GetString() != AppVersion) return null;
        var sections = root.GetProperty("sections").EnumerateArray().Select(section => new ReleaseSection(
            section.GetProperty("title").GetString()!, section.GetProperty("items").EnumerateArray().Select(item => item.GetString()!).ToArray())).ToArray();
        return new(AppVersion, root.GetProperty("status").GetString() == "preview", root.GetProperty("summary").GetString()!, sections);
    }
}
