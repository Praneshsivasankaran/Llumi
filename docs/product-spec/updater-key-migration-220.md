# macOS 2.2.0: one-time manual updater-key migration

The owner explicitly chose a new Sparkle EdDSA key, a separate future update feed
and one manual installation for this macOS update after the original Sparkle
private key could not be recovered. Local key creation, rebuild, signing,
notarization and validation are authorized. **Publication is not authorized.**
This contract supersedes any earlier plan to deliver 2.2.0 through the existing
1.1.3 Sparkle feed or to wait for the original key before preparing a candidate.

## User-visible upgrade behavior

- Installed macOS **1.1.3 cannot update in-app to the new-key 2.2.0 candidate**.
  Its trusted old public key cannot validate the new feed/archive signatures.
  Do not imply that selecting Check for Updates in 1.1.3 completes this migration.
- The transition is one manual installation of the final Developer ID signed,
  notarized and stapled 2.2.0 app from its verified DMG. Quit the existing Llumi,
  replace it at the same Applications path, then open the new app normally.
  The DMG's supported installation flow remains the app and Applications shortcut.
- Keep the same product bundle identifier, approved Developer ID publisher/team,
  application identity and preference domain. Preserve valid user preferences,
  setup completion, appearance, enabled providers, background/notch controls,
  alert thresholds/receipts and update preferences. Preserve provider-owned
  credentials and sessions. Do not ask users to delete application data or sign
  into a separate Llumi account.
- Startup remains authoritative OS registration; do not manufacture registration
  state from a copied preference. Verify startup after replacement on the final
  installed path and preserve the user's intended choice.
- The new app uses its new public key and separate feed for **future** compatible
  updates. Existing automatic-update choices remain unchanged; a manual install
  must not silently enable checking/downloading. An unpublished/unreachable new
  feed must produce a recoverable check failure, not block app operation.
- Preserve the existing truthful update-completion contract. A copy, a successful
  check/download, or a changed version by itself is not verified updater
  installation evidence. Do not create a synthetic Sparkle success receipt or
  show its completion popup merely to make a manual migration look automatic.

## Separate trust and feed boundaries

The selected migration metadata is:

| Field | Value |
| --- | --- |
| Marketing version | `2.2.0` |
| macOS internal build | `5` |
| New future feed | `https://tryllumi.com/appcast-v2.xml` |
| New Sparkle Keychain account | `io.github.praneshsivasankaran.llumi.sparkle.v2` |
| Preserved old public feed | `https://tryllumi.com/appcast.xml` |

Generate the new key in the selected secure account and record its public key in
release metadata before rebuilding. Verify that the final bundle and generation
tooling agree. Keep the private key only in the approved secure store; do not write
it to source, command arguments, logs, reports or release artifacts. These selected
identifiers do not claim that key generation, builds or validation have completed.

The old public appcast, its signature, existing release/download records and old
application bytes remain intact. Do not replace the old feed with one signed by
the new key, point old clients at the new feed, or silently republish an existing
release asset. The separate new feed is staged locally until explicit publication
authorization. New-key artifacts must not inherit the old feed's trust claims.

Developer ID signing and Sparkle EdDSA signing protect different boundaries. Keep
the approved bundle/publisher/team and verify compatibility with the installed
app's designated requirement. This does not make the old Sparkle public key accept
a different EdDSA key. Preserve signed-feed/archive verification, nested-code
verification, Hardened Runtime, notarization, Gatekeeper and downgrade/version
guards. Do not add a fallback that ignores signatures, disables validation, trusts
arbitrary keys, or weakens transport/redirect restrictions to complete migration.

## Fresh artifact and acceptance requirements

Changing the embedded public key or feed changes the candidate. Preserve previous
2.2.0 artifacts and receipts as historical evidence; build new bytes from the
updated, recorded source. Marketing version remains **2.2.0**, with macOS internal
build advancing from 4 to **5**. Validate all release metadata consistently. Earlier signing, notarization,
checksums, appcast signatures and native acceptance do not transfer to new bytes.

1. Test release metadata and updater policy against the selected new key/feed,
   including rejection of an unexpected key, old/wrong feed, invalid signatures,
   wrong publisher/team and non-advancing update build. Run affected native
   regression tests and production source/bundle privacy checks.
2. Build the new universal Release. Verify bundle identity, marketing/internal
   versions, public key, feed URL, approved nested-code inventory and absence of
   local-review/test-feed metadata in the production app.
3. Sign each reviewed code target, notarize and staple the app, then create, sign,
   notarize and staple the new final DMG. Verify Gatekeeper, strict signatures and
   the app mounted from that final DMG. Generate final checksums only afterward.
4. Generate and verify local new-key feed/archive signatures with the official
   pinned Sparkle tools. Verify exact enclosure URL, version/build, size, hash and
   intended future feed path. No generated metadata is public until authorized.
5. Validate one manual replacement from a recoverable copy of the existing 1.1.3
   installation at its real path. Compare valid settings before/after, preserve
   provider sessions, verify one running instance, version, live providers,
   background/Open/close/Quit, notifications and authoritative startup. Restore
   any temporary test settings. Keep unperformed login-session checks explicit.
6. Validate future new-key in-app updating using an isolated signed test host and
   newer signed fixture on a private loopback feed. The fixture may use test-only
   versions, but the new key, signature guards and installation behavior must
   match production. Preserve/restore feed preferences and remove test fixtures.
   This proves the new trust chain; it is **not** a 1.1.3-to-2.2.0 in-app upgrade.

Report implemented behavior, synthetic regression results, final-artifact trust,
manual installation acceptance and future updater acceptance separately. Keep
unverified native/device cases visible. Retain exact source/artifact provenance;
never report earlier candidate hashes or screenshots as new-key evidence.

## Platform and publication limits

Windows retains the shared marketing version **2.2.0** and the existing background,
alert and appearance behavior contract. Native internal/package build numbers and
Store/update mechanisms remain independent. This macOS Sparkle migration neither
changes Windows distribution nor establishes any Windows implementation, test,
signing, Store or device result.

No release upload, public feed/website/download change, notification to users,
automatic update delivery or other publication is authorized here. Release notes
must explain the manual-install requirement before future publication; public
availability must not be claimed while the new feed and candidate remain local.
