# Windows handoff: background mode and usage alerts

Prepared 2026-10-12 from the macOS local candidate; macOS version/build **2.2.0 / 5**.
The owner selected **2.2.0** as the shared marketing version for macOS and Windows.
Native internal/package build numbers remain independent. The Mac internal build
advances to 5 for its new Sparkle key/feed; earlier 1.1.4/build 4 and 2.2.0/build 4
checks retain their historical labels.
This is an implementation contract, not a claim that this update exists or has been
tested on Windows. Start with [the shared product specification](../product-spec/background-alerts.md)
and preserve the current Windows allowance/setup behavior in
[windows-213.md](../product-spec/windows-213.md). Do not copy the Mac preference
domain, bundle identity, notification framework or internal build number.

Historical Mac signing reference: the superseded 2.2.0/build 4 app from source
`71e75c3dd6e243b38c023f3b6b0cda4ffb10070c` passed Developer ID signing, Apple app
notarization, stapling and Gatekeeper. The final DMG also passed Apple notarization,
stapling, Gatekeeper and mounted contained-app verification. Its old-key updater
metadata attempt failed; no replacement key or appcast was created at that stage.
The owner subsequently chose a new Sparkle key in account
`io.github.praneshsivasankaran.llumi.sparkle.v2` and future feed
`https://tryllumi.com/appcast-v2.xml`, with one manual Mac installation from 1.1.3.
After that installation and future publication, compatible Mac updates follow the
existing automatic check/download setting, with **Restart and update** / **Later**
and installation on normal quit after Later. The migration preserves the user's
setting. This Mac signing-key migration does not prescribe a Windows installer
change or establish Windows updater validation.
Build 5 from `c9d35b3a2640ea2d3c7fc5c204d884a00c81b937` now has a signed/notarized
app and DMG, verified local feed/archive signatures, a successful manual installation
over 1.1.3, and a separate successful private future new-key update test. Native
tests ran 247 cases with four intentional skips and zero failures; distribution,
updater-policy and site-tool tests passed 17, 23 and 25 cases respectively.
Fresh revalidation again ran 247 native tests with four intentional skips and
zero failures. A fresh universal unsigned Release passed 17 distribution tests;
24 updater-policy tests now pass, including minimum update version 5 remaining
fixed for future feed generation. The final signed/notarized build 5 DMG passed
release verification again.

A separate automatic-update rehearsal used the actual final 2.2.0/build 5 runtime
executable in an isolated identity with separate preferences and providers off.
Without a manual check, it downloaded a private synthetic 2.2.1/build 6 fixture
under the same Developer ID/new Sparkle v2 key and minimum update version 5.
Update ready appeared; Later kept build 5 and one process; normal Quit installed
build 6 without relaunching. Reopening showed completion once, with Continue
preventing repetition on the next reopening. The target was signed but not
notarized; retain the earlier build 4-to-exact-notarized-build 5 test as separate
trust evidence. Production app/defaults were unchanged; the test server was
stopped, fixture launch paths retired and test preferences deleted. This validates
Mac behavior only; it does not release 2.2.1 or validate a public update endpoint.

Final installed notification permission was enabled locally by the owner; macOS
accepted the test and the owner confirmed it worked. Temporary feed, update and
background test preferences were restored, with all nine tracked fields preserved.
A later exact installed build 5 check confirmed cold background launch with one
process, accessory activation policy and zero normal on-screen windows; explicit
Finder Open reused that process, closing the main window kept it in background,
and Quit stopped it. Eight tracked nonsensitive settings and their original
presence were restored afterward. Actual logout/login, System appearance change
with popup open and Intel runtime are not established. Encrypted credential
backup is explicitly deferred at the owner's request; no completed backup has
been verified. Exact evidence and remaining items are in the
[Mac candidate record](../releases/2026-10-macos-background-alerts-candidate.md).
Those are macOS-only distribution checks; they do not establish Windows signing,
Store acceptance, installation or notification readiness. No publication occurred.
Finish preparation and verification locally before the owner's final publication
decision; remote pushes, release uploads and site/feed deployment are not authorized.

## Extend the existing Windows architecture

| Concern | Existing Windows location | macOS reference |
| --- | --- | --- |
| Process uniqueness and explicit reopen | `windows/src/AgentMeter/Program.cs`: current and legacy mutexes, show/quit events, strict argument validation | `macos/AgentMeter/App/AppDelegate.swift`, existing instance lease |
| Lifecycle, tray, monitor, refresh publications | `windows/src/AgentMeter/TrayContext.cs` | AppDelegate and `Presentation/BackgroundPolicy.swift` |
| Preferences and migration | `windows/src/AgentMeter/Preferences.cs` | Existing preferences plus `Core/UsageAlerts.swift` |
| Native startup registration | `StartupRegistration.cs`, `PackagedStartupRegistration.cs`, `PackagedEnvironment.cs` | ServiceManagement registration |
| Full interface and palette | `UsageForm.cs`, `Palette.cs`, existing provider/usage controls | `Views/UsagePopoverView.swift`, shared appearance |
| Readings and scheduler | `AgentMeter.Core/Usage.cs`, `UsagePresentation.cs`, `RefreshCoordinator.cs` | UsageStore publications, consumer windows |
| New alert evaluator and native delivery | Add alongside existing Core/application layers; inject delivery for tests | `Core/UsageAlerts.swift`, `Presentation/UsageAlertController.swift`, `Views/UsageAlertSettingsView.swift` |

Reuse the current scheduler, provider adapters and activity detection. The compact
popup must not create a second polling service. A countdown timer can update visible
text without provider requests. Keep cancellation, bounded subprocess output/time,
single-flight refreshes, rate-limit embargoes and safe retry behavior.

## Background lifecycle

- Add **Start in background**, off for fresh and existing installations unless the
  user opts in. Persist it independently from **Launch at login/Start with Windows**,
  tray visibility, appearance, provider controls and overlay Monitor Mode.
- When enabled, the initial process shows no main/setup window. Keep the tray entry
  discoverable even if its ordinary visibility preference was previously off,
  without overwriting that saved preference. Setup stays reachable through Open.
- When disabled, explicit launch follows the existing full-interface/setup flow.
  Native login/startup launch stays quiet under the established lifecycle contract.
- Closing the full window keeps the coordinator and monitoring alive. **Exit**
  cancels owned tasks and helpers, removes tray/monitor surfaces and releases the
  instance lifetime. Opening Settings or Open Llumi focuses one full interface.
- A second explicit launch signals the existing process and opens/focuses that
  interface. A second startup launch stays quiet. Do not duplicate the mutex or
  permit Store/direct/legacy copies to poll simultaneously.
- Keep Monitor Mode independent. Windows has no Dock equivalent; do not emulate
  macOS activation policy by creating undiscoverable taskbar windows.
- Startup registration must report actual OS state and failures. Test packaged
  startup and direct-install startup separately; neither should be inferred from
  the preference boolean.

## Tray popup and appearance

Add an equivalent compact usage popup to the existing tray context; retain the
full Usage/Settings/About interface. The current tray left click opens the full
interface, so deliberately update that interaction while keeping **Open Llumi**
obvious. Retain the native right-click action menu.

- Show enabled providers and their supported reported windows, available reset
  timestamps/countdowns, last successful refresh and live/stale/error states.
  Missing quota/reset stays unknown. Stale values must remain visibly stale.
- Provide **Refresh**, **Settings**, **Open Llumi** and **Exit**.
- Use the compact charcoal-grey/blue design in Dark and light neutral surfaces in
  Light. Reuse the approved monochrome three-bars branding within the compact
  surface; preserve the existing approved native Windows tray/app identity unless
  the shared branding specification is deliberately changed.
- Read the **same saved Appearance** as the main interface and Monitor Mode.
  Light/Dark changes redraw an already-created popup immediately. System follows
  OS changes live. Test text, secondary text, bars, cards, borders, disabled/error
  states and blue contrast in both modes. Avoid a forced dark popup or a separate
  popup theme setting. Preserve the existing setup-specific Light convention.
- Validate keyboard focus, Escape/click-away dismissal, screen-reader labels,
  work-area placement, high contrast where supported, DPI and multiple monitors.

## Quota alerts: exact behavior

Alerts are **off by default**. On opt-in, enable thresholds **75%, 90%, 95% used**
by default. Provide per-provider switches and individual threshold enable/edit
controls. Values must be distinct whole percentages from 1 to 100. Reject invalid
edits while preserving valid saved settings. The Mac evaluator bounds its model
at eight thresholds per provider; the current UI edits the three default rows.

An observation may alert only when its provider is enabled, authentication is
verified, the reading is live and provider-reported, there is no failure or in-flight
check, its account binding is known, and the window has a finite 0–100 used value
plus a **future provider-reported reset**. Only supported consumer windows qualify.
Do not calculate a reset from a clock tick, assume zero/unlimited, or infer a new
window from falling usage. Mac freshness bounds are 120 seconds old and at most
five seconds in the future; its reported-reset horizon is at most 366 days.

Persist a receipt per provider/account/window, with the reset and an acknowledged
threshold range. Keep account identities/window names out of preferences by using
unambiguous length-prefixed SHA-256 bindings. This is duplicate-prevention state,
not raw usage history. The Mac format bounds settings at 16 KiB and receipts at
128 records/64 KiB; Windows may use a native schema with equivalent limits and
atomic, checked persistence. Corrupt/unwritable history pauses alerts visibly
rather than deleting receipts and repeating notifications.

1. For an eligible observation, find newly crossed enabled thresholds and select
   only the highest for each independent window.
2. Acknowledge **all crossed values**, including lower and disabled thresholds,
   before delivery. Editing a threshold lower or toggling it cannot create a burst
   from the same already-seen level.
3. On initial opt-in, wait for an eligible observation and emit **at most one**
   current-status notice across every provider/window; acknowledge the other
   crossed values too. Later refreshes may each alert independent windows.
4. Restarts, provider/account switches, disable/re-enable, usage decreases and
   future-reset jitter never erase receipts. Return to an earlier account within
   the same window must not repeat it.
5. Re-arm only after the prior reset boundary, from a fresh observation recorded
   at/after that boundary with a later provider-reported reset. Unknown, past,
   backward or prematurely changing resets are insufficient.
6. Keep window scope/duration identity stable. Codex's exact parser-owned
   `bucket:primary`/`bucket:legacy:primary` (and secondary equivalents) identify
   the same quota across supported transport envelopes. Canonicalize those exact
   forms only, preserving escaped bucket names and separate same-duration slots.

Notification title names the provider; body names the actual window and the used
percentage/threshold. Model/additional names must come from supported normalized
fields and be sanitized/bounded. Never include identities, raw responses or secrets.

## Native delivery and notification settings

- Choose supported Windows notification APIs appropriate to the packaged/direct
  identities; validate activation, permission/policy and registration for both
  channels. Respect Focus/Do Not Disturb and OS presentation controls.
- Ask for native permission only through a supported user opt-in/test flow. Show
  denial/unavailable states, a notification-settings shortcut and an explicit
  **Test notification** action. Testing must not enable quota alerts implicitly.
- Say a request was accepted when that is all the API confirms. A successful API
  call does not prove a visible banner. Explain relevant Windows suppression
  settings without changing global notification privacy or Focus settings.
- Keep test feedback separate from refresh/evaluator status so ordinary readings
  do not erase it. Cancellation/Exit must not later show success. Handle denial
  and failed delivery with safe messages, without raw system/provider errors.
- Use bounded serial delivery separate from cancellable refresh evaluation.
  Ordinary same-account publications must not discard already-committed notices.
  Defer while checking. Before each send, revalidate the **exact originating
  account/window**, reset, scope, duration, freshness and threshold. Remove invalid
  queued notices after account switch, stale/error/expired data, window removal,
  provider/threshold disablement or Exit. Another window with equal numbers is not
  a substitute.
- Commit receipts before delivery. Best-effort notification failure must not cause
  repeated threshold delivery on each poll. Provide the user-triggered test to
  troubleshoot. Notifications do not pause agents or enforce spending limits.

## Claude null/missing usage parity

The macOS update corrected `rate_limits_available: true` with null/missing
`rate_limits`: this is failed/unavailable retrieval, not a fresh empty quota or zero.
Windows already contains the equivalent distinction in
`AgentMeter.Core/ClaudeControlTransport.cs::ParseAllowance`: null raises Network,
missing raises Malformed, and explicit `rate_limits_available: false` remains
NotReported. Preserve and regression-test it; do not add a duplicate workaround.
Verify the coordinator retains any allowed prior reading as stale and alerts ignore it.

## Provider scope and feasibility gates

Only **Claude Code and Codex** are implemented by this update. The Mac investigation
is recorded with official sources in [BACKGROUND-ALERTS-REVIEW.md](../macos/BACKGROUND-ALERTS-REVIEW.md).
Repeat installation/version, permitted-access, auth compatibility, schema and live
comparisons on Windows before enabling another adapter.

| Provider | Gate and contract |
| --- | --- |
| Kimi Code | Documented released local usage API, but absent/not signed in on the Mac. Prefer Kimi-owned auth/refresh. Validate authenticated loopback GET `/api/v1/oauth/usage`, available windows/ratios/resets, international-region handling and `/usage` comparison. Never launch an unauthenticated/LAN broad-agent server just for monitoring. |
| Cursor | Installed desktop evidence is conditional; no supported personal quota interface or CLI-only path was validated. SDK per-agent tokens/cost lacks account denominators/resets and does not reuse desktop credentials. Do not copy desktop tokens/cookies, invoke private dashboard RPCs as if supported, or silently install the CLI. |
| DeepSeek | Official GET `/user/balance`; requires explicit user key. Future Windows connection stores it in Credential Manager and restricts requests to the official endpoint. Preserve every returned currency and decimal balance field plus `is_available`. No subscription/chat quota, percentage or reset. Add currency-specific low-balance thresholds, receipt persistence and top-up/recovery re-arming with tests before enabling it. No key in chat. |
| Gemini through Antigravity | Read-only CLI usage is documented, but no installed/signed-in CLI or live JSON schema was validated. Consumer-plan third-party wrapping has a permitted-access blocker. Establish an allowed monitoring interface first. Antigravity quotas do not establish Gemini chat/API quotas. |

Do not invoke models to test monitoring. Never modify provider credentials or
sessions. Report absent fields as unknown. Copilot is excluded; Kiro, Qwen and local
LLM/Ollama monitoring remain outside scope. Balance alerts are deferred with DeepSeek,
not an implemented Mac feature to port.

## Optional taskbar usage: Windows-only feasibility

Investigate **Show taskbar usage**, off by default, only on Windows using supported
taskbar progress/overlay APIs. A pinned shortcut alone cannot display live progress;
a real window with a taskbar button is required. Define selected provider/window
semantics explicitly, including switching, unavailable/stale/error state and reset.
Validate Explorer restart/re-registration, minimize/close/reopen/Exit, DPI and
readability. Keep this independent from tray background mode and Monitor Mode.

If useful taskbar output requires problematic hidden windows, Explorer injection,
patching or misleading quota selection, leave it out and record the limitation.
No taskbar implementation or feasibility result was established on this Mac.

## Acceptance and release checklist

Use synthetic fixtures for automated tests. Suggested groups mirror the Mac
`BackgroundPolicyTests`, `UsageAlertTests`, `UsageAlertControllerTests` and popup
appearance checks; adapt them to Windows architecture instead of translating UI APIs.

- **Lifecycle:** defaults/migration/invalid preferences, background and normal start,
  close/still monitoring, Open/Settings, explicit second launch, startup second
  launch, Exit/helper cleanup, hidden-tray discoverability, independent Monitor Mode.
- **Thresholds:** exact and multi-threshold crossing, multiple windows/providers,
  initial opt-in at most one notice, per-provider/threshold controls, invalid edits,
  disabled thresholds/lower edits without delayed bursts.
- **Continuity:** persisted restart receipts, account A/B/A, disable/re-enable,
  expired/unknown/stale/failed/unverified/in-flight data, backward/jittering resets,
  fresh successor reset, Codex supported aliases and separate equal-duration windows.
- **Persistence/delivery:** corrupt/oversized/unwritable/full receipts fail safely;
  permission denial/unavailable/send failure; first send concurrent with refresh;
  exact-origin removal; account switch/disable/Exit cancellation; test feedback
  survives refresh and never claims banner visibility from scheduling alone.
- **Native device:** real background startup, main-window close, tray interaction,
  keyboard/accessibility, native startup through sign-out/in, real notification
  with permission/Focus variants, Light/Dark/System changes with popup open, mixed
  DPI/multiple displays, Explorer restart, sleep/network loss/recovery.
- **Provider/security regression:** signed-in Claude and Codex readings compared
  with provider UI; preserve isolation, allowed endpoints, safe fixed subprocess
  arguments, no inference, sanitized logging and bounded retries/resources.
- **Packaging:** run the Windows suite, build exact candidate, production privacy
  and payload audits, upgrade/settings preservation and native installation gates
  for the intended channel. Complete [distribution gates](../DISTRIBUTION.md) for
  direct downloads. A Store submission artifact is not a GitHub download by default.

Keep four result categories: **implemented**, **automated-tested**, **device/user
verified**, and **unverified/deferred**. Mac user confirmations of popup/background,
notifications and appearance are reference behavior only; they cannot validate
Windows code, OS permissions or taskbar behavior. No publication is authorized by
this handoff.
