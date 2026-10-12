# Privacy

Llumi has no account system, telemetry, analytics, or backend. It reads allowance through the Codex and Claude Code tools you have already installed and authenticated. Those tools may contact their own services under their own settings and terms; Llumi does not send model prompts during usage checks.

Llumi does not copy or store provider credentials. Account continuity is checked in memory so a result cannot silently cross accounts. Full usage snapshots stay in memory. Llumi does not save a timeline of usage readings; optional alerts retain the minimal local state described below to avoid repeat warnings.

Optional macOS usage alerts store settings and a bounded repeat-prevention record in local system preferences. This record contains opaque account/window hashes, reset timestamps and handled threshold ranges, without raw account identities, provider credentials or a history of usage amounts. Disabling alerts retains this record for later re-enabling. macOS controls notification display and history; notifications identify the provider, quota window, reached threshold and available reset information, without account identity. Clicking a notification opens Llumi.

Optional Windows usage alerts create or update a small, protected local record while alerts are enabled. It records handled thresholds, quota-window timing, observation ordering and completed snooze baselines so warnings do not repeat after restarting with the same local data. Account/window keys are pseudonymous and derived using a protected random key; raw identities, account bindings and observed usage amounts are not saved. Windows user protection secures both the key and record in the same per-user application storage as preferences. Snooze saves its end time as a preference, retaining that time after expiry or early resume to prevent a backlog after restarting. Disabling alerts stops new warnings and state updates, retaining the existing record for later re-enabling. This Windows feature adds no telemetry, backend or provider authentication.

Notifications contain the provider, allowance label, reached threshold and reset time; Windows controls their display and notification history. Llumi disables cross-device notification mirroring and requests expiry on reboot. Clicking a notification opens the existing Usage window while Llumi is running. On clean Quit, Llumi requests removal of its owned notifications without clearing unrelated history; activation after the app exits is not guaranteed. Unpackaged Windows builds create or update a separate `Llumi Alerts` Start Menu shortcut when a notification or test is first requested. That shortcut contains the application identity and executable location, without provider or account data. Store builds use their package identity.

On macOS, activity detection uses executable identity, process lifetime, a bounded invocation-mode check, and frontmost/on-screen window metadata. It does not read window titles, terminal contents, keystrokes, screen pixels, prompts, responses, or source repositories. Usage helpers run in isolated temporary working directories, outside user worktrees.

On Windows, activity detection uses executable identity, process lifetime, a bounded invocation-mode check, and foreground/visible/non-minimized window metadata. It does not read window titles, terminal contents, keystrokes, screen pixels, prompts, responses or source repositories. Usage helpers run in isolated temporary working directories outside source worktrees. Llumi sends no model prompts.

On macOS, preferences stay in local system preferences. Small rotating diagnostic logs contain health events, timings, and failure categories rather than raw provider responses or account identity. Mac logs are in `~/Library/Logs/Llumi`. Inspect and sanitize any diagnostic material before sharing it.

The Microsoft Store package runs as an ordinary-user desktop application. Its runFullTrust capability supports the tray, compact monitor and separately installed provider tools; it does not grant administrator access. Launch at Startup is optional and controlled through Windows StartupTask.

Windows Store package preferences, monitor placement, sanitized rotating logs and optional protected alert state stay in the package's per-user LocalState folder. Full usage snapshots remain in memory; provider credentials remain with the providers. Windows manages package data during updates and uninstall. Existing portable Llumi data and provider files are not imported or deleted. Inspect and sanitize diagnostics before sharing.

Unpackaged Windows source builds keep preferences, monitor placement, sanitized rotating logs and optional protected alert state under `%LOCALAPPDATA%\Llumi`. Removing an unpackaged build does not automatically remove those settings, its notification shortcut or provider files. Removing application data also removes its local alert continuity; Llumi has no cloud copy.

Normal Mac operation should not request Documents, Desktop, Downloads, Music/Media Library, Accessibility, Screen Recording, Automation, or Full Disk Access. An unexpected request is a bug: do not grant it just to make Llumi work; report the version and action that triggered it.


Llumi is independent of the supported providers. Their own applications and services retain their separate privacy policies.

Llumi migrates only validated appearance, monitor/menu visibility and setup-completion preferences from former AgentMeter installations. Existing Llumi preferences take priority. Legacy credentials, provider/account data, sessions, logs and login-item registration are not migrated; legacy settings are not deleted.
