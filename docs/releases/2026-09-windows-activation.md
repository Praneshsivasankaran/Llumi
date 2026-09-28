# Windows website activation — 28 September 2026

The owner authorized enabling and deploying the Windows download controls after confirming Store installation, version, real Codex/Claude usage, compact monitor, startup and quit/relaunch. These physical checks are owner-reported acceptance.

Read-only verification independently found installed package `PraneshS.AgentMeterforWindows_2.0.2.0_x64__wdrmhqnx6zf9r`, version **2.0.2.0**, signature kind **Store**, status **OK**.

The anonymous public listing shows Llumi by Pranesh S, Llumi screenshots, Website `https://tryllumi.com`, Privacy `https://tryllumi.com/privacy/` and Support `https://tryllumi.com/support/`.

Both website Windows controls use `https://apps.microsoft.com/detail/9NV153Q5K5MQ`, with “Available on Microsoft Store” status. macOS 1.1.1 configuration and all frozen release assets remain unchanged. No Partner Center or discoverability setting is changed. The completed metadata monitor remains stopped.

Local validation passed: 11 website tests, two canonical-routing tests, public-tree hygiene and production privacy audit.

## Deployment and final verification

- Activation commit: `c8341b0ce85661571589f86cad4597220e55e974` on canonical GitHub main.
- Cloudflare deployment: `a49bb5fd-f86f-47f1-8703-cd5425957678`, serving HTTPS apex and www redirect.
- Live desktop (1280 px) and mobile (390 px): both hero and download-section Windows controls clicked successfully through to the anonymous Llumi listing for product `9NV153Q5K5MQ`. No horizontal overflow. Mobile refers to browser viewport verification, not a physical phone installation.
- Website status reads “Available on Microsoft Store”; all three public Store metadata links remain correct.
- Anonymous HTTP checks passed for 19 page/asset/link/download requests, including Privacy, Support, Security, License, canonical redirects and retained historical routes. Both macOS controls retain the original public DMG URL; downloaded DMG and sidecar still match `21724ba1b9255c5cbf56003422abe4f2576c3ce8bd8ef599dce769a9766c808c`.
- [Website CI](https://github.com/Praneshsivasankaran/Llumi/actions/runs/36433387868), [Windows CI](https://github.com/Praneshsivasankaran/Llumi/actions/runs/36433387843) and [macOS CI](https://github.com/Praneshsivasankaran/Llumi/actions/runs/36433387782) all passed for the activation commit. Website CI includes Chromium/Firefox/WebKit and accessibility checks.

The launch gate is complete using directly observed website/Store link checks, independently queried installed-package identity/version, and the owner's native Store/provider/lifecycle acceptance. No native Store reinstall or new physical test is claimed from the browser run. Screenshots and machine-readable verification are retained in the private activation archive; the earlier frozen launch archive remains intact.
