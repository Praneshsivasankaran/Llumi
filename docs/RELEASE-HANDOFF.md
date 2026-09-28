# Release handoff through GitHub

GitHub is the common source of truth between macOS and Windows release sessions. Machine-local paths are not release identifiers. Record the repository, exact source commit, tag, release ID, asset IDs, filenames, sizes and SHA-256 values.

## Verified current GitHub handoff

- Canonical repository: `Praneshsivasankaran/Llumi`
- Public release: [Llumi 1.1.1 for macOS](https://github.com/Praneshsivasankaran/Llumi/releases/tag/llumi-macos-1.1.1), ID `397602229`, published 2026-09-27
- Tag: `llumi-macos-1.1.1`, source `af2199b572a8b2bb4273664ed38d87476a21eca1`
- Assets: `Llumi-1.1.1-macos.dmg` and its original `.sha256` sidecar
- DMG SHA-256: `21724ba1b9255c5cbf56003422abe4f2576c3ce8bd8ef599dce769a9766c808c`

The Windows receiving session downloaded the authenticated draft assets from GitHub and independently matched the DMG hash to the original sidecar, GitHub asset digest and owner-provided expected hash on 2026-09-27. The DMG was not modified or rebuilt.

The Mac owner reports independent verification of Llumi 1.1.1 build 1, bundle ID `io.github.praneshsivasankaran.llumi`, universal arm64/x86_64, Developer ID team `K38622WCYD`, Hardened Runtime, secure timestamp, zero entitlements, accepted notarization, a valid staple and Gatekeeper acceptance as Notarized Developer ID. These Apple checks were performed on the Mac, not rerun on Windows. Internal signing evidence remains private.

The owner explicitly authorized publication of the existing draft on 2026-09-27. Its two original assets and tag were preserved. The receiving Windows session then downloaded both public assets without authentication: HTTP 200, the expected filename, 1,967,043 DMG bytes, and the same SHA-256 and original sidecar. `site/config.json` now uses the actual public asset URL returned by GitHub and preserves the tag, source and checksum. Apple-tool checks were not rerun on Windows.

## macOS production artifact

The final Llumi 1.1.1 signed, notarized and stapled artifact was transferred from the Mac through the GitHub draft and is now public. For future releases, transfer the exact accepted DMG and existing SHA-256 sidecar to a **draft** release in `Praneshsivasankaran/Llumi`. Do not rebuild, replace accepted bytes or generate substitute signing evidence on another machine.

Before creating the release, compare the release receipt's source commit with the proposed tag. The historical `macos-1.1.1` tag points to the pre-Llumi AgentMeter source `44d4b7e106b2ecd5d4c73be2bc74601449f45063`; preserve it. The new `llumi-macos-1.1.1` tag matches the confirmed Llumi source above. Future releases must likewise match the final Mac receipt, rather than moving historical tags or assuming current main built the DMG.

Use the title **Llumi 1.1.1 for macOS**. Notes cover Codex and Claude Code, contextual notch monitoring, allowances and resets, guided setup, Light/Dark/System and local privacy architecture. Only the final DMG and checksum sidecar belong in public release assets. Do not upload private signing receipts, credentials, raw logs or internal reports to a release that will become public.

The Mac session must supply a sanitized verification summary tied to the DMG hash: source commit, version/build, bundle ID, universal arm64/x86_64, Developer ID validity, notarization acceptance, staple validation and Gatekeeper acceptance. Retain detailed `Release-Report.md`, `release-evidence.json` and signing evidence in an access-controlled GitHub evidence store if one is explicitly configured; this public repository must not acquire private evidence automatically. Neither a filename nor a checksum alone proves notarization.

## Receiving session

1. Inspect GitHub release metadata and verify it is the intended draft, tag and source commit.
2. Download the exact DMG and its original sidecar with authenticated `gh release download`, into ignored local artifacts.
3. Hash the received DMG and compare it with the sidecar, GitHub asset digest and the Mac's sanitized verification summary. Stop on mismatch; do not regenerate a sidecar to conceal one.
4. Verify signatures, staple, architecture, mount and Gatekeeper on macOS. Windows can verify bytes but cannot substitute for Apple's tooling.
5. Keep `site/config.json` download URLs null until the release is published under a separate explicit authorization.

Draft downloads are authenticated evidence checks, not a public distribution round-trip. After authorized publication, download anonymously through the exact website CTA, recheck the hash, then mount and run Apple's verification tools on a Mac. Do not claim this gate passed while the release is a draft.

## Windows

Microsoft Store is the launch distribution channel. The owner published package Submission 2 and metadata-only Submission 3. Published Submission 3 preserves 2.0.2.0 and saves the tryllumi.com Website, Privacy and Support URLs. Corrected public metadata was verified on 2026-09-28. The owner subsequently confirmed Store installation and physical checks and authorized website activation. The installed package independently reports 2.0.2.0 with Store signature and OK status. The website now links to the same product. Future release activations retain these separate metadata, acceptance and authorization gates. See the [frozen launch record](releases/2026-09-launch.md).

## Independent gates

Source reconciliation and CI, draft release preparation, website deployment, GitHub release publication, public-download verification and Microsoft publication are distinct actions. Preserve accepted tags, releases and asset bytes at every stage.
