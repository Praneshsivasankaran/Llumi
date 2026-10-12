# macOS updates

The active local candidate is **2.2.0/build 5**. See [its preparation record](../releases/2026-10-macos-background-alerts-candidate.md)
for completed local checks, remaining native/backup items and historical build 4 evidence. The latest public release is
[1.1.3/build 3](../releases/2026-10-macos-1.1.3.md), published on 2026-10-05.
Public feeds, notes and downloads remain unchanged during 2.2.0 preparation.

## Authorized 2.2.0 manual migration

The original Sparkle private key could not be found. The owner selected one manual
installation of a newly signed/notarized 2.2.0/build 5 app, using a new Sparkle key
in account `io.github.praneshsivasankaran.llumi.sparkle.v2` and the separate future
feed `https://tryllumi.com/appcast-v2.xml`. The existing public
`https://tryllumi.com/appcast.xml` and historical release bytes remain intact.
Installed 1.1.3 cannot validate the new key and cannot perform this transition
through Check for Updates. Same bundle ID/team, settings and provider sessions
must survive the manual installation. The new feed supports future compatible
updates after installation and publication; it is not currently published.

Follow [the migration contract](../product-spec/updater-key-migration-220.md).
Build 5 completed fresh tests, signing/notarization and local feed/archive signature
verification; it does not reuse build 4 receipts. Manual installation and an isolated
future new-key updater test both passed, as recorded below. No signature,
notarization, Gatekeeper, transport or downgrade checks were disabled to make
old-key clients accept the new candidate.

## Local build 5 acceptance

Release source is `c9d35b3a2640ea2d3c7fc5c204d884a00c81b937`, with runtime metadata
from `f877457`. The native suite ran 247 tests with four intentional skips and no
failures; 17 distribution and 23 updater-policy tests passed. Website tooling at
`afec470` passed 25 tests. Both the app and DMG received Apple Accepted results
with zero issues, were stapled and passed Gatekeeper. Local feed/archive signatures
verified against the new key. The exact final DMG and provenance are in the
[candidate record](../releases/2026-10-macos-background-alerts-candidate.md#current-220build-5-migration-record)
and local `dist/macos-2.2.0-build5/release-state.json`.

A disposable **new-key** 2.2.0/build 4 host downloaded and installed the exact final
build 5, then relaunched as one process. Its completion notice was observed and
acknowledged. Old-key feed, tampered feed, signed malformed feed and invalid archive
signature were rejected. An equal build was not offered, and an unavailable local
server produced a recoverable error. This is future new-key updater validation;
it does not claim a 1.1.3 old-key in-app upgrade.

The separate manual installation replaced `/Applications/Llumi.app` 1.1.3 with exact
build 5 bytes from the notarized DMG. Both providers were Live, all nine tracked
setting values and their presence were preserved, Launch at Login remained on, and no false completion notice
appeared. Second launch retained the process; Quit stopped it. Temporary feed, automatic-update and background overrides were restored.
After the owner enabled system permission locally, the installed app reported the
test accepted and the owner confirmed the final notification worked. Alerts remain
at their original off setting. Cold background launch started the process, but UI observation reopened
the window, so final window absence/Dock state is not independently confirmed.
Actual logout/login, live OS System appearance with popup open, Intel/Windows
execution and the owner's encrypted key backup remain unverified. The backup
helper is prepared and has not exported the key.

The optional v2 site-builder support is tested; no new feed is present in the site
and nothing has been deployed. The old public feed remains unchanged.

## Update behavior introduced in 1.1.3

Version 1.1.3/build 3 adds native update controls and a once-after-verified-update popup. Settings has a manual **Check for Updates** button and one **Automatically check and download updates** switch. On enables both Sparkle-owned preferences; off leaves checks manual. Existing automatic-download choices determine the combined preference; checking-only choices become manual. Fresh installs default off. Startup waits for setup completion. System profiling remains disabled.

The manual button checks and downloads an available ordinary application update without changing the automatic preference. A narrow standard-user-driver subclass accepts only a user-initiated ordinary signed application download; informational, major and non-application updates retain Sparkle’s standard review flow. Neither a resumed installing update nor download completion may force a restart. After Sparkle prepares either a manual or automatic update, Llumi shows **Restart and update** / **Later**. Later keeps the current process running; Sparkle can install the prepared update on natural quit. Turning off automatic updates affects future checks/downloads, not an already prepared update. Foreground automatic checks coalesce with active sessions and are throttled to once per 15 minutes. Check for Updates reopens an already prepared prompt instead of starting another session. Unacknowledged completion blocks another cycle until Continue/close acknowledges it.

Installation receipts are recorded only at verified preparation for on-quit installation or Sparkle's actual installation callback. A receipt binds origin/target numeric version/build to a hash of the canonical host path. Only that same host running the exact newer target can show **The app has been updated**, version, **View release notes** and **Continue**. The trusted HTTPS notes URL is derived from the validated running marketing version. No remote HTML is rendered by Llumi. Acknowledgment suppresses the notice on subsequent launches; quitting before acknowledgment retains it. Canceled/failed installs, mismatched targets, downgrades, fresh installs and checks/downloads alone never show success. Preview and installed hosts sharing a bundle ID cannot consume one another's records. First 1.1.2 upgrades stay quiet without verified evidence, as approved by the owner; inherited preferences do not establish installation.

Each new candidate remains local until the owner reviews it and approves publication. The installed published app must stay recoverable. A private Mac-only test can temporarily override a compatible test host's `SUFeedURL` through its user defaults to a loopback server with a signed feed/archive. This is Sparkle's supported testing override; preserve and restore the previous default, retain all verification flags, and never deploy that feed publicly or modify the installed host's signed bundle. For 2.2.0/build 5, validate manual migration from 1.1.3 and future in-app updating between new-key fixtures separately; the old-key 1.1.3 host is not a compatible updater test source for build 5. The historical 1.1.2 → 1.1.3 acceptance is recorded in the 1.1.3 publication receipt; it does not validate 2.2.0. Notarization and publication receipts are separate from a local Developer ID signed build.


## Historical native 1.1.3 local validation — 5 October 2026

The next two sections retain staged evidence and gates as recorded before the
1.1.3 release. Later completion is recorded in its publication receipt; these
paragraphs do not describe the current public release or the 2.2.0 candidate.

The scoped snapshot passed 182 macOS tests: 180 passed, two existing opt-in provider/capture tests skipped. This includes 25 receipt/identity/isolation regressions, five presentation state tests and native Light/Dark restart/completion renders. Release policy has 14 passing tests; website tests have 18 passing cases and routing has three. Website browser validation passed in Chromium, WebKit and Firefox. The removed `/review/update-flow/` route returns 404; release history and local 1.1.3 notes return 200.

These validate the model, native views and source wiring, not an actual download/install/relaunch. The separately approved Mac-only upgrade remains pending. The production 1.1.3 notes destination stays unpublished until approval; the exact production URL is wired, and its content is reviewable at the local version route. No public feed/configuration, installed app, provider collection or approved appearance/checking migration changed. Concurrent setup/demo changes were preserved outside this update commit and excluded from its build snapshot.

## Historical combined update controls — local validation

The follow-up control simplification passed 201 macOS tests: 197 passed and four opt-in live/capture tests skipped. Coverage includes combined preference adoption, manual-only/off behavior, automatic-download policy restrictions, excluded update types and stages, Later, and one-shot restart. Universal arm64/x86_64 Release build, production source/bundle privacy checks, 14 updater-policy tests and 17 distribution tests passed. Local Developer ID signing and strict verification passed; notarization and public release were not performed.

The local signed 1.1.3/build 3 replaced the prior review app at `/Applications/Llumi.app` with preferences preserved and a recoverable ZIP backup. Native Settings visibly shows one combined automatic switch plus the manual button. The existing download-off setting remained combined off; Launch at Login, Light appearance and provider switches were preserved. Hostless decision tests and this native UI check do not establish a complete newer signed update download, restart, successful installation or completion-popup cycle. That private cycle remains required before publication.

## Historical published 1.1.2 baseline

macOS 1.1.2/build 2 was published on 3 October 2026. Its signed production feed and website download went live, and the final public app was installed and physically verified on the owner's Mac. See the [publication receipt](../releases/2026-10-macos-1.1.2.md). It was superseded by 1.1.3 on 5 October; all published tags and bytes remain frozen. `macos/release-candidate.json` records the current local candidate, now 2.2.0/build 5; each candidate must advance its internal build and satisfy version policy with matching bundle metadata. This file is separate from the immutable launch baseline.

Existing 1.1.1 users must install 1.1.2 manually once; compatible later macOS updates can then be delivered in-app. Windows Store 2.0.2 is unchanged. The Claude presentation fix remains its own cross-platform commit, `c9a77c5`; Windows native UI regression execution is required before another Windows package.

## Integration and behavior

Sparkle 2.10.0 is pinned exactly through Swift Package Manager, with its resolved commit and upstream binary checksum recorded. The public `SPUUpdater` with a narrow `SPUStandardUserDriver` subclass targets the main app bundle and remains responsible for feed/archive verification, download, extraction, authorization and installation. Llumi adds the ready-update and completion windows described above without introducing a custom installer, version comparator, independent downloader or public feed change. Settings exposes one combined control for the Sparkle-owned checking and automatic-download preferences.

Existing production feed: `https://tryllumi.com/appcast.xml`; selected new-key future feed: `https://tryllumi.com/appcast-v2.xml`. The [1.1.3 publication receipt](../releases/2026-10-macos-1.1.3.md) records the latest published signed feed/DMG verification; current local work does not change the old feed or publish the new one. Feed and release notes require Sparkle signatures; signature failure expiration is disabled. Downloaded archives are verified before extraction. System profiling is disabled, and the updater delegate returns no allowed profile keys even if an inherited Sparkle preference enables profile submission. No Llumi/provider data is attached to update requests.

Official references: [setup](https://sparkle-project.org/documentation/), [programmatic controller](https://sparkle-project.org/documentation/programmatic-setup/), [consent/security options](https://sparkle-project.org/documentation/customization/), [publishing](https://sparkle-project.org/documentation/publishing/), [stable release](https://github.com/sparkle-project/Sparkle/releases/tag/2.10.0).

## Signing material and artifact

A dedicated Sparkle EdDSA key was generated for earlier releases with upstream `generate_keys --account io.github.praneshsivasankaran.llumi.sparkle` and stored in login Keychain. That historical record does not establish its current availability. During 2.2.0/build 4 preparation on 2026-10-12, the official appcast tool reported the original key missing (`-25300`); focused login/iCloud Keychain checks found no matching item. No replacement Sparkle key was created during that attempt. The separately approved replacement Developer ID identity signed build 4's app and DMG; both passed Apple notarization, stapling and Gatekeeper. Those receipts remain historical and nothing was published.

The owner then authorized a new Sparkle key in account `io.github.praneshsivasankaran.llumi.sparkle.v2`, the separate `appcast-v2.xml` feed and a one-time manual installation. Original-key restoration is no longer the selected route. Build 5 embeds the new public key/feed and completed fresh app/DMG trust and local feed/archive signature checks. The private key remains in Keychain; it was not exported into reports, source or generation arguments. Owner-controlled encrypted backup is outstanding; the prepared helper has not exported it.

Use the final `Llumi-<version>-macos.dmg` for both manual distribution and compatible new-key update enclosures (`Llumi-2.2.0-macos.dmg` containing build 5 for this candidate only after completing its final gates). Sparkle officially supports DMG updates. A separate ZIP update artifact is unnecessary. Keep build 5 output separate from the superseded build 4 DMG with the same filename. Both app and DMG must be Developer ID signed, notarized and stapled before the final archive signatures are generated. Signing order is explicitly inside-out across the reviewed Sparkle helpers, framework, then Llumi. The reviewed helper entitlements are empty. The release verifier requires exact `Developer ID Application: Pranesh S (K38622WCYD)` and `TeamIdentifier=K38622WCYD`, secure timestamp and strict validity; it also checks nested publishers. Sparkle's own installer checks the host signing requirement/bundle identity. An EdDSA signature is an additional requirement, not a substitute for release verification.

## Local appcast tooling

`macos/updates/appcast.xml.in` is an unsigned review template with explicit pending date, URL, length and signature fields. Never serve it. The real feed is generated by the pinned official tool, which obtains the private key directly from Keychain. Do not hand-write signatures or copy the private key to environment variables/files.

```sh
scripts/macos/generate-appcast.sh /path/to/pinned/Sparkle/bin /path/to/local/archives
# After signing/notarization and signature generation are separately authorized:
scripts/macos/generate-appcast.sh /path/to/pinned/Sparkle/bin /path/to/local/archives --execute
```

Default is a plan only. Execution checks the official tools' checksums and confirms the Keychain public key matches the candidate before signing, plus exact final DMG name, notarized publisher, mounted app version/build/bundle ID/public key and nested code before generation. It selects the current build and avoids delta archives. The new build 5 feed records minimum update version 5; disposable private fixtures are tested separately. Output stays local. Published download URL is prepared for the eventual new GitHub release; no feed/release upload is performed. The final enclosure length, archive/feed signatures and intended release metadata are recorded in the local signed feed. Future releases require regeneration from their own final stapled bytes.

## Prepublication validation record

The following paragraphs record the earlier staged approvals and tests. Their pending gates were subsequently completed for 1.1.2; the publication receipt above records the final state. Physical Intel and Windows acceptance remain separate from this Mac release.

The raw-bucket fix was physically verified before this integration: Release Usage and compact monitor used real authenticated provider data; the unchanged source's Debug acceptance runner held the expanded native monitor on screen. The internal bucket remained parsed while the UI showed only five-hour/weekly windows. No provider authentication/account was changed. A transient Claude timeout recovered. Private captures remain outside the repository.

The initial unsigned builds and synthetic release-policy/distribution tests validated source integration and guard behavior. The owner subsequently authorized local signing and isolated updater installation tests, while withholding Apple notarization and all public release actions.

Local validation passed: 94 macOS tests, 15 distribution tests and 13 updater-policy tests; Release builds and the production privacy check also passed. A separate unsigned copy with a loopback-only test feed exposed the native Check for Updates… menu. The unsigned/malformed feed was rejected with "The update feed is improperly signed and could not be validated"; after the local server stopped, Sparkle displayed a recoverable retrieval error. This validates signed-feed enforcement and unreachable-feed handling, not successful parsing of a signed malformed feed or archive/install behavior. Additional distribution tests reject an unexpected nested helper and a changed framework symlink.

Local signed validation completed on 2026-10-03: separate bundle ID `io.github.praneshsivasankaran.llumi.sparkle.e2e`, test-only 99.1.0/build 9001 → 99.1.1/build 9002, same Developer ID publisher/team, and unchanged EdDSA public key/verification flags. A loopback-only HTTP feed was used exclusively by disposable output copies; production remains HTTPS without ATS exceptions. The signed feed offered the newer update; its signed DMG downloaded, installed and relaunched with a new PID. Exactly one Llumi test app process remained. About showed 99.1.1 and bundle metadata showed build 9002. Dark appearance, notch/menu preferences and setup state survived. Launch at Login remained off for the disposable identity. Both real providers were Ready after relaunch; Claude displayed five-hour primary and weekly detail with no raw bucket names. No provider accounts/credentials or real login registration were changed.

Native rejection tests passed for a modified DMG, invalid archive EdDSA signature, missing archive signature, unsigned feed, signed malformed XML and an unreachable server. Same and older feed builds were not offered; rejection tests left the old host build unchanged. An actually valid ad-hoc app/nested signature was rejected by the release verifier for its wrong publisher; synthetic policy tests also reject an incorrect team. These tests used locally signed, non-notarized artifacts and do not substitute for notarized production Gatekeeper acceptance. All installed 1.1.1 files matched their pre-test hashes and the app was restored afterward. Captures, provider-status evidence and test artifacts remain outside the public repository.

`generate-appcast.sh ... --stage-signed` prepares local signed metadata before Apple notarization; `--execute` still requires full notarized verification. Both modes verify the feed and enclosure using the pinned official tools, check exact URL/version/build/length and embedded notes, and emit a local release-state manifest. Staged metadata explicitly requires regeneration after stapling. Source branch pushes are authorized separately; `main`, public tags/releases, website downloads and production appcast remain unchanged. Physical Intel and Windows acceptance remain separate gates.

The actual 1.1.2/build 2 universal Release candidate passed a clean build, 94 macOS tests, 16 distribution tests, 13 updater-policy tests, the production privacy check and file hygiene checks. Local signing used the approved Developer ID identity/team with Hardened Runtime and secure timestamps for Llumi and explicit nested Sparkle targets. The app and staged DMG passed strict signature and contained-product verification. Official tooling generated and verified the staged feed and archive signatures. The final appcast path rejected the non-notarized candidate. This candidate is ready for the separately authorized Apple notarization stage; nothing was submitted, stapled, installed over public 1.1.1 or published. Final Gatekeeper acceptance and release-byte signatures remain dependent on notarization/stapling.
