# Release and maintenance checklist

This checklist applies to new versions. [Launch baselines](releases/launch-baselines.json) are immutable records, not build targets. A repository commit, passing CI, release draft, certification and public publication are separate gates.

## Plan and source

- [ ] Triage reports with exact platform/version/channel, reproduction and sanitized evidence. Use [the Windows backlog](windows/NEXT-UPDATE.md); do not invent readings or defect confirmations.
- [ ] Start behavior changes in `docs/product-spec`; document intentional macOS/Windows differences.
- [ ] Select new app/build/package versions. Windows package version must exceed 2.0.2.0 and any later accepted Store package. Do not bump frozen artifacts or move tags.
- [ ] Preserve provider authentication ownership, account continuity, isolated subprocess directories, bounded cleanup, privacy and explicit unavailable states.
- [ ] Run affected platform tests, production privacy audit, public-tree hygiene and dependency/license/inventory guards. Record exact commands, totals, source commit and toolchain.

## macOS release owner

- [ ] Build the new universal app on the Mac from the accepted commit; confirm bundle ID, version/build and actual minimum OS.
- [ ] Perform physical provider, notch/hover, appearance, setup, login-item and quit/relaunch acceptance. Record hardware actually tested; universal output alone is not Intel acceptance.
- [ ] Review dependencies/entitlements; Developer ID sign with Hardened Runtime and secure timestamp. Notarize and staple app and DMG, then validate mount, signatures, staple and Gatekeeper.
- [ ] Hash the final stapled DMG. Upload that exact DMG and original sidecar to one GitHub **draft** with a new matching source tag. Keep private signing reports out of public assets.
- [ ] Have the receiving session download from GitHub and match hash, filename, size, tag and source. Publish only after explicit authorization, then perform anonymous website → GitHub → DMG hash verification. See [handoff](RELEASE-HANDOFF.md).

## Windows release owner

- [ ] Choose Store or direct distribution explicitly. Store artifacts are not GitHub downloads; direct release requires every [distribution gate](DISTRIBUTION.md).
- [ ] Keep product ID, publisher, Microsoft identity, Application ID, `AgentMeterStartup`, `runFullTrust` and Desktop family stable. Derive future MSIX structure from the accepted package; use a strictly greater package version.
- [ ] Hash and inventory the exact package. Verify runtime handling, dependencies, notices, no bundled provider SDK/CLI/Node, Raspberry assets and all saved listing fields.
- [ ] Test both legacy/Llumi launch orders without weakening mutexes. Quit via the tray before suites and installations; preserve settings and provider credentials during upgrades.
- [ ] Perform clean install, upgrade, startup, provider checks, compact/hover/appearance/setup, quit/relaunch, uninstall/reinstall and installed-version verification. Record offered Store version separately from an old installed version.
- [ ] Preserve existing package for metadata-only changes. Review before/after fields, discoverability, pricing and publishing hold; require explicit certification and publication authorization separately.
- [ ] After publication, verify anonymous Store name, screenshots and actual Website/Privacy/Support hrefs. A published dashboard is not proof of public propagation.

## Website and closeout

- [ ] Keep download controls disabled until their release/channel gates pass and activation is authorized. Use actual public asset URLs returned by GitHub; preserve SHA-256 and source in config.
- [ ] Verify HTTPS apex, HTTP/www redirects, desktop/mobile layout, keyboard access, no unexpected requests, privacy text parity, Support/Security/License/GitHub links and no current AgentMeter branding.
- [ ] Run site build/tests, routing tests and hygiene/privacy checks; deploy only with authorization. Verify the deployed result and final platform download/install round-trip.
- [ ] Run `npm run --prefix site/tests test:production-privacy` against the live canonical site after deployment or hosting analytics changes. Require clean raw HTML and fresh-browser network checks on home/privacy/support, including delayed and pagehide traffic; local-only browser validation does not establish production privacy.
- [ ] Archive sanitized release facts in docs and private raw evidence in the approved local/private archive. Record hashes, commands, timestamps, limitations and unresolved gates. Never commit credentials, raw provider output or internal reports.
- [ ] Compare accepted binaries and tags again: no changes to previous releases. Update docs/security support statements and backlog, commit using existing human identity, and push only within authorized scope.
- [ ] Treat Store discoverability, historical URL retirement, new channels and security runtime updates as explicit follow-up decisions; never silently change them during metadata cleanup.
