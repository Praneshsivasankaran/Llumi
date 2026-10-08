using System.ComponentModel;
using System.Diagnostics;
using AgentMeter.Core;

namespace AgentMeter;

internal static class ProviderSetup
{
    // Fixed official destinations only; no provider-provided URL or credentials reach the shell.
    internal static Uri? For(ProviderState state) => state.Enabled && state.Snapshot is null &&
        state.Failure is FailureKind.NotInstalled or FailureKind.LoggedOut
        ? state.Name switch
        {
            "Codex" => new("https://learn.chatgpt.com/docs/codex/cli"),
            "Claude" or "Claude Code" => new("https://code.claude.com/docs/en/quickstart"),
            _ => null
        } : null;

    internal static bool Open(Uri destination)
    {
        if (destination.Scheme != Uri.UriSchemeHttps || destination.Host is not ("developers.openai.com" or "learn.chatgpt.com" or "code.claude.com"))
            return false;
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(destination.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        { return false; }
    }
}
