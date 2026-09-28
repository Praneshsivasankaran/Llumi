# Official distribution

GitHub is the canonical home for Llumi's product documentation, issues, source and direct releases.

| Platform | Store channel | GitHub Releases |
| --- | --- | --- |
| macOS | Mac App Store planned; not published | [Llumi 1.1.1 signed and notarized DMG](https://github.com/Praneshsivasankaran/Llumi/releases/tag/llumi-macos-1.1.1) published |
| Windows | [Llumi 2.0.2.0 published](https://apps.microsoft.com/detail/9NV153Q5K5MQ); website download enabled | No direct Windows launch download |

Historical AgentMeter beta assets remain unchanged. The final Llumi macOS DMG passed the Mac owner's signing and notarization checks, and its anonymous public GitHub download matches the accepted SHA-256. See the [release handoff](RELEASE-HANDOFF.md) and [macOS distribution guide](macos/DISTRIBUTION.md).

## Windows direct-download requirements

A Microsoft Store submission MSIX is not automatically a direct-download artifact. Before publishing a Windows GitHub binary, review the exact proposed artifact and complete each gate:

| Gate | Required evidence |
| --- | --- |
| Final dependency and license inventory | Every shipped file, hash, version, origin and applicable license; exact runtime-pack notices if bundled |
| Redistribution rights | Verify terms for every included component, including runtime and Windows SDK projections; retain required notices |
| Provider payload exclusion | No Claude Agent SDK, `sdk.mjs`, bundled Claude/Codex executable or Node runtime; test the final payload |
| Signing and trust | Choose the direct artifact format and signing identity; verify signatures, timestamps, download provenance and Windows trust behavior |
| Clean installation | Test on a clean supported Windows system without development tools; verify launch, prerequisites, startup, uninstall and data handling |
| Updates | Define authenticated update delivery, version compatibility, data preservation, rollback and coexistence with the Store installation |
| Security and privacy | Audit the final binary and source, dependency advisories, provider isolation, logging, credential boundaries and clean-machine behavior |

These are release gates, not claims that a Windows direct release is approved. Build and CI success alone do not complete them. Keep internal audit receipts, raw diagnostics, signing credentials and Store submission material outside the public repository.

Update README download links only after a destination is publicly live. Until then, show **Coming soon**. Preserve existing releases and their status when adding a platform.
