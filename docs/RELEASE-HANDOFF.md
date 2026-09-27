# Release handoff through GitHub

GitHub is the common source of truth between macOS and Windows release sessions. Machine-local paths are not release identifiers. Record the repository, exact source commit, tag, release ID, asset IDs, filenames, sizes and SHA-256 values.

## macOS production artifact

The owner reports the final Llumi 1.1.1 signed, notarized and stapled artifact exists on the Mac. Transfer that exact `Llumi-1.1.1-macos.dmg` and its existing SHA-256 sidecar to a **draft** release in `Praneshsivasankaran/Llumi`. Do not rebuild, replace accepted bytes or generate substitute signing evidence on another machine.

Before creating the release, compare the release receipt's source commit with the proposed tag. The historical `macos-1.1.1` tag points to the pre-Llumi AgentMeter source `44d4b7e106b2ecd5d4c73be2bc74601449f45063`; preserve it. The Llumi tag must be confirmed against the final Mac receipt, rather than moving that historical tag or assuming current main built the DMG.

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

Microsoft Store is the launch distribution channel. Submission 2 remains under manual publishing hold. A passing certification does not authorize Publish now. Keep the Windows website CTA disabled until Llumi itself is published; do not present the historical AgentMeter listing or unsigned direct installer as Llumi.

## Independent gates

Source reconciliation and CI, draft release preparation, website deployment, GitHub release publication, public-download verification and Microsoft publication are distinct actions. Preserve accepted tags, releases and asset bytes at every stage.
