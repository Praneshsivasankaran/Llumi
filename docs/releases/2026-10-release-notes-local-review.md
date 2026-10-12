# Release Notes and update-flow local review

Historical design review prepared 5 October 2026. The browser simulation described below was subsequently removed at the owner’s direction; the current native implementation and validation are documented in [macOS updates](../macos/UPDATES.md) and the [2.2.0/build 5 candidate record](2026-10-macos-background-alerts-candidate.md). Current review builds contain release history and notes only. This record is not installation guidance for 2.2.0: existing 1.1.3 users require the [one-time manual migration](../macos/UPDATES.md#authorized-220-manual-migration). The owner subsequently authorized the Mac release, website and new feed; see the [2.2.0 publication receipt](2026-10-macos-2.2.0.md). The local-only restrictions below describe the historical design-review checkpoint.

At this design-review checkpoint, the website added a Release Notes tab, a published-release archive and permanent version pages. Notes were structured in `site/releases.json`; public 1.1.2 copy and date were drawn from its signed appcast and release record. The 1.1.3 notes were an unreleased preview with no publication date or public download.

At that checkpoint, ordinary builds included five public pages and excluded preview notes and simulations. Explicit `--include-review` builds included seven pages, adding the 1.1.3 notes and interactive update-flow prototype. Review output carried no-index metadata and could not be reused as ordinary output without starting in a fresh directory. Nested routes retained working relative assets and navigation. Public downloads and signed appcast bytes were preserved. These historical page counts and prototype behavior do not describe current review output.

## Historical flow selected by the owner

- Automatic: opt in, check and download automatically, then ask to restart with **Restart and update** or **Later**. Later keeps the current version and shows no success confirmation.
- Manual: check for updates, view the exact version's notes, download, install and relaunch.
- Both: only a successful installed-version launch leads to **The app has been updated**, the installed version, **View release notes** and **Continue**. The link opens that exact version's page. Dismissal suppresses the popup on ordinary relaunch.

The interactive browser flow was an explicit simulation. State stayed in memory; it performed no download, installation, updater-preference change or external request. First installation, ordinary launch, cancellation and failure did not claim successful updates. Reducer tests and browser interactions established prototype behavior, not physical Sparkle installation or cross-launch persistence in the app.

## Historical prototype validation

- 17 static-site tests passed, including preview exclusion, nested links, escaping, invalid metadata, policy parity and byte-identical signed feed.
- 14 flow reducer regressions and three routing tests passed.
- Existing website validation passed in Chromium, WebKit and Firefox, with HTML validation, accessibility, navigation and responsive checks at 1440, 1024, 768, 390 and 320 pixels.
- Focused flow browser validation passed 158 checks in the same three engines at 1440, 390 and 320 pixels. No accessibility violations, JavaScript errors or external requests. Both flows, Later, cancellation/failure, one-time dismissal, popup keyboard focus and exact local patch-note navigation were verified.
- Production privacy audit and `git diff --check` passed. Desktop/mobile archive, notes, manual update, restart-ready and completion visuals were captured and inspected.

## Native integration required at the historical checkpoint

The running 1.1.3/build 3 preview and installed 1.1.2/build 2 were preserved byte for byte; no native source changes or rebuild were part of this design preview. At that checkpoint, the native versions allowed automatic checks but disabled automatic installation, and the scheduled-check coordinator performed information-only probes. Website content could not change that behavior. Later native implementation and acceptance supersede this checkpoint; see [macOS updates](../macos/UPDATES.md). The following requirements are preserved as historical design requirements.

The planned native implementation needed to expose accurate opt-in installation behavior, preserve manual checking, prompt before restarting, record the pending target build, and verify the newly running build before presenting a once-per-version acknowledgment. A successful check callback was not installation evidence. The first 1.1.2 upgrade remained under that old binary's manual flow and had no Llumi completion baseline; its bootstrap acknowledgment required a verified policy and separate physical test. Offline notes, first install, canceled/failed upgrade, downgrade and ordinary startup required native coverage.

The historical plan required a separately approved Mac-only upgrade test after design acceptance and before public rollout, with approved version notes published and verified before exposing a signed feed linking to them. Native installation, website deployment, GitHub publication and feed promotion were separate release gates. No public release or email distribution was performed as part of this design review; Windows was outside that work.

## Preview the current release history and notes locally

```sh
python3 scripts/build-site.py --include-review --output .review/release-notes-site
python3 -m http.server 4174 --bind 127.0.0.1 --directory .review/release-notes-site
```

Open `/releases/` and `/releases/macos/2.2.0/` on that loopback server. The browser simulation route/assets/tests have been removed. Current website validation uses `scripts/test-site.py` and `site/tests/check.cjs`; native update regressions and screenshots are in the macOS test target.
These commands preview history and notes only; they do not reproduce the removed
prototype or its historical validation results.
