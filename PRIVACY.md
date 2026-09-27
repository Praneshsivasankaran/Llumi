# Privacy

Llumi has no account system, telemetry, analytics, or backend. It reads allowance through the Codex and Claude Code tools you have already installed and authenticated. Those tools may contact their own services under their own settings and terms; Llumi does not send model prompts during usage checks.

Llumi does not copy or store provider credentials. Account continuity is checked in memory so a result cannot silently cross accounts. Usage snapshots stay in memory, and no usage history is saved.

On macOS, activity detection uses executable identity, process lifetime, a bounded invocation-mode check, and frontmost/on-screen window metadata. It does not read window titles, terminal contents, keystrokes, screen pixels, prompts, responses, or source repositories. Usage helpers run in isolated temporary working directories, outside user worktrees.

On Windows, activity detection uses executable identity, process lifetime, a bounded invocation-mode check, and foreground/visible/non-minimized window metadata. It does not read window titles, terminal contents, keystrokes, screen pixels, prompts, responses or source repositories. Usage helpers run in isolated temporary working directories outside source worktrees. Llumi sends no model prompts.

On macOS, preferences stay in local system preferences. Small rotating diagnostic logs contain health events, timings, and failure categories rather than raw provider responses or account identity. Mac logs are in `~/Library/Logs/Llumi`. Inspect and sanitize any diagnostic material before sharing it.

The Microsoft Store package runs as an ordinary-user desktop application. Its runFullTrust capability supports the tray, compact monitor and separately installed provider tools; it does not grant administrator access. Launch at Startup is optional and controlled through Windows StartupTask.

Windows Store package preferences, monitor placement and sanitized rotating logs stay in the package's per-user LocalState folder. Usage snapshots remain in memory; provider credentials remain with the providers. Windows manages package data during updates and uninstall. Existing portable Llumi data and provider files are not imported or deleted. Inspect and sanitize diagnostics before sharing.

Unpackaged Windows source builds keep preferences, monitor placement and sanitized rotating logs under `%LOCALAPPDATA%\Llumi`. Removing an unpackaged build does not automatically remove those settings or provider files.

Normal Mac operation should not request Documents, Desktop, Downloads, Music/Media Library, Accessibility, Screen Recording, Automation, or Full Disk Access. An unexpected request is a bug: do not grant it just to make Llumi work; report the version and action that triggered it.


Llumi is independent of the supported providers. Their own applications and services retain their separate privacy policies.

Llumi migrates only validated appearance, monitor/menu visibility and setup-completion preferences from former AgentMeter installations. Existing Llumi preferences take priority. Legacy credentials, provider/account data, sessions, logs and login-item registration are not migrated; legacy settings are not deleted.
