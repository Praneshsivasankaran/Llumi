using System.Runtime.InteropServices;

namespace AgentMeter.Core;

public sealed record CliSearchEnvironment(
    string? PathValue,
    string LocalApplicationData,
    string RoamingApplicationData,
    Architecture Architecture,
    string? CodexOverride = null)
{
    public static CliSearchEnvironment Current() => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        RuntimeInformation.OSArchitecture,
        Environment.GetEnvironmentVariable("LLUMI_CODEX_PATH")
            ?? Environment.GetEnvironmentVariable("AGENTMETER_CODEX_PATH"));
}

public static class CliLocator
{
    internal const int MaximumCandidates = 8;
    public static string? FindCodex() => FindCodex(CliSearchEnvironment.Current());

    public static string? FindCodex(CliSearchEnvironment environment) => EnumerateCodex(environment).FirstOrDefault();

    public static IReadOnlyList<string> CodexCandidates() => CodexCandidates(CliSearchEnvironment.Current());
    public static IReadOnlyList<string> CodexCandidates(CliSearchEnvironment environment) => EnumerateCodex(environment)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumCandidates).ToArray();

    private static IEnumerable<string> EnumerateCodex(CliSearchEnvironment environment)
    {
        // An explicit override is authoritative, including an invalid path.
        if (environment.CodexOverride is not null)
        {
            if (ExistingExecutable(environment.CodexOverride) is { } configured) yield return configured;
            yield break;
        }
        var pathDirectories = PathDirectories(environment.PathValue).ToArray();
        foreach (var directory in pathDirectories)
            if (ExistingExecutable(Path.Combine(directory, "codex.exe")) is { } onPath) yield return onPath;

        // npm's Windows shim is a .cmd. Resolve its native binary, never invoke a shell.
        foreach (var prefix in pathDirectories)
            foreach (var npmExecutable in NpmCodexCandidates(prefix, environment.Architecture)) yield return npmExecutable;

        if (Path.IsPathFullyQualified(environment.LocalApplicationData))
        {
            var versions = Path.Combine(environment.LocalApplicationData, "OpenAI", "Codex", "bin");
            string[] installed = [];
            try
            {
                if (Directory.Exists(versions))
                {
                    var directories = new DirectoryInfo(versions).EnumerateDirectories().Take(129).ToArray();
                    if (directories.Length <= 128) installed = directories.OrderByDescending(d => d.LastWriteTimeUtc)
                        .ThenBy(d => d.Name, StringComparer.Ordinal).Select(d => Path.Combine(d.FullName, "codex.exe")).ToArray();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var path in installed)
                if (ExistingExecutable(path) is { } executable) yield return executable;
        }

        if (Path.IsPathFullyQualified(environment.RoamingApplicationData))
            foreach (var roaming in NpmCodexCandidates(Path.Combine(environment.RoamingApplicationData, "npm"), environment.Architecture))
                yield return roaming;
    }

    internal static bool CanTryNextAfterLaunchFailure(System.ComponentModel.Win32Exception error) =>
        error.NativeErrorCode is 2 or 3 or 193 or 216; // File/path missing, bad executable image, machine mismatch.

    private static IEnumerable<string> NpmCodexCandidates(string prefix, Architecture architecture)
    {
        var (packageName, target) = architecture switch
        {
            Architecture.Arm64 => ("codex-win32-arm64", "aarch64-pc-windows-msvc"),
            Architecture.X64 => ("codex-win32-x64", "x86_64-pc-windows-msvc"),
            _ => ("", "")
        };
        if (target.Length == 0) yield break;
        var openai = Path.Combine(prefix, "node_modules", "@openai");
        foreach (var package in new[] { "codex", packageName })
        {
            var relative = Path.Combine(package, "vendor", target, "codex", "codex.exe");
            var executable = ExistingExecutable(Path.Combine(openai, relative));
            if (executable is not null) yield return executable;
            executable = ExistingExecutable(Path.Combine(openai, "codex", "node_modules", "@openai", relative));
            if (executable is not null) yield return executable;
        }
    }

    public static string? FindOnPath(string executableName) =>
        FindOnPath(executableName, Environment.GetEnvironmentVariable("PATH"));

    public static string? FindOnPath(string executableName, string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(executableName) || Path.GetFileName(executableName) != executableName) return null;
        return FindInDirectories(executableName, PathDirectories(pathValue));
    }

    private static string? FindInDirectories(string executableName, IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            var executable = ExistingExecutable(Path.Combine(directory, executableName));
            if (executable is not null) return executable;
        }
        return null;
    }

    private static IEnumerable<string> PathDirectories(string? pathValue)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (pathValue ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            string? directory = null;
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                // Relative PATH entries must never make discovery depend on the working directory.
                if (Path.IsPathFullyQualified(expanded)) directory = Path.GetFullPath(expanded);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (IOException) { }
            if (directory is not null && seen.Add(directory)) yield return directory;
        }
    }

    public static string? ExistingExecutable(string path)
    {
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                ? Path.GetFullPath(path) : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
