# Background operation and optional usage alerts

This update is implemented on macOS only. Windows should adopt the same behaviour
contracts using a tray popup and native notifications; its overlay remains independent.
No taskbar changes are attempted or claimed on macOS.

## Lifecycle and popup

- Persist a separate **Start in background** preference, off for existing/fresh
  installations. **Launch at login** retains authoritative ServiceManagement state.
- Start in background suppresses the main/setup window on launch, keeps a menu-bar
  entry available, and hides the Dock icon while no full interface is visible.
- Open Llumi, Settings, setup and an explicit second launch restore/focus the full
  interface and its Dock entry. Closing the full interface keeps monitoring alive;
  the background preference hides the Dock entry again. Quit stops owned work.
- The kernel instance lease remains the sole instance mechanism, including app copies.
- Menu-bar click opens a compact charcoal-grey popover with blue accents and a
  monochrome three-bars mark. Right click retains native action-menu access.
- Show enabled providers and every supported reported window; missing usage/reset
  remains unknown. Show last successful observation, live/stale/error state and
  available reset countdowns. Refresh, Settings, Open Llumi and Quit are accessible.
- Notch behaviour and placement remain an independent option. Background mode cannot
  strand the app when the user previously disabled the menu-bar icon.

## Alerts

- Alerts are off by default. Opting in enables editable 75%, 90%, 95% used thresholds
  by default, with individual threshold and provider controls.
- Evaluate only fresh, authenticated, provider-reported readings with a known window
  identity and future reported reset. Unknown or elapsed resets cannot establish a
  new window. Never infer resets from a clock tick or a decrease in usage.
- Each account/provider/window has its own threshold receipts. Persist opaque hashes
  of account/window identity and receipts, never raw account data or usage history.
- At most the highest newly crossed enabled threshold fires for each window on a
  refresh. First opt-in above thresholds emits at most one current-status notice
  across all providers/windows, once an eligible observation arrives. All lower
  crossed thresholds are acknowledged so changing controls cannot produce a burst.
- A fresh provider-reported successor window re-arms alerts. Restarts, disable/enable,
  stale results, account switching and usage decreases do not erase receipts.
- Native permission is requested on opt-in or a user-triggered test notification.
  Denial is visible with a link to native notification settings. Alerts warn only;
  they do not pause tools or enforce spending limits.
- DeepSeek, when authenticated feasibility passes in a future change, needs separate
  currency-specific low-balance controls and recovery/top-up re-arming; no percentage
  alerts, invented denominator, subscription quota or reset windows.

## Feasibility gate on 2026-10-12

Kimi Code: documented/released usage API with Kimi-owned OAuth/region handling;
not installed or signed in here, so authenticated loopback usage and comparison are
blocked. Do not start a broad-capability Kimi server or extract credentials.

Cursor: desktop 3.24.9 installed; individual CLI absent and no documented individual
usage interface found. Private dashboard/RPC paths remain conditional, not a
supported API guarantee. Team Admin API credentials are outside automatic personal
sign-in scope. Do not copy desktop tokens or install the CLI implicitly.

DeepSeek: official GET https://api.deepseek.com/user/balance is reachable and rejects
unauthenticated access (401). No user-provided key is available. Live balance and
currency validation are blocked. A future connection must use a native secure field
and Keychain, never a key pasted into chat or stored in preferences.

## Focused implementation and acceptance

1. Extend existing preferences, AppDelegate, menu-bar entry and instance lease.
   Verify background launch, full-window close, second launch, popup actions and Quit.
2. Add a bounded persisted alert evaluator, native notification delivery and settings.
   Test crossing, multiple windows, restarts, resets, account changes, stale/unknown
   readings, invalid settings and permission/error handling with synthetic fixtures.
3. Reuse UsageStore publications and cadence; add no provider polling subsystem.
   Audit subprocess isolation, logs, persistence and updater behaviour for local review.
4. Run the existing macOS suite, new tests, native Debug/Release builds and production
   privacy check. Capture popup/settings and distinguish synthetic screenshots from
   live provider/device evidence. No publishing, release signing or update uploads.

Balance alert tests/adapter are deferred together with DeepSeek authentication; do
not claim them as implemented or validated. Windows live tests and taskbar feasibility
remain for a Windows session.
