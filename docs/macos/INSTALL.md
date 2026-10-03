# Install Llumi for macOS

Download the signed, notarized and stapled **Llumi 1.1.2** DMG from [tryllumi.com](https://tryllumi.com/) or its [GitHub release](https://github.com/Praneshsivasankaran/Llumi/releases/tag/llumi-macos-1.1.2). Requires macOS 14 or later, Apple Silicon or Intel. Verify the download against the original SHA-256 sidecar before opening it.

Open the DMG, drag `Llumi.app` into Applications, eject the DMG and launch Llumi from Applications. Keep Gatekeeper enabled. Report unexpected security failures rather than bypassing them. When replacing AgentMeter, disable its Launch at Login setting and quit it normally first; enable startup in Llumi only after installation. See [migration details](../LLUMI-MIGRATION.md).

## Provider setup

Use Llumi's guided setup or Settings → Check Setup. Install and sign in to Codex CLI and/or standalone Claude Code through their own tools. Llumi copies setup commands but never executes them or accepts provider credentials. A desktop app alone is not a verified CLI allowance source.

## Daily use

The menu-bar item or Dock icon opens the usage window. Closing the window leaves Llumi running; Quit stops the app. The notch monitor follows supported coding activity; hover shows details and click opens the main window. Other displays use a top-edge fallback. Settings control appearance, monitoring and launch at login.

Unknown or stale usage remains explicitly unavailable. Check provider authentication and refresh rather than treating an unknown value as exhausted allowance.

## Help

[Support](https://tryllumi.com/support/) · [Report a bug](https://github.com/Praneshsivasankaran/Llumi/issues/new/choose). Include platform, version, display arrangement and reproduction steps. Inspect and sanitize diagnostics from `~/Library/Logs/Llumi`; never share credentials, raw provider output or private conversations.
