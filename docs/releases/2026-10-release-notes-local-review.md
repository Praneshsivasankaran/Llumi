# Release Notes and update-flow local review

Historical design review prepared 5 October 2026. The browser simulation described below was subsequently removed at the owner’s direction; the current native implementation and validation are documented in docs/macos/UPDATES.md. Current review builds contain release history and notes only.

Prepared for owner design review. The website adds a Release Notes tab, a published-release archive and permanent version pages. Notes are structured in `site/releases.json`; public 1.1.2 copy and date are drawn from its signed appcast and release record. The 1.1.3 notes are an unreleased preview with no publication date or public download.

Ordinary builds include five public pages and exclude preview notes and simulations. Explicit `--include-review` builds include seven pages, adding the 1.1.3 notes and interactive update-flow prototype. Review output carries no-index metadata and cannot be reused as ordinary output without starting in a fresh directory. Nested routes retain working relative assets and navigation. Public downloads and signed appcast bytes are preserved.

## Flow selected by the owner

- Automatic: opt in, check and download automatically, then ask to restart with **Restart and update** or **Later**. Later keeps the current version and shows no success confirmation.
- Manual: check for updates, view the exact version's notes, download, install and relaunch.
- Both: only a successful installed-version launch leads to **The app has been updated**, the installed version, **View release notes** and **Continue**. The link opens that exact version's page. Dismissal suppresses the popup on ordinary relaunch.

The interactive browser flow is an explicit simulation. State stays in memory; it performs no download, installation, updater-preference change or external request. First installation, ordinary launch, cancellation and failure do not claim successful updates. Reducer tests and browser interactions establish prototype behavior, not physical Sparkle installation or cross-launch persistence in the app.

## Validation

- 17 static-site tests passed, including preview exclusion, nested links, escaping, invalid metadata, policy parity and byte-identical signed feed.
- 14 flow reducer regressions and three routing tests passed.
- Existing website validation passed in Chromium, WebKit and Firefox, with HTML validation, accessibility, navigation and responsive checks at 1440, 1024, 768, 390 and 320 pixels.
- Focused flow browser validation passed 158 checks in the same three engines at 1440, 390 and 320 pixels. No accessibility violations, JavaScript errors or external requests. Both flows, Later, cancellation/failure, one-time dismissal, popup keyboard focus and exact local patch-note navigation were verified.
- Production privacy audit and `git diff --check` passed. Desktop/mobile archive, notes, manual update, restart-ready and completion visuals were captured and inspected.

## Native integration still required

The running 1.1.3/build 3 preview and installed 1.1.2/build 2 were preserved byte for byte; no native source changes or rebuild were part of this design preview. Current versions allow automatic checks but disable automatic installation. The current scheduled-check coordinator performs information-only probes. Website content cannot change that behavior.

The next native implementation must expose accurate opt-in installation behavior, preserve manual checking, prompt before restarting, record the pending target build, and verify the newly running build before presenting a once-per-version acknowledgment. A successful check callback is not installation evidence. The first 1.1.2 upgrade remains under that old binary's manual flow and has no Llumi completion baseline; its bootstrap acknowledgment requires a verified policy and separate physical test. Offline notes, first install, canceled/failed upgrade, downgrade and ordinary startup need native coverage.

After design acceptance, test a separately approved Mac-only upgrade before any public rollout. Publish and verify an approved version's notes before exposing a signed feed that links to them. Native installation, website deployment, GitHub publication and feed promotion remain separate release gates. No public release or email distribution was performed here; Windows remains parked.

## Reproduce the local preview

```sh
python3 scripts/build-site.py --include-review --output .review/release-notes-site
python3 -m http.server 4174 --bind 127.0.0.1 --directory .review/release-notes-site
```

Open `/releases/` and `/releases/macos/1.1.3/` on that loopback server. The browser simulation route/assets/tests have been removed. Current website validation uses `scripts/test-site.py` and `site/tests/check.cjs`; native update regressions and screenshots are in the macOS test target.
