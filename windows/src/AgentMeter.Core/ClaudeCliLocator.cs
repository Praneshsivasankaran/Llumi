using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentMeter.Core;

public sealed record ClaudeCliSearchEnvironment(string? PathValue, string UserProfile,
    string LocalApplicationData, string RoamingApplicationData, string? ClaudeOverride = null)
{
    public static ClaudeCliSearchEnvironment Current() => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("LLUMI_CLAUDE_PATH")
            ?? Environment.GetEnvironmentVariable("AGENTMETER_CLAUDE_PATH"));
}

public enum ClaudeCliOrigin { Standalone, DesktopManaged, VsCodeManaged }
public sealed record ClaudeCliCandidate(string Path, ClaudeCliOrigin Origin);

public static class ClaudeCliLocator
{
    private const int MaximumDirectories = 128;

    public static string? Find() => Find(ClaudeCliSearchEnvironment.Current());

    public static string? Find(ClaudeCliSearchEnvironment environment) =>
        Candidates(environment, includeManagedCaches: false).FirstOrDefault(candidate => candidate.Origin == ClaudeCliOrigin.Standalone)?.Path;

    public static IReadOnlyList<string> StandaloneCandidates() => StandaloneCandidates(ClaudeCliSearchEnvironment.Current());
    public static IReadOnlyList<string> StandaloneCandidates(ClaudeCliSearchEnvironment environment) =>
        Candidates(environment, includeManagedCaches: false).Where(candidate => candidate.Origin == ClaudeCliOrigin.Standalone)
            .Select(candidate => candidate.Path).Distinct(StringComparer.OrdinalIgnoreCase).Take(CliLocator.MaximumCandidates).ToArray();

    // Diagnostic discovery preserves managed installation evidence without treating
    // Desktop/extension binaries as the user's independently installed CLI.
    public static IReadOnlyList<ClaudeCliCandidate> Discover() => Discover(ClaudeCliSearchEnvironment.Current());

    public static IReadOnlyList<ClaudeCliCandidate> Discover(ClaudeCliSearchEnvironment environment) =>
        Candidates(environment, includeManagedCaches: true)
            .DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<ClaudeCliCandidate> Candidates(ClaudeCliSearchEnvironment environment, bool includeManagedCaches)
    {
        var physicalRoots = new Lazy<IReadOnlyList<(string? Path, ClaudeCliOrigin Origin)>>(() => ManagedPhysicalRoots(environment));
        if (environment.ClaudeOverride is not null)
        {
            var configured = CliLocator.ExistingExecutable(environment.ClaudeOverride);
            if (configured is not null && Classify(configured, environment, physicalRoots) is { } candidate) yield return candidate;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in (environment.PathValue ?? "").Split(Path.PathSeparator))
        {
            var onPath = CliLocator.FindOnPath("claude.exe", directory);
            if (onPath is not null && Classify(onPath, environment, physicalRoots) is { } candidate && seen.Add(candidate.Path)) yield return candidate;
        }

        var native = Candidate(environment.UserProfile, ".local", "bin", "claude.exe");
        if (native is not null && Classify(native, environment, physicalRoots) is { } nativeCandidate && seen.Add(nativeCandidate.Path)) yield return nativeCandidate;
        if (!includeManagedCaches) yield break;

        var desktopVersions = VersionedExecutables(CombineRoot(environment.RoamingApplicationData, "Claude", "claude-code"));
        foreach (var package in Directories(CombineRoot(environment.LocalApplicationData, "Packages"), "Claude_*"))
            desktopVersions.AddRange(VersionedExecutables(Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code")));
        foreach (var desktop in desktopVersions.OrderByDescending(x => x.Version).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            if (Classify(desktop.Path, environment, physicalRoots) is { } candidate && seen.Add(candidate.Path)) yield return candidate;

        var extensions = new[] { ".vscode", ".vscode-insiders" }.SelectMany(folder =>
            Directories(CombineRoot(environment.UserProfile, folder, "extensions"), "anthropic.claude-code-*"));
        foreach (var extension in extensions.Select(directory => (Directory: directory,
                Version: ParseVersion(Path.GetFileName(directory)["anthropic.claude-code-".Length..])))
            .Where(x => x.Version is not null).OrderByDescending(x => x.Version)
            .ThenBy(x => x.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(x => Candidate(x.Directory, "resources", "native-binary", "claude.exe"))
            .Where(path => path is not null))
            if (Classify(extension!, environment, physicalRoots) is { } candidate && seen.Add(candidate.Path)) yield return candidate;
    }

    private static ClaudeCliCandidate? Classify(string path, ClaudeCliSearchEnvironment environment,
        Lazy<IReadOnlyList<(string? Path, ClaudeCliOrigin Origin)>> physicalRoots)
    {
        var physical = PhysicalPath(path);
        if (physical is null) return null;
        var origin = Origin(path, environment);
        if (origin == ClaudeCliOrigin.Standalone) origin = Origin(physical, environment);
        if (origin == ClaudeCliOrigin.Standalone)
        {
            foreach (var root in physicalRoots.Value)
            {
                if (root.Path is null) return null;
                if (RelativeParts(physical, root.Path) is not { Length: > 0 }) continue;
                origin = root.Origin;
                break;
            }
        }
        return new(physical, origin);
    }

    private static IReadOnlyList<(string? Path, ClaudeCliOrigin Origin)> ManagedPhysicalRoots(ClaudeCliSearchEnvironment environment)
    {
        var roots = new List<(string? Path, ClaudeCliOrigin Origin)>();
        void Add(string? root, ClaudeCliOrigin origin)
        {
            if (root is not null && Directory.Exists(root)) roots.Add((PhysicalPath(root), origin));
        }
        Add(CombineRoot(environment.RoamingApplicationData, "Claude", "claude-code"), ClaudeCliOrigin.DesktopManaged);
        foreach (var package in Directories(CombineRoot(environment.LocalApplicationData, "Packages"), "Claude_*"))
            Add(Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code"), ClaudeCliOrigin.DesktopManaged);
        foreach (var folder in new[] { ".vscode", ".vscode-insiders" })
            foreach (var extension in Directories(CombineRoot(environment.UserProfile, folder, "extensions"), "anthropic.claude-code-*"))
                Add(extension, ClaudeCliOrigin.VsCodeManaged);
        return roots;
    }

    private static string? PhysicalPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        // Desired access zero opens only metadata; BACKUP_SEMANTICS also permits
        // directory handles. Following the handle resolves ancestor junctions,
        // symbolic links and short/extended path aliases without reading contents.
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity && length < 32768)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity) return null;
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) result = @"\\" + result[8..];
        else if (result.StartsWith(@"\\?\", StringComparison.Ordinal) && result.Length > 6 && result[5] == ':') result = result[4..];
        else return null;
        return Path.IsPathFullyQualified(result) ? result : null;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    private static ClaudeCliOrigin Origin(string path, ClaudeCliSearchEnvironment environment)
    {
        if (RelativeParts(path, CombineRoot(environment.RoamingApplicationData, "Claude", "claude-code")) is { Length: > 0 })
            return ClaudeCliOrigin.DesktopManaged;

        var package = RelativeParts(path, CombineRoot(environment.LocalApplicationData, "Packages"));
        if (package is { Length: >= 6 } && package[0].StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) &&
            Equal(package[1], "LocalCache") && Equal(package[2], "Roaming") &&
            Equal(package[3], "Claude") && Equal(package[4], "claude-code"))
            return ClaudeCliOrigin.DesktopManaged;

        foreach (var folder in new[] { ".vscode", ".vscode-insiders" })
        {
            var extension = RelativeParts(path, CombineRoot(environment.UserProfile, folder, "extensions"));
            if (extension is { Length: >= 2 } && extension[0].StartsWith("anthropic.claude-code-", StringComparison.OrdinalIgnoreCase))
                return ClaudeCliOrigin.VsCodeManaged;
        }

        // VS Code permits a custom extensions directory. Inspect only the given
        // executable's known extension layout; do not scan arbitrary directories.
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && parts[^4].StartsWith("anthropic.claude-code-", StringComparison.OrdinalIgnoreCase) &&
            Equal(parts[^3], "resources") && Equal(parts[^2], "native-binary"))
            return ClaudeCliOrigin.VsCodeManaged;
        return ClaudeCliOrigin.Standalone;
    }

    private static string[]? RelativeParts(string path, string? root)
    {
        if (root is null) return null;
        try
        {
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;
            return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
    }

    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static List<(Version Version, string Path)> VersionedExecutables(string? root)
    {
        var result = new List<(Version, string)>();
        foreach (var directory in Directories(root))
        {
            var version = ParseVersion(Path.GetFileName(directory));
            var executable = Candidate(directory, "claude.exe");
            if (version is not null && executable is not null) result.Add((version, executable));
        }
        return result;
    }

    private static Version? ParseVersion(string value) =>
        Version.TryParse(value.Split('-')[0], out var version) ? version : null;

    private static string? CombineRoot(string root, params string[] parts)
    {
        try { return Path.IsPathFullyQualified(root) ? Path.Combine([root, .. parts]) : null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static string? Candidate(string root, params string[] parts)
    {
        var path = CombineRoot(root, parts);
        return path is null ? null : CliLocator.ExistingExecutable(path);
    }

    private static string[] Directories(string? root, string pattern = "*")
    {
        try
        {
            if (root is null || !Directory.Exists(root)) return [];
            // Search only known installation levels; never recurse through user data.
            var directories = Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly)
                .Take(MaximumDirectories + 1).ToArray();
            return directories.Length <= MaximumDirectories ? directories : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
