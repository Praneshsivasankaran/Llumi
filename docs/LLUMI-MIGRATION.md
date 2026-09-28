# Llumi migration and historical compatibility

Llumi is the canonical product and repository name. macOS 1.1.1 and Windows 2.0.2.0 are published frozen baselines. Existing AgentMeter releases, tags, screenshots and signing receipts remain historical; they do not describe current Llumi binaries.

## Upgrade

In AgentMeter, disable Launch at Login / Start with Windows, then quit. Install the published Llumi release through its verified platform channel. Re-enable startup from Llumi if desired. Do not run both apps. No privileged or database cleanup is necessary. An old login item may need removal through the old app or the normal OS settings.

macOS uses `io.github.praneshsivasankaran.llumi`. Allowlisted migration reads `io.github.praneshsivasankaran.agentmeter`, then `local.agentmeter.mac`. New valid values win; appearance, notch/menu visibility and setup completion are the only migratable fields. Old domains and files are retained. Registration is always read from `SMAppService.mainApp`.

Windows portable preferences migrate from the historical `%LOCALAPPDATA%\AgentMeter` to `%LOCALAPPDATA%\Llumi` with bounded, type-validated parsing. Packaged LocalState stays with its original package identity. The Store-assigned `PraneshS.AgentMeterforWindows`, product `9NV153Q5K5MQ`, and packaged startup task `AgentMeterStartup` are retained. The accepted 2.0.2.0 MSIX retains these values; metadata changes must not alter them.

## Source paths and compatibility

Source project paths and C# namespaces still use AgentMeter to avoid unrelated code churn. Built application products are Llumi. Mac and Windows hold their legacy single-instance locks alongside the new identity to prevent duplicate background services. Legacy locks do not authorize deleting preferences or startup entries.

## Intentional remaining AgentMeter references

| Location | Reason to preserve |
| --- | --- |
| Xcode project/scheme, source folders, C# namespaces and solution/test names | Engineering identity; renaming is unrelated to launch documentation and could affect builds |
| Mac preference domains and old/new single-instance locks | Allowlisted migration and duplicate-service prevention |
| Windows Microsoft identity, Application ID, startup task and legacy mutex | Store update continuity and mutual exclusion |
| Historical environment variable aliases | Existing user setup compatibility |
| `assets/screenshots/`, historical demo, Git tags and releases | Original evidence; explicitly historical, never relabeled as Llumi |
| Case-sensitive GitHub Pages `/AgentMeter/` routes | Existing Store/installed-client links during transition; retain until a separate retirement decision |
| Synthetic migration, path and rejected-download fixtures | Tests of backward compatibility and rejection of historical artifacts |

Current clone, issue, support, security, product and download links use `Praneshsivasankaran/Llumi` and `tryllumi.com`. The repository rename, Raspberry branding and release signing/publication are complete. Public Store URL propagation and the website Windows activation remain separate gates. See [launch record](releases/2026-09-launch.md) and [maintenance checklist](RELEASE-CHECKLIST.md).

Known Windows executable overrides use `LLUMI_CODEX_PATH` and `LLUMI_CLAUDE_PATH`; the former `AGENTMETER_CODEX_PATH` / `AGENTMETER_CLAUDE_PATH` names remain fallback aliases for compatibility. Discovery still validates the executable and rejects desktop-managed Claude paths.
