# Windows release candidate

The user physically accepted commit `ee0e0fb263b78d1117118f4f98473a7539c78446` on 2026-09-23 after 607 automated tests passed. The reported PASS covers Llumi/Raspberry branding, real Codex and Claude usage, Claude five-hour compact and weekly details, Light/Dark, onboarding, sanitized diagnostics, lifecycle/single-instance behavior, integrations and startup. This is user-reported physical acceptance, separate from packaging and distribution acceptance.

The next Windows version is **2.0.2**, with file/MSIX version **2.0.2.0**, greater than historical Store candidate 2.0.1.0. This packaging pass does not change provider behavior or UI.

## Direct download

Use a per-user Inno Setup EXE containing the self-contained win-x64 publish. No admin elevation or separate .NET installation is required. The installer creates a Start menu shortcut and an uninstall entry; startup is optional and preserved on Llumi upgrades. Uninstall retains preferences and does not touch provider credentials or installations. New installs use the Llumi directory and identity; older AgentMeter installs remain untouched, with allowlisted preferences imported by the application. Only the exact known legacy startup command is removed to avoid duplicate startup. The shared legacy mutex prevents concurrent provider services across old/new/Store copies.

Framework-dependent output is smaller but requires .NET 10 Desktop Runtime x64. Self-contained output is larger and must be rebuilt when its runtime needs security updates. Measure both from the same source/runtime using `windows/tools/package-direct.ps1`. An x64 installer is not an Arm64 release. Minimum installer OS is Windows 10 build 19045, matching the recovered Store baseline; validate on clean supported systems before public distribution.

Updates are manual downloads from the eventual official release channel; no updater is introduced. Keep the stable Llumi installer identity and preserve preferences. Do not distribute a downgrade as an upgrade. Do not interpret a successful install on a developer PC as validation of an OS without SDKs/runtimes.

## Store preparation

The historical package is immutable. Its SHA-256 is `5cd7219626334f2312fbbebe7b5540dbd57b4df9a5ba8a586589a9d131486bcd`.

- Microsoft identity: `PraneshS.AgentMeterforWindows`
- Store ID: `9NV153Q5K5MQ` (external listing identity; not a manifest field)
- Publisher: `CN=8971912E-75C4-4D4B-8B53-3814AB80FF0F`
- Application ID: `AgentMeter`; startup task: `AgentMeterStartup`
- Architecture: x64; capability: `runFullTrust`; packaged classic/full-trust medium-IL app
- Minimum OS: `10.0.19045.0`; maximum tested: `10.0.26200.0`

Derive the new manifest from that hash-verified package with `windows/tools/prepare-store.ps1`. Change only version, customer-facing branding and executable references to Llumi; regenerate the same resource scales from approved Raspberry artwork. Keep Store output separate from the direct installer. Local preparation does not submit, sign, certify or validate the installed Store startup contract.

## Signing and publication gates

A locally trusted test certificate is not public code-signing trust. Do not use it to present the direct installer as production-signed. Public release requires an appropriate signing identity, trusted/timestamped binaries and installer, clean-machine lifecycle/trust validation, final dependency review and explicit publication authorization. Neither package is published by these scripts. See [distribution requirements](../DISTRIBUTION.md).

References: [Microsoft deployment models](https://learn.microsoft.com/en-us/dotnet/core/deploying/), [Store package version rules](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements).
