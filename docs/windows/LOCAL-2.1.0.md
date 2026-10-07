# Windows 2.1.0 local review

Local catch-up implementation based on the macOS 1.1.3 allowance/setup requirements. Reference behavior is taken from the released tag, not from the different platform version numbers. This supersedes the earlier Windows 2.0.3 restriction to general Claude windows.

## Changes

- Preserve supported general, model and additional time windows. Choose compact general five-hour, weekly, then shortest valid duration; additional/model limits cannot replace it. Details use duration/scope labels and preserve verified zero/exhausted values.
- Distinguish Not reported, Unsupported format, Unsupported billing, Unavailable and Stale, with current sign-in evidence separate from allowance readiness. Fresh empty responses clear old observations. Stale values require current proof of the same account; account/logout/verification failures clear previous data.
- Add shared per-provider switches in Settings/setup. Persist old preferences with both providers enabled by default; off stops queries/discovery/activity, cancels owned work, fences delayed results and removes cards/monitor entries. Both off has a Settings action.
- Add single-flight/manual ten-second minimum, exponential background backoff capped at fifteen minutes, reset refresh cooldown and a separate monotonic rate embargo that survives toggles/sleep/wake. Retry can recheck during generic backoff; checking/countdown indicates when it can actually run.
- Simplify welcome/setup copy, keep Windows copy-only PowerShell commands and authoritative startup handling. Show sign-in and monitoring separately.
- Equal Usage card sizing, consistent official Codex blossom/Claude Code pixel artwork, all supported expanded allowance details, stale age/accessibility text, keyboard access to Usage, and Reset Position in Settings/monitor menu.
- Remove the Settings tray-availability explanation, independence/account/telemetry note and automatic-save footer; preserve error messages and collapse the unused message area.
- Remove the passive diagnostics privacy note and its empty layout space. Copy Diagnostics retains concise success/clipboard-error feedback after use.
- Update local app version to 2.1.0. Microsoft Store remains the Windows update channel. No Sparkle, installer, public release, website change or Store metadata change.

## Local review

Build the production app with `pwsh -NoProfile -File windows/tools/build.ps1`. The framework-dependent output is `windows/artifacts/release/Llumi.exe`; it needs the .NET 10 Desktop Runtime. The script creates local files only.

An additional development-only `AgentMeter.ReviewHost` project reuses the native application forms with fixed synthetic examples. It runs no provider collectors/activity scanners, writes no production preferences/startup settings and does not acquire the product single-instance locks. Use it to inspect both-window/model, weekly-only, missing/unsupported, stale, provider-off, themes and setup states while preserving the installed app. It is excluded from production packaging. Capture output is fixture rendering, not real-account or physical acceptance.

## Validation boundaries

The local Release build passed with zero warnings/errors and 717 tests passed, zero failed/skipped. Release metadata/dependency, runtime notices (10), version guard (17), dependency rejection (7), inventory (7), Windows production privacy, public-tree hygiene and macOS privacy checks passed. The separate synthetic review host also built successfully and produced native fixture previews.

The subsequent Settings copy removals passed 98 relevant tests with zero failures/skips, including short/narrow-window reachability and sanitized diagnostics checks, plus Windows production privacy and public-tree checks. Both local app and synthetic preview were rebuilt; Release metadata/dependency and inventory checks passed again.

Automated tests use synthetic accounts and output. They do not prove support for untested live plans/auth modes or installed Store lifecycle. Before a future publication: owner review, real Codex/Claude account checks, physical screen-reader/high-contrast/multi-display/changed scaling, packaged startup, clean install/upgrade from Store 2.0.2 and uninstall/reinstall. Keep these separate from passing parser/runtime/UI fixtures. No external distribution is authorized by this local implementation.

Very dense expanded monitor details can exceed the available screen height. The full list remains available in the scrollable Usage view and accessibility text; monitor keyboard access opens Usage with Ctrl+1. Physical review of unusually large allowance sets remains pending.
