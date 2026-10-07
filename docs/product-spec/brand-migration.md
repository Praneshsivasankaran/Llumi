# Llumi identity migration

The current product is **Llumi** (capital L followed by lowercase lumi).
Tagline: **Track your AI coding usage.**
The approved Raspberry gauge replaces the former three-bar mark. One semicircular arc, purple through magenta to pink, and one needle. Windows uses the official full-color Llumi application tile for its notification-area icon, matching setup, the dashboard and taskbar; select the embedded size appropriate to the display and retain the status tooltip. macOS keeps its monochrome menu-bar variant. Provider marks and layout remain unchanged.

macOS retains version 1.1.1 and moves to `io.github.praneshsivasankaran.llumi`.
Allowlisted legacy preferences come first from `io.github.praneshsivasankaran.agentmeter`, then `local.agentmeter.mac`. Current valid Llumi preferences win. Only appearance, notch/menu visibility and setup completion may migrate. No provider data, logs, credentials, paths or login registration migrate. The old domains remain intact.

Llumi must acquire its own instance lock and the former production lock before starting services. This prevents an old AgentMeter candidate and Llumi from polling concurrently. A legacy lock is compatibility data, not Llumi's app identity.

Windows keeps its existing candidate version pending verification of the accepted Store package. Microsoft identity `PraneshS.AgentMeterforWindows`, Store product `9NV153Q5K5MQ`, and existing packaged LocalState remain stable. The packaged startup task `AgentMeterStartup` remains a compatibility identifier until the accepted manifest can be inspected. Direct-install preferences migrate only explicitly validated values from the old portable data folder, without copying the directory. The old named mutex remains held as a compatibility guard alongside the new Llumi mutex.

Before replacing AgentMeter, turn off its Launch at Login/Start with Windows setting, quit it, then install Llumi and enable startup again if desired. No system database cleanup or automatic legacy startup deletion is performed.

Historical releases, receipts, tags and screenshots remain AgentMeter. Old screenshots are not evidence of Llumi. The repository and public URLs remain unchanged until separately authorized. No signing, submission, final packaging, release or website deployment is authorized by this source migration.
