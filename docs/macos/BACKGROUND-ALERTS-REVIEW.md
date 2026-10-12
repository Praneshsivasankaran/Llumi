# macOS background and alerts local review

This is local work only, based on canonical `main` commit
`39f065a6ba208a7c3e3d70aa7c6670177bd7945a`. The initially selected repository was
empty; the matching public Llumi repository was recovered without overwriting local
changes. No release version, signing identity, feed or published binary was changed.

## Provider feasibility, checked 2026-10-12

| Provider | Gate | Current machine and evidence | Remaining blocker |
| --- | --- | --- | --- |
| Kimi Code | Blocked; documented/released capability | No installed CLI, app, login directory or default loopback listener found. Official released 2.1.1 includes GET `/api/v1/oauth/usage`. | User-owned installation/login and authenticated loopback API, comparison with `/usage` or account console, expiry/offline/account-switch and international account testing. |
| Cursor | Conditional personal integration; CLI-only blocked | Desktop 3.24.9 arm64 installed; separate Agent CLI absent. Documented CLI status/whoami are authentication checks, without a usage command. | Supported personal usage interface, permitted credential compatibility and dashboard comparison. Team Admin API requires different credentials and changes scope. |
| DeepSeek | Blocked live validation; documented balance endpoint | No user-provided API key in the process environment. Official GET `/user/balance` reached the provider and returned HTTP 401 without credentials. | User connects a key in a future native secure field; never paste it into chat. Validate returned currencies/balance components and recovery before adding the adapter. |

None passed the authenticated gate, so none is enabled or represented as working
in this build. No model was invoked for feasibility checks, provider sessions were
not altered, and no credentials/cookies or account identity were extracted.

Kimi returns only present windows (`limit5h`, `limit7d`, `monthTotal`, `monthCode`) with
reported ratios and optional resets. Global region selection is implemented by
Kimi. Its local API remains experimental and its server token grants broad agent
capabilities: do not start an unauthenticated/LAN server just for monitoring.
[Released API](https://github.com/MoonshotAI/kimi-code/blob/f67e639/docs/en/reference/server-api.md),
[usage implementation](https://github.com/MoonshotAI/kimi-code/blob/f67e639/packages/oauth/src/managed-usage.ts),
[region handling](https://github.com/MoonshotAI/kimi-code/blob/f67e639/packages/oauth/src/region.ts),
[security guidance](https://github.com/MoonshotAI/kimi-code/blob/f67e639/docs/en/guides/web.md).

Cursor's private dashboard/RPC presence does not establish a supported third-party
contract. The installed `cursor agent` wrapper would automatically install its CLI
when absent; it was inspected rather than invoked. Current documented pools are
Cursor Models and Other Models, reset with the billing cycle. Missing pools stay
unknown. [CLI reference](https://cursor.com/docs/cli/reference/parameters),
[authentication](https://cursor.com/docs/cli/reference/authentication),
[Admin API](https://cursor.com/docs/account/teams/admin-api),
[usage limits](https://cursor.com/help/models-and-usage/usage-limits).

DeepSeek is API balance only: preserve each returned currency and the decimal strings
`total_balance`, `granted_balance`, `topped_up_balance`, plus `is_available`. No
subscription quota, usage percentage or reset is inferred.
[Official balance schema](https://api-docs.deepseek.com/api/get-user-balance/).

## Architecture and implementation

- AppDelegate owns lifecycle, status/popover and notch, while the existing secure
  kernel lease owns process uniqueness. Start in background persists separately
  from native login registration. Full interface visibility controls Dock presence.
- UsageStore remains the sole refresh scheduler, with existing single-flight,
  cooldown, rate-limit embargo and reset timers. The popover's timer updates text
  only; alerts consume publications rather than adding queries.
- UsageAlertEvaluator stores bounded opaque receipt hashes and threshold bands in
  UserDefaults. Quota readings stay in memory. Fresh verified observations, future
  reported resets and account/window continuity gate every notification.
- UsageAlertController requests native notification permission on opt-in/test,
  reports denial/errors, and cancels owned tasks on quit. Settings reject invalid
  field edits without replacing the user's valid configuration. A bounded serial
  delivery queue survives ordinary refresh publications, defers checking readings,
  and discards notices whose exact originating account/window is no longer valid.
- Claude null/missing usage now fails correctly; verified old readings can remain
  visibly stale, and never trigger alerts. Fixed subprocess arguments/isolation,
  inference rejection, bounded output/deadlines and sanitized logs remain in place.

## Local build procedure

Build with Xcode 27 / Swift 6.4 on macOS 26.6, using a dedicated DerivedData directory
and a review bundle identifier:

```sh
xcodebuild -project macos/AgentMeter.xcodeproj -scheme AgentMeter \
  -configuration Debug -derivedDataPath "$HOME/Library/Developer/Xcode/DerivedData/LlumiReview" \
  -destination 'platform=macOS' CODE_SIGNING_ALLOWED=NO \
  PRODUCT_BUNDLE_IDENTIFIER=io.github.praneshsivasankaran.llumi.review test
xcodebuild -project macos/AgentMeter.xcodeproj -scheme AgentMeter \
  -configuration Release -derivedDataPath "$HOME/Library/Developer/Xcode/DerivedData/LlumiReview" \
  -destination 'platform=macOS' CODE_SIGNING_ALLOWED=NO \
  PRODUCT_BUNDLE_IDENTIFIER=io.github.praneshsivasankaran.llumi.review build
python3 scripts/macos/prepare-local-review.py \
  "$HOME/Library/Developer/Xcode/DerivedData/LlumiReview/Build/Products/Release/Llumi.app"
python3 macos/Scripts/privacy-check.py \
  "$HOME/Library/Developer/Xcode/DerivedData/LlumiReview/Build/Products/Release/Llumi.app"
```

The preparation step is restricted to the review bundle, disables updater activity
before first launch, and ad-hoc signs that output only. Existing installed Llumi
preferences/provider credentials are preserved. The existing product lease also
prevents simultaneous monitoring by release and review copies.

## Validation record

Executed on an Apple Silicon Mac running macOS 26.6 (25G72), Xcode 27.0
(27A266a), Swift 6.4. Installed Claude Code was 2.1.294 and Codex CLI 0.161.0.

| Category | Result and practical limit |
| --- | --- |
| Implemented | Background launch/Dock policy, compact menu-bar popup, independent login/notch controls, configurable optional Codex/Claude quota alerts, bounded persisted deduplication, permission/test UI and Claude missing/null usage correction. |
| Automated regression | Final full macOS suite: 244 tests, four existing opt-in artifact/live-account tests skipped, zero failures. Final focused alert engine/controller tests: 36 passed, zero failures/skips. They cover independent equal-valued windows, held delivery, refresh deferral, account/window changes, disable/quit and Codex aliases. |
| Native build | Debug test build and universal arm64/x86_64 Release build succeeded. Only arm64 execution was performed. Release privacy audit and ad-hoc signature verification passed. |
| Device validated | Native Debug background startup had no main/setup window or Dock entry and retained the menu icon even when its saved preference was off. Opening Settings from the popup restored the full window/Dock, closing hid them while monitoring continued, and Quit stopped the process and menu/notch work. A second launch exited and focused the existing instance. Release also passed second-launch, full-interface settings, close/still-running and explicit Quit smoke checks. |
| Provider regression | Existing Codex adapter obtained a live reading. Claude was signed out and showed that state. Synthetic adapter tests cover null/missing readings, retained stale data, recovery, binding verification and existing transport isolation. No real Claude signed-in comparison is claimed. |
| Login/notifications | Native login registration was enabled successfully and restored off. Denied notification permission was visible; after native permission was allowed, the user-triggered test was accepted by macOS. Alert opt-in and native permission were both restored off; Release reflected denial after returning from Settings. Banner appearance and a real logout/login startup were not observed. |
| Screenshots | Native Debug views captured the popup and complete alert controls with synthetic Codex/Claude readings. No private account output appears in the images. These are local review artifacts outside Git. |
| Unverified/deferred | Authenticated Kimi/Cursor/DeepSeek comparisons, live expired/account-switched/offline behavior for those providers, DeepSeek connection/low-balance alerts, Intel runtime, Windows tray/taskbar, OS logout/login and distribution/signing. |

The final prepared app is `$HOME/Library/Caches/LlumiReview/Llumi Review.app`.
Local evidence resides alongside it: `build-test-final-candidate.log`,
`build-release-final-candidate.log`, sanitized lifecycle records and `screenshots/`
(`background-popup-final.png`, `alert-settings-final.png`). Native view captures
are labelled synthetic and are not committed. The settings capture omits the
sidebar's native material; the complete controls were also inspected on screen.
The Release review bundle has its own preferences and automatic updating is
disabled in the prepared output. It was quit after final native validation.

The final security review covers native notification callback isolation, cancellation
on quit, invalid edits, stale/in-flight data suppression, canonical Codex window
identity and multi-window delivery. It found no new credential extraction, helper
server, authentication mutation or extra inference calls. Existing refresh scheduling
and subprocess restrictions remain the only monitoring paths.

## Windows handoff

Adopt [the shared contract](../product-spec/background-alerts.md). Keep Start in
background separate from authoritative startup registration, keep overlay independent,
and ensure the tray/full interface stays discoverable. Use a native tray popup and
Windows notifications; persist equivalent hashed account/window threshold receipts.
Preserve one highest new threshold per quota window and one initial current notice,
freshness/reset/account rules, permission denial and explicit Exit cleanup. Preserve
committed notices across ordinary refreshes, defer in-flight checks, and validate the
exact originating account/window before sending. Native delivery remains best effort;
failed delivery is visible and must not repeatedly emit the same threshold.

Provider gate findings do not prove Windows feasibility. Repeat installation,
credential compatibility and authenticated comparisons there. DeepSeek needs an
explicit Credential Manager connection and currency-specific low-balance evaluator,
including top-up recovery tests, before enabling support.

Investigate taskbar progress/overlay via supported APIs and a real taskbar window;
validate Explorer restart, scaling and selected provider/window semantics. Leave it
out if it depends on hidden-window hacks. No Windows implementation, taskbar test,
Intel runtime validation, logout/login cycle or distribution validation is claimed
from this Mac session.
