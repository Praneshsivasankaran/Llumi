# Settings and lifecycle

Windows 2.1.0 locally adopts the macOS 1.1.3 product contract in [Windows catch-up requirements](windows-210.md), which supersedes conflicting earlier Windows allowance, setup and provider-selection rules below. Intentional native differences remain.

Windows Settings omits the tray-availability explanation, independence/account/telemetry note, automatic-save footer and passive diagnostics privacy note. Copy Diagnostics shows success or clipboard-error feedback after use. Persistence and monitor-position errors remain visible when they occur. macOS copy remains unchanged.

Keep settings limited to launch at login/startup, compact monitor, background menu/tray icon and Appearance: System, Light, Dark. Preferences persist without credentials, account data or usage history. Show authoritative startup registration state; failed persistence must not claim success.

Normal launch opens Usage. Login/startup launch stays quiet when the background control surface is enabled. Close continues monitoring; Open restores/focuses one window. If the background icon is hidden, retain a discoverable native application entry so closing cannot strand the app. Quit cancels refreshes, removes background and compact surfaces, stops activity monitoring and terminates owned helpers.

Background menu: Open Llumi, Refresh, Settings, Quit. No allowance dashboard in that menu. No refresh sliders, colors, opacity controls, animation-speed controls, history, notifications, account management or API keys.

macOS 1.0.0 candidate: one process per user owns the background services, including across copies of the app. A nonblocking kernel lock is acquired before services or diagnostics initialize. A second launch may request that the existing window open, but starts no services. The lock lasts through shutdown and is released by process exit; it is not a PID file and is never deleted on exit. Windows behavior is unchanged by this Mac remediation.

The new macOS preference domain imports only validated `notchEnabled`, `menuEnabled` and `appearance` values from the beta once. Existing new-domain values win. Login registration is never copied; ServiceManagement remains authoritative. The normal Dock entry remains available with both optional surfaces disabled.

macOS 1.1.1 adds first-launch setup: Welcome → provider choice → selected provider instructions → Verify → existing preferences → Done. Either provider, both, or neither may be configured. Check Again uses the existing coalesced usage refresh; Ready requires a live verified reading. Commands are copied only, never executed by Llumi. Help → Setup Llumi… reopens setup. A single completion boolean persists; validated existing beta/current preferences exempt existing users from automatic setup. A bare migration-attempt marker does not prove an existing installation. Windows now implements the equivalent workflow described below.

One Appearance preference drives every Mac surface, including compact and expanded notch material, text and marks. System inherits macOS; Light/Dark override live without changing notch geometry or interaction. The About screen retains version and “AI coding allowance, quietly at a glance.” only beneath the product identity.

Windows setup now follows the same Welcome → provider selection → selected provider instructions → Verify → shared preferences → Done workflow. A bounded, type-validated completion flag persists separately; valid existing Windows preferences exempt existing users only before that flag exists. Setup remains manually accessible. Commands are copy-only and Windows-specific; startup uses the existing authoritative registration.

Both platforms expose Check Setup and explicit local Copy Diagnostics. Readiness comes from existing refresh state; unknown authentication remains unknown. Diagnostic schema 1 allows only app numeric version/build, OS numeric version, architecture and fixed per-provider detected/authentication/usage/failure/last-result categories. Never export raw logs, provider strings, identities, payloads, paths, environment, or commands. No network/upload or extra provider polling subsystem.

Copy-only Windows setup commands (official sources checked 2026-09-23): `npm install -g @openai/codex` (requires Node.js/npm, native Windows installation) then `codex login`; `irm https://claude.ai/install.ps1 | iex` then `claude auth login`. Sources: [OpenAI CLI installation](https://github.com/openai/codex#installing-and-running-codex), [Claude Windows setup](https://code.claude.com/docs/en/setup), [Claude authentication command](https://code.claude.com/docs/en/cli-reference). macOS installation instructions remain platform-specific. Llumi displays/copies instructions and opens fixed official documentation URLs only; it does not run an installer or login shell.

On both platforms, provider checks are embedded in Settings, with installation, authentication/readiness and usage states, Check Again and Copy Diagnostics. Windows uses a native scrollable Provider Setup section; it does not open a separate diagnostics dialog. The first-launch guided setup retains its verification step. Existing macOS presentation is unchanged.

Windows uses one effective Appearance across the main window, onboarding and compact/expanded monitor. Explicit Light/Dark and System changes redraw existing surfaces live without changing monitor geometry, activity or interactions.
