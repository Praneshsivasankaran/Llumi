# macOS updates

## Local 1.1.3 candidate

Version 1.1.3/build 3 adds Updates in Settings, a signed-feed information probe on foreground launch/reopen, and an available-update banner. New and existing users enable automatic checking once in this version; subsequent changes use Sparkle's persisted setting directly. Setup gates updater startup. Login launch stays quiet. Probes do not show no-update/network-error UI; manual checks and installation use Sparkle's standard UI. Scheduled checks redirect to signed information probes and surface the same banner without retaining an installation session after Later. Silent installation and system profiling remain disabled.

The candidate remains local until the owner reviews it and approves publication. The installed public 1.1.2 must stay recoverable. A private Mac-only test can temporarily override the host's `SUFeedURL` through its user defaults to a loopback server with a signed feed/archive. This is Sparkle's supported testing override; preserve and restore the previous default, retain all verification flags, and never deploy that feed publicly or modify the installed host's signed bundle. Complete the private 1.1.2 → 1.1.3 install/relaunch acceptance before seeking public release approval. Notarization and publication receipts are separate from a local Developer ID signed build.

## Published 1.1.2 baseline

macOS 1.1.2/build 2 was published on 3 October 2026. The signed production feed and website download are live, and the final public app was installed and physically verified on the owner's Mac. See the [publication receipt](../releases/2026-10-macos-1.1.2.md). Historical 1.1.1/build 1, its tag and DMG remain frozen. `macos/release-candidate.json` now records the local 1.1.3/build 3 candidate; each candidate must advance version and build and match bundle metadata. This file is separate from the immutable launch baseline.

Existing 1.1.1 users must install 1.1.2 manually once; compatible later macOS updates can then be delivered in-app. Windows Store 2.0.2 is unchanged. The Claude presentation fix remains its own cross-platform commit, `c9a77c5`; Windows native UI regression execution is required before another Windows package.

## Integration and behavior

Sparkle 2.10.0 is pinned exactly through Swift Package Manager, with its resolved commit and upstream binary checksum recorded. The standard `SPUStandardUpdaterController` targets the main app bundle. Application menu and Settings → Check for Updates… use its standard UI and availability validation. Startup waits for guided setup completion. Version 1.1.3 enables automatic checks once and exposes the persisted choice in Settings; silent automatic downloading/installing is disabled. No custom install UI or version comparator is introduced.

Production feed: `https://tryllumi.com/appcast.xml`. The final signed 1.1.2 feed is deployed and verified against the published DMG. Feed and release notes require Sparkle signatures; signature failure expiration is disabled. Downloaded archives are verified before extraction. System profiling is disabled, and the updater delegate returns no allowed profile keys even if an inherited Sparkle preference enables profile submission. No Llumi/provider data is attached to update requests.

Official references: [setup](https://sparkle-project.org/documentation/), [programmatic controller](https://sparkle-project.org/documentation/programmatic-setup/), [consent/security options](https://sparkle-project.org/documentation/customization/), [publishing](https://sparkle-project.org/documentation/publishing/), [stable release](https://github.com/sparkle-project/Sparkle/releases/tag/2.10.0).

## Signing material and artifact

A dedicated Sparkle EdDSA key was generated with upstream `generate_keys --account io.github.praneshsivasankaran.llumi.sparkle`. The private key stays in login Keychain, under Sparkle's service and that account; it was never exported, printed, committed or uploaded. Only the public key is embedded and recorded in the candidate manifest.

Use the final `Llumi-1.1.2-macos.dmg` for both manual distribution and the update enclosure. Sparkle officially supports DMG updates. A separate ZIP update artifact is unnecessary. Both app and DMG must be Developer ID signed, notarized and stapled before the final archive signatures are generated. Signing order is explicitly inside-out across the reviewed Sparkle helpers, framework, then Llumi. The reviewed helper entitlements are empty. The release verifier requires exact `Developer ID Application: Pranesh S (K38622WCYD)` and `TeamIdentifier=K38622WCYD`, secure timestamp and strict validity; it also checks nested publishers. Sparkle's own installer checks the host signing requirement/bundle identity. An EdDSA signature is an additional requirement, not a substitute for release verification.

## Local appcast tooling

`macos/updates/appcast.xml.in` is an unsigned review template with explicit pending date, URL, length and signature fields. Never serve it. The real feed is generated by the pinned official tool, which obtains the private key directly from Keychain. Do not hand-write signatures or copy the private key to environment variables/files.

```sh
scripts/macos/generate-appcast.sh /path/to/pinned/Sparkle/bin /path/to/local/archives
# After signing/notarization and signature generation are separately authorized:
scripts/macos/generate-appcast.sh /path/to/pinned/Sparkle/bin /path/to/local/archives --execute
```

Default is a plan only. Execution checks the official generator's checksum, exact final DMG name, notarized publisher, mounted app version/build/bundle ID/public key and nested code before generation. It selects the current build, avoids delta archives and requires an updater host build of at least 2. Output stays local. Published download URL is prepared for the eventual new GitHub release; no feed/release upload is performed. The final enclosure length, archive/feed signatures and release date are recorded in the published feed. Future releases require regeneration from their own final stapled bytes.

## Prepublication validation record

The following paragraphs record the earlier staged approvals and tests. Their pending gates were subsequently completed for 1.1.2; the publication receipt above records the final state. Physical Intel and Windows acceptance remain separate from this Mac release.

The raw-bucket fix was physically verified before this integration: Release Usage and compact monitor used real authenticated provider data; the unchanged source's Debug acceptance runner held the expanded native monitor on screen. The internal bucket remained parsed while the UI showed only five-hour/weekly windows. No provider authentication/account was changed. A transient Claude timeout recovered. Private captures remain outside the repository.

The initial unsigned builds and synthetic release-policy/distribution tests validated source integration and guard behavior. The owner subsequently authorized local signing and isolated updater installation tests, while withholding Apple notarization and all public release actions.

Local validation passed: 94 macOS tests, 15 distribution tests and 13 updater-policy tests; Release builds and the production privacy check also passed. A separate unsigned copy with a loopback-only test feed exposed the native Check for Updates… menu. The unsigned/malformed feed was rejected with "The update feed is improperly signed and could not be validated"; after the local server stopped, Sparkle displayed a recoverable retrieval error. This validates signed-feed enforcement and unreachable-feed handling, not successful parsing of a signed malformed feed or archive/install behavior. Additional distribution tests reject an unexpected nested helper and a changed framework symlink.

Local signed validation completed on 2026-10-03: separate bundle ID `io.github.praneshsivasankaran.llumi.sparkle.e2e`, test-only 99.1.0/build 9001 → 99.1.1/build 9002, same Developer ID publisher/team, and unchanged EdDSA public key/verification flags. A loopback-only HTTP feed was used exclusively by disposable output copies; production remains HTTPS without ATS exceptions. The signed feed offered the newer update; its signed DMG downloaded, installed and relaunched with a new PID. Exactly one Llumi test app process remained. About showed 99.1.1 and bundle metadata showed build 9002. Dark appearance, notch/menu preferences and setup state survived. Launch at Login remained off for the disposable identity. Both real providers were Ready after relaunch; Claude displayed five-hour primary and weekly detail with no raw bucket names. No provider accounts/credentials or real login registration were changed.

Native rejection tests passed for a modified DMG, invalid archive EdDSA signature, missing archive signature, unsigned feed, signed malformed XML and an unreachable server. Same and older feed builds were not offered; rejection tests left the old host build unchanged. An actually valid ad-hoc app/nested signature was rejected by the release verifier for its wrong publisher; synthetic policy tests also reject an incorrect team. These tests used locally signed, non-notarized artifacts and do not substitute for notarized production Gatekeeper acceptance. All installed 1.1.1 files matched their pre-test hashes and the app was restored afterward. Captures, provider-status evidence and test artifacts remain outside the public repository.

`generate-appcast.sh ... --stage-signed` prepares local signed metadata before Apple notarization; `--execute` still requires full notarized verification. Both modes verify the feed and enclosure using the pinned official tools, check exact URL/version/build/length and embedded notes, and emit a local release-state manifest. Staged metadata explicitly requires regeneration after stapling. Source branch pushes are authorized separately; `main`, public tags/releases, website downloads and production appcast remain unchanged. Physical Intel and Windows acceptance remain separate gates.

The actual 1.1.2/build 2 universal Release candidate passed a clean build, 94 macOS tests, 16 distribution tests, 13 updater-policy tests, the production privacy check and file hygiene checks. Local signing used the approved Developer ID identity/team with Hardened Runtime and secure timestamps for Llumi and explicit nested Sparkle targets. The app and staged DMG passed strict signature and contained-product verification. Official tooling generated and verified the staged feed and archive signatures. The final appcast path rejected the non-notarized candidate. This candidate is ready for the separately authorized Apple notarization stage; nothing was submitted, stapled, installed over public 1.1.1 or published. Final Gatekeeper acceptance and release-byte signatures remain dependent on notarization/stapling.
