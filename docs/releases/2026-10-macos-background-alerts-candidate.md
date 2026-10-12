# Llumi for macOS: background mode and usage alerts

Local release candidate prepared on 2026-10-12. Version/build: **2.2.0 / 5**. This document
does not authorize publication. Existing releases, website downloads and public
update feeds remain unchanged.

The owner selected shared marketing version **2.2.0** to align macOS with Windows.
The earlier 1.1.4/build 4 label and notarized 2.2.0/build 4 candidate are historical.
The owner approved a new Sparkle key and one-time manual installation route, so
macOS internal build advances to **5**. Windows internal/package build numbers
remain independent. The changed key/feed requires new artifacts and verification,
not relabeling earlier checks or notarization receipts.

Build 5 will use `https://tryllumi.com/appcast-v2.xml` and the new Sparkle Keychain
account `io.github.praneshsivasankaran.llumi.sparkle.v2`. Existing 1.1.3 cannot install
it through its old-key updater. The old public `https://tryllumi.com/appcast.xml`
remains intact. See [the migration contract](../product-spec/updater-key-migration-220.md).

## Release notes

- **One manual installation for existing Mac users.** Install the final signed and
  notarized 2.2.0 app manually to enter the new update channel. Keep the same app
  identity and saved settings. The separate new feed supports future updates;
  1.1.3's in-app Check for Updates cannot perform this transition. The candidate
  and new feed remain unpublished.
- **Start in background.** Keep Llumi monitoring from the menu bar without opening
  the main window or showing its Dock icon. Launch at login is a separate setting.
  Open Llumi and Settings remain available; closing the window keeps monitoring
  active, and Quit stops the app.
- **Usage at a glance.** The menu-bar popup shows enabled Claude Code and Codex
  providers, their supported quota windows, available reset countdowns, last
  successful refresh and stale/error states. Refresh, Settings, Open Llumi and Quit
  are available in the popup. The notch remains an independent option.
- **Optional usage alerts.** Alerts start off. After enabling them, configure
  provider controls and individual thresholds; the defaults are 75%, 90% and 95%
  used. Notifications name the quota window. Receipt persistence prevents repeats
  after relaunch, and only the highest newly crossed threshold fires for each
  window. First opt-in emits at most one current-status notification.
- **Consistent appearance.** The popup follows Llumi's Light, Dark or System setting,
  including an existing popup. Light uses neutral light surfaces; Dark keeps the
  compact charcoal design. Both retain blue accents and the monochrome menu mark.
- **Clearer notification testing.** Test feedback distinguishes macOS accepting a
  request from a banner being displayed, survives ordinary refreshes, and keeps
  notification settings accessible. Llumi respects Focus and screen-sharing
  privacy controls. Notifications warn; they do not pause agents or enforce limits.
- **More accurate unavailable Claude usage.** A response reporting supported quotas
  but missing or null usage is treated as unavailable/malformed. Prior verified
  values may remain visibly stale and never trigger alerts.

There is no new Llumi account, backend, provider credential extraction, or inference
request. This candidate retains Claude Code and Codex support. Kimi Code, Cursor,
DeepSeek and Gemini through Antigravity are **not added**.

## Validation and release status

Evidence from earlier local review builds is retained in
[the macOS review record](../macos/BACKGROUND-ALERTS-REVIEW.md). It is not a substitute
for checks against the final distribution artifact.

The 1.1.4 and 2.2.0/build 4 rows below retain historical evidence. Build 4's app and
DMG were notarized successfully but are superseded by the new-key build 5 plan.
No build 5 test, artifact, signing or device result is claimed until recorded below.

| Status | Evidence and limit |
| --- | --- |
| Implemented | Background lifecycle, compact popup, quota alerts, receipt persistence, notification controls and feedback, shared popup appearance, Claude missing/null usage fix. |
| Current build 5 migration | Authorized and pending: new key/account/feed metadata, source rebuild, affected tests/privacy checks, new app/DMG signing and notarization, final hashes, local signed new-feed metadata, manual-install preservation and future new-key updater validation. |
| Previous-label candidate regression | The complete 1.1.4/build 4 native suite ran on 2026-10-12: 247 tests executed, four intentional skips, zero failures. |
| Superseded build 4 candidate checks | Full native suite: 247 tests, four intentional skips, zero failures. Universal Release build, built-app privacy, 17 distribution guards, 15 updater-policy tests and unsigned DMG checksum/mount/contained-app checks passed. The refreshed isolated review app visibly displayed Version 2.2.0, started without the main window, showed both providers Live, and kept monitoring after close. Five user-setting field hashes were unchanged; the receipt store continued updating with fresh observations. |
| Superseded build 4 signed app | Source `71e75c3dd6e243b38c023f3b6b0cda4ffb10070c`. All six code targets passed strict signature, Hardened Runtime, secure timestamp, approved publisher/team and empty-entitlement checks. The app satisfies the installed 1.1.3 app's exact designated requirement. Apple's app notarization submission was Accepted with zero issues; the app was stapled and Gatekeeper accepted it. |
| Superseded build 4 signed DMG | Apple's DMG notarization submission was Accepted with zero issues. The DMG passed stapling, Gatekeeper and mounted contained-app verification. Historical path, hash and size appear below. This does not validate build 5 or its new updater trust chain. |
| Superseded build 4 staged-app smoke | The exact notarized 2.2.0/build 4 app displayed its version in About and both Claude Code and Codex Live, without recording private amounts. A cold launch with Start in background enabled opened no main window; closing the full window retained monitoring; explicit Quit stopped the process. This validates that staged signed app, not build 5, an installed-path login or a Sparkle upgrade. |
| Historical old-key blocker | Build 4 appcast generation failed because the original Sparkle EdDSA private-key item was absent. No appcast or replacement key was created during that attempt. The owner subsequently selected the new-key build 5/manual-install route instead of restoring the old key. |
| Earlier automated checks | Baseline full native suite: 244 tests, four intentional skips, zero failures. Notification follow-up: 17 controller tests passed. Appearance follow-up: 28 selected checks, 27 passed and one unrelated optional capture skipped. |
| Earlier native builds | Debug and universal arm64/x86_64 Release builds passed, along with production privacy, ad-hoc signature and public-tree checks. Runtime execution was on Apple Silicon only. |
| Device/user confirmed | Background operation and the popup with the main window closed; native test notification; Light/Dark popup behavior after the appearance fix. The owner explicitly confirmed each in local review. |
| Other earlier device evidence | Single-instance reopen, Settings/Open/close/Quit, live Codex reading, native login registration on/off. A test banner was observed after temporarily allowing notifications during screen sharing; the global setting was restored. |
| Earlier live provider smoke | After provider-owned Claude sign-in and Llumi Retry, native Llumi Review showed Claude Code Live with five-hour and weekly windows; Codex was also Live. No private amounts or identities are retained here. This is review-app evidence, not final signed-distribution acceptance. |
| Previous-label production build/package | Universal arm64/x86_64 Release 1.1.4/build 4 succeeded. Canonical bundle validation, source/built-app privacy checks, 17 distribution guards and 15 updater-policy tests passed. Unsigned DMG checksum, mount and contained-app validation passed. Production metadata now rejects any local-review marker. |
| Previous-label 1.1.4 review smoke | Replaced the isolated review bundle using the same Release executable. Background launch suppressed the main window; both providers returned Live, Claude with five-hour/weekly windows. Notch Open reached the full UI; closing retained monitoring; Finder launch reopened the same single process; Quit stopped it; next launch stayed in background. Six persisted setting fields retained identical hashes, and native login registration still reported on. |
| Previous-label notification and screenshots | The 1.1.4 native test request was accepted by macOS; this does not prove a visible banner under current screen-sharing suppression. Earlier owner-confirmed banner evidence remains separate. An opt-in native fixture capture test passed, producing light/dark popup and stale/unavailable images with synthetic data. Actual 1.1.4 alert settings were captured through native UI in the review chat. |
| Unverified final gates | Build 5 rebuild/tests/signing/notarization and feed/archive signatures; manual 1.1.3-to-2.2.0 installation with preserved settings; isolated future new-key Sparkle upgrade; final installed-path login session; live OS System-theme switch with popup open; Intel runtime. A 1.1.3-to-new-key-2.2.0 in-app upgrade is not the supported migration path. |
| Publication | Not authorized. No GitHub release, public feed/website change, public release upload or user update occurred. The owner separately authorized the completed Apple notarization submissions. |

Initial preparation found no usable **Developer ID Application** identity and no
configured `NOTARY_PROFILE`. The owner subsequently explicitly approved a replacement
Developer ID certificate for the established team `K38622WCYD`. The original
certificate was left intact; the replacement matches its Keychain private key and
the approved publisher/team. Build 4 app signing and Apple notarization completed
as recorded above; build 5 requires fresh artifacts and verification. No certificate/private-key material or notary
credentials are included in this document. The earlier Claude sign-in blocker was
also resolved; provider and background lifecycle smoke passed on the superseded
build 4 staged notarized app as recorded above.

## Current 2.2.0/build 5 migration record

The selected feed is `https://tryllumi.com/appcast-v2.xml`; its signing account is
`io.github.praneshsivasankaran.llumi.sparkle.v2`. The current plan is one manual
installation followed by future updates under that new trust chain. The old public
feed and published releases remain unchanged. New key generation, source/build
provenance, artifact paths/hashes, test results, signing/notarization, local appcast
verification and native migration acceptance are **pending recording**. Build 4's
hash and Apple acceptance below must not be used as evidence for build 5.

## Superseded 2.2.0/build 4 artifact record

The ignored `dist/macos-2.2.0-candidate/` directory contains the newly built canonical
`Llumi.app`, verified `Llumi-2.2.0-macos-unsigned.dmg` with its SHA-256 sidecar,
`Llumi-2.2.0-macos.txt` release-note payload, and `candidate-manifest.json` with exact
source/artifact hashes and check results from the unsigned rehearsal. These are new
2.2.0 bytes; earlier 1.1.4 artifacts remain unchanged as local history. The unsigned
DMG remains a rehearsal artifact and must not be distributed or relabeled as signed.

The app built from `71e75c3dd6e243b38c023f3b6b0cda4ffb10070c` subsequently passed
Developer ID signing and Apple app notarization/stapling/Gatekeeper. The final DMG
also passed Apple's notarization with zero issues, stapling, Gatekeeper and mounted
contained-app verification:

- File: `dist/macos-2.2.0-final/Llumi-2.2.0-macos.dmg`
- Final size: **4,528,694 bytes**
- SHA-256: `7526b9df339c0d2a8bafdbf68852dc3d3d0311da41e393bd77538217e989eec4`

These are the final stapled **build 4** DMG bytes, retained as superseded local
history. They are not the new-key build 5 artifact and must not be published as it.
Their notarization/signatures do not transfer to rebuilt bytes.

The official `generate-appcast.sh --execute` attempt stopped because the existing
Sparkle EdDSA account `io.github.praneshsivasankaran.llumi.sparkle` returned Keychain
item-not-found (`-25300`). The official service/default search and a narrow local
backup lookup did not find the original key. `appcast.xml` was not created. No new
Sparkle key was generated during that attempt, and installed versions' trusted
public key was not changed. The owner later authorized a separate new key/feed and
manual installation; original-key restoration is no longer the selected plan.

At the build 4 checkpoint, the separate ad-hoc signed Llumi Review app showed
**Version 2.2.0**, with updater disabled and user settings preserved. Its previous
1.1.4 bundle was backed up. The installed production 1.1.3 app remains untouched.
After the staged notarized-app smoke, the temporary background preference was
restored to its original absence, all eight tracked setting hashes matched their
pre-test values, and the feed preference was unchanged. Llumi Review was restored
running in background. No production installation or updater test is implied.

The build 4 test log is `build-test-release-2.2.0.log` in the local LlumiReview
cache; XCTest results are under `LlumiRelease220Tests` DerivedData. No fresh visual
fixture captures were needed for a metadata-only rename; previous-label images
remain explicitly labeled by their original evidence.
The local-only `signing/notarized-2.2.0-settings.png` capture shows actual Settings
from the staged notarized build 4 app with production usage alerts at their default off
state. Earlier synthetic popup captures remain rendering evidence, with their
original candidate labels; they are not recast as screenshots of this signed app.

## Previous-label 1.1.4 artifacts and reproducibility

The ignored `dist/macos-1.1.4-candidate/` directory contains:

- `Llumi.app`: canonical production identity, universal Release, **not Developer ID signed or notarized**.
- `Llumi-1.1.4-macos-unsigned.dmg` and SHA-256 sidecar: verified local packaging rehearsal, **not distributable**.
- `Llumi-1.1.4-macos.txt`: reviewed local release-note payload for later official appcast generation.
- `candidate-manifest.json`: exact source commit, executable/DMG hashes, actual check results and unresolved gates.
- `screenshots/`: fresh synthetic native popup captures in Light/Dark and stale/unavailable states.

The separately identified, ad-hoc signed **Llumi Review** app was refreshed to 1.1.4/4
at its existing local cache path. Its updater remains disabled; its preferences and
the installed production 1.1.3 app were preserved. The old review bundle was backed
up before replacement. This local review identity cannot validate production Sparkle.

Commands actually run for the previous 1.1.4 preparation (historical evidence):

```sh
xcodebuild -project macos/AgentMeter.xcodeproj -scheme AgentMeter -configuration Debug -derivedDataPath "$HOME/Library/Developer/Xcode/DerivedData/LlumiRelease114Tests" -destination 'platform=macOS' CODE_SIGNING_ALLOWED=NO test
LLUMI_RELEASE_OUTPUT="$PWD/dist/macos-1.1.4-candidate" scripts/macos/build-release.sh
python3 scripts/macos/test-pipeline.py dist/macos-1.1.4-candidate/Llumi.app
python3 scripts/macos/test-updater-policy.py
LLUMI_RELEASE_OUTPUT="$PWD/dist/macos-1.1.4-candidate" scripts/macos/create-dmg.sh dist/macos-1.1.4-candidate/Llumi.app --unsigned
scripts/macos/verify-release.sh dist/macos-1.1.4-candidate/Llumi-1.1.4-macos-unsigned.dmg --unsigned
```

The full suite's four skips were explicitly opt-in checks: real-account evidence,
allowance renders, setup renders and update renders. The allowance-render check was
then separately opted in and passed. Real provider smoke was performed in the native
review app without recording account identifiers or private amounts in this report.
No live network-loss, expired-login or account-switch disruption was induced; synthetic
regressions cover those paths. DeepSeek low-balance tests remain deferred with that
unimplemented adapter.

The blocked build 4 attempt left installed production 1.1.3 untouched, with no feed
preference change or private update server. For the authorized build 5 route,
rebuild/sign/notarize the new-key app and DMG, then generate and verify local
new-feed metadata. Validate manual replacement of a recoverable 1.1.3 installation
with settings preserved. Test future new-key updates separately using isolated
signed fixtures and a loopback-only feed; do not misreport this as an old-key
1.1.3 in-app migration. Finish native login-session and System-appearance checks
on the final installed candidate. Keep release uploads and public feed/site changes
blocked until separate publication authorization.

## Provider feasibility

These results are from the 2026-10-12 Mac investigation. They do not establish
Windows compatibility or working integrations in this candidate.

| Provider | Decision | Next gate |
| --- | --- | --- |
| Kimi Code | Documented/released capability; live validation blocked | Install and sign in through Kimi, validate its authenticated loopback usage API and international-region handling, compare with Kimi's own usage, then exercise expiry/offline/account changes. Keep Kimi in charge of credentials and refresh. |
| Cursor | Conditional, unsupported as an automatic personal adapter today | Establish a supported and permitted personal account-usage interface that works with existing sign-in. Installed desktop was 3.24.9; Agent CLI was absent. SDK per-agent cost/token data is not account quota, and SDK auth does not reuse desktop credentials. |
| DeepSeek | Official balance API; live validation blocked | A user-provided key must be entered in a future native secure connection flow and stored in Keychain. Validate currencies and balance components. Separate low-balance controls and top-up recovery are required; percentage quotas and resets cannot be invented. |
| Gemini through Antigravity | Technical candidate; permission and live-validation blockers | The documented read-only `agy` usage command needs an installed, signed-in CLI and schema/account checks. Consumer-plan third-party wrapping is unsupported; no monitoring exception was found. These would be Antigravity quotas, not Gemini chat-app or API billing quotas. |

Official-source links and detailed evidence are in
[the feasibility record](../macos/BACKGROUND-ALERTS-REVIEW.md#provider-feasibility-checked-2026-10-12).
No models were invoked, credentials extracted or provider sessions changed for
these checks. Copilot remains excluded. Kiro, Qwen and local LLM/Ollama monitoring
remain outside this update.

## Windows follow-through

[The Windows handoff](../windows/BACKGROUND-ALERTS-HANDOFF.md) specifies the lifecycle,
tray popup, alert algorithm, appearance behavior, native differences, security
boundaries and acceptance tests. Windows implementation and device validation must
be performed on Windows; this Mac candidate does not prove those results.
