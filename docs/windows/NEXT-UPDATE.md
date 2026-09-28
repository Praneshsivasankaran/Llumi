# Windows next-update backlog

Baseline: immutable Llumi 2.0.2 / MSIX 2.0.2.0. No new application defects were independently reproduced during launch documentation cleanup. The earlier 607-test and physical acceptance results remain historical evidence for that candidate, not a fresh test run or native Store installation acceptance.

| Item | Status and evidence | Completion criterion |
| --- | --- | --- |
| Final Store installation path | Launch verification pending, not a source defect. Historical 2.0.1.0 and desktop review copies were uninstalled at the owner's request. | Public listing → native Store → install/update → verify installed Llumi 2.0.2.0, provider usage, startup and quit/relaunch; record offered and installed versions separately. |
| Public metadata propagation | Published Submission 3 saves correct Llumi URLs; anonymous en-US/India listing still showed historical links on 2026-09-28. | All three public hrefs use tryllumi.com. If mismatch persists, collect evidence for Microsoft support before proposing another metadata change. |
| Discoverability | Decision open: published submission is public, free, worldwide, direct-link-only. No setting changed. | Owner chooses whether/when to make Store search discovery available; separately authorize any change. |
| Future Windows observations | Intake scaffold; no unreported UI bug is assumed. Prior Claude setup, five-hour compact/weekly hover, theme and lifecycle issues were accepted as resolved. | Add exact version/channel, steps, expected/actual behavior and sanitized capture; reproduce before assigning a source fix. |
| Compact monitor regression coverage | Maintenance watch list: provider choice, five-hour compact/weekly hover, scaling/multiple displays, Light/Dark, activity transitions. | Exercise on real hardware for the next candidate and record unknown values truthfully. |
| Runtime/dependency maintenance | Self-contained packages need new releases for bundled-runtime updates; frozen 2.0.2.0 cannot be patched in place. | Review advisories and redistribution inventory, select a new version, run complete packaging/lifecycle acceptance. |
| Direct Windows download | Separate future channel, not a launch requirement. No production direct binary published. | Complete signing, clean-machine, update/coexistence and distribution gates before proposing availability. |

## Intake template

- Title, severity and affected platform/version/installation channel:
- Exact reproduction; expected versus actual behavior:
- Frequency, Windows build, architecture, display/scaling and provider versions:
- Sanitized evidence and whether independently reproduced:
- Proposed shared-spec change and intentional native differences:
- Test/physical acceptance criteria, owner and target **new** version:

Do not include provider credentials, raw usage payloads, private terminal contents or conversations. Do not ship speculative fixes into either frozen launch baseline.
