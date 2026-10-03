# Llumi 2.0.3 Windows patch preparation

The application version is **2.0.3** and the file/MSIX version is **2.0.3.0**.
This patch includes the reviewed Claude consumer presentation fix from
`c9a77c5f4e7e7e36837f251e67f1ac6dba35768a`. See the shared
[usage specification](../product-spec/usage.md): five-hour is primary, weekly is
supplementary, and unrecognized buckets remain internal. Codex is unchanged.
The existing published macOS 1.1.2 release is not replaced by this Windows patch.

Both Windows package producers reject versions at or below the Windows
`package_version` in the immutable [launch baseline](../releases/launch-baselines.json).
They require a four-component package version with revision zero. Run
`windows/tools/test-release-version.ps1` for guard regressions; the Windows build
also runs them. Frozen release records and package provenance remain historical.

The Store candidate must retain product `9NV153Q5K5MQ`, package identity
`PraneshS.AgentMeterforWindows`, application `AgentMeter`, startup task
`AgentMeterStartup`, `runFullTrust`, x64, Windows.Desktop minimum `10.0.19045.0`,
and the self-contained runtime. Physical verification with existing provider
accounts precedes packaging. Complete automated, privacy, inventory, license and
safe upgrade verification before certification submission. A local build or
synthetic render does not establish physical or Store upgrade acceptance.

Keep existing Store metadata and the manual publishing hold. Stop at **Publish
now** for owner authorization. Do not publish this MSIX as a GitHub release asset.

## Store release notes

Llumi 2.0.3

- Fixed an issue where internal Claude usage bucket names could appear in the interface.
- Improved Claude usage presentation consistency.
