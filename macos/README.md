# Llumi for macOS

Native Swift, SwiftUI, and AppKit application. The usage window, menu-bar item, and contextual notch monitor share one usage store and one activity monitor.

[Build and test](../docs/macos/BUILD.md) · [Install and provider setup](../docs/macos/INSTALL.md) · [Privacy](../PRIVACY.md)

Usage refresh is coalesced per provider every 30 seconds. Activity uses 500 ms native reconciliation and native process-exit events. Desktop activity means frontmost with a visible normal window; interactive CLI sessions remain active until they exit. Unknown invocation modes fail closed.

Provider helpers use an explicit isolated temporary directory and Git-discovery ceiling. Do not replace this with the app's inherited working directory: it can expose user worktrees and trigger unrelated macOS privacy requests.

The published Llumi 1.1.1 release uses permanent bundle identifier `io.github.praneshsivasankaran.llumi` and is Developer ID signed, notarized and stapled. See the [frozen baseline](../docs/releases/2026-09-launch.md). App Sandbox is intentionally disabled. The Release target contains no development command channel; Debug-only acceptance hooks are excluded at compilation. Beta preference migration is allowlisted and runs once; Launch at Login must be disabled in the old beta before replacing it and re-enabled in the installed production app after installation.
