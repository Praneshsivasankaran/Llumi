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

## Follow-up: Cursor and Gemini through Antigravity

The user explicitly requested this additional feasibility check on 2026-10-12.
Gemini/Antigravity implementation remains outside the current build; this expands
the investigation only. The existing adapter asks provider-owned programs for
account identity and quota: Codex uses `account/read` and `account/rateLimits/read`,
while Claude uses authentication checks and the `get_usage` control request. Both
verify account continuity without extracting the provider's stored secrets.

**Cursor remains conditional and live-blocked.** Desktop 3.24.9 is installed, but
its private `GetUsageLimitStatusAndActiveGrants` RPC uses the internal authenticated
dashboard client. Static presence does not establish an exposed local quota API.
No equivalent documented quota command was found in the [CLI reference](https://cursor.com/docs/cli/reference/parameters)
or [ACP interface](https://cursor.com/docs/cli/acp); its Agent CLI is absent here.

The official [SDK](https://cursor.com/docs/sdk/typescript#agentgetusage) now provides
`Agent.getUsage()` for per-agent token counts and billed costs. Those results lack
account allowance denominators and reset windows, and local access is enabled per
account. The SDK's [authentication contract](https://cursor.com/docs/sdk/typescript#cursorauth)
explicitly excludes reading the installed desktop app's credentials: it uses an
explicit key, an environment key or a separately minted SDK login key. The
[local bridge](https://cursor.com/docs/sdk/bridge) therefore does not establish the
same existing-desktop-sign-in path. The Teams Admin API remains a separate keyed
team integration. No private RPC, dashboard session or credential store was queried.

**Antigravity has a documented technical candidate, with live and permitted-access
blockers.** No Antigravity app or `agy` executable was found in standard installation
locations or indexed apps. Google's [CLI changelog](https://antigravity.google/docs/changelog)
records read-only JSON reporting from version 1.1.11 (2026-08-07):
`agy -p "/usage" --output-format json`. It reports quota without an agent turn,
quota consumption or a saved conversation. [Authentication](https://antigravity.google/docs/cli/install/)
can remain owned by Google's CLI and OS keyring.

The [statusline schema](https://antigravity.google/docs/cli/statusline/) documents
account email, optional plan tier and quota buckets with `remaining_fraction`,
`reset_time` and optional `reset_in_seconds`. This is not proof that the same fields
exist in `/usage` JSON; its exact schema, identity continuity and bucket stability
still require authenticated validation. These are Antigravity quotas, including its
Gemini models, not Gemini chat-app limits or Gemini API billing. Actual pools/resets
must come from the response, rather than inferred [plan limits](https://antigravity.google/docs/plans).

Google's [FAQ](https://antigravity.google/docs/faq) explicitly says consumer-plan
wrapping of `agy` in third-party applications is unsupported; its
[terms](https://antigravity.google/terms) restrict third-party access. No read-only
monitoring exception was found. Llumi needs a permitted monitoring interface or
provider clarification before automatic integration, as well as an installed,
signed-in CLI and live quota/account/error comparisons. Nothing was installed,
authenticated or sent to a model during this check.

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
| Automated regression | Background/alerts baseline: full macOS suite 244 tests, four existing opt-in artifact/live-account tests skipped, zero failures; focused alert engine/controller tests 36 passed. After the notification-feedback repair, all 17 controller tests passed with no failures/skips, including three new regressions. The full suite was not repeated for this scoped repair. |
| Native build | Debug test build and universal arm64/x86_64 Release build succeeded. Only arm64 execution was performed. Release privacy audit and ad-hoc signature verification passed. |
| Device validated | Native Debug background startup had no main/setup window or Dock entry and retained the menu icon even when its saved preference was off. Opening Settings from the popup restored the full window/Dock, closing hid them while monitoring continued, and Quit stopped the process and menu/notch work. A second launch exited and focused the existing instance. Release also passed second-launch, full-interface settings, close/still-running and explicit Quit smoke checks. |
| Provider regression | Existing Codex adapter obtained a live reading. Claude was signed out and showed that state. Synthetic adapter tests cover null/missing readings, retained stale data, recovery, binding verification and existing transport isolation. No real Claude signed-in comparison is claimed. |
| Login/notifications | Native login registration was enabled successfully and restored off. Denial and accepted test requests were validated initially. After the user enabled alerts and reported no banner, the rebuilt Release app displayed a native test banner with display-sharing notifications temporarily allowed by explicit permission. The global sharing setting was restored off; the user's Llumi alerts and permission remain enabled. A real logout/login startup was not observed. |
| Screenshots | Native Debug views captured the popup and complete alert controls with synthetic Codex/Claude readings. No private account output appears in the images. These are local review artifacts outside Git. |
| Unverified/deferred | Authenticated Kimi/Cursor/DeepSeek comparisons, live expired/account-switched/offline behavior for those providers, DeepSeek connection/low-balance alerts, Intel runtime, Windows tray/taskbar, OS logout/login and distribution/signing. |

The final prepared app is `$HOME/Library/Caches/LlumiReview/Llumi Review.app`.
Local evidence resides alongside it: `build-test-final-candidate.log`,
`build-release-final-candidate.log`, sanitized lifecycle records and `screenshots/`
(`background-popup-final.png`, `alert-settings-final.png`). Native view captures
are labelled synthetic and are not committed. The settings capture omits the
sidebar's native material; the complete controls were also inspected on screen.
The Release review bundle has its own preferences and automatic updating is
disabled in the prepared output. The updated app is left open for local review.

### Notification follow-up, 2026-10-12

The device had Llumi notification permission enabled, but macOS was configured to
pause notifications while mirroring or sharing the display. The active screen-control
session showed the sharing indicator. With explicit user permission, changing that
global setting to Allow Notifications caused the rebuilt app's test to appear as a
native banner, verified through Notification Center accessibility and a screenshot.
The setting was immediately restored to Notifications Off. This is expected
[macOS notification behavior](https://support.apple.com/en-au/guide/mac-help/mh40583/26/mac/26).

The former “Test notification sent” feedback overstated successful scheduling and
could disappear on an ordinary provider publication. Test feedback now remains
separate from evaluator status, says that macOS accepted the request, and points to
Focus and display-sharing controls if a banner is absent. Notification settings remain
accessible with permission allowed. Cancellation, denial and failed requests retain
accurate feedback; no system privacy setting is queried or modified by Llumi.

The scoped repair passed 17 native controller tests and a fresh universal Release
build (`build-release-notification-feedback.log`), production privacy audit,
ad-hoc signature verification, public-tree hygiene and diff checks. Device inspection
confirmed the new feedback and settings button. The local candidate manifest records
the source commit and executable hash separately from the earlier baseline test run.

The final security review covers native notification callback isolation, cancellation
on quit, invalid edits, stale/in-flight data suppression, canonical Codex window
identity and multi-window delivery. It found no new credential extraction, helper
server, authentication mutation or extra inference calls. Existing refresh scheduling
and subprocess restrictions remain the only monitoring paths.

## Popup appearance follow-up, 2026-10-12

The user found that the menu-bar popup stayed dark after choosing Light. Both the
NSPopover and SwiftUI content had forced dark overrides. The popup now uses the
existing shared appearance preference, reapplies it to a cached popup, and uses
adaptive text, borders, cards and blue accents. System resolves the application's
effective appearance and observes changes; NSPopover's nil appearance defaults to
VibrantLight and is not used as an inheritance shortcut. Observation stops on quit.

Validation: 28 selected native checks ran, 27 passed and one unrelated opt-in setup
capture was skipped; zero failures in the final run. The initial capture attempt
exposed an existing standalone-runner asset assertion problem. The harness now
checks compiled asset names and passes the app bundle to SwiftUI, matching other
views. Native light/dark popup captures with live, stale and unavailable synthetic
readings were visually inspected. They are saved alongside the review app under
`screenshots/popup-*-light.png` and `screenshots/popup-*-dark.png`.

A fresh universal Release build, production privacy audit, ad-hoc signature check
and public-tree hygiene passed. The rebuilt app preserved the user's Light setting
and alert opt-in. These captures validate native content rendering; a real system
appearance change with the popup open has not been device-validated. The background
popup still needs the user's menu-bar click because the available screen-control
tool cannot target the status item directly. No provider or credential code changed.

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
