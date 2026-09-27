# Contributing

Start with the [shared product specification](docs/product-spec/README.md). Describe the user problem and consider macOS and Windows before changing behavior. Keep native platform adaptations explicit; identify any remaining parity work.

Fork the repository, create a focused branch and follow the [macOS](docs/macos/BUILD.md) or [Windows](docs/windows/BUILD.md) build instructions. Run the tests for every affected platform, the public-tree check, and the macOS production privacy check for Mac changes. Explain the behavior changed and what was verified. A passing synthetic test is separate from installed-app, real-provider and distribution acceptance.

Tests must use synthetic data and must not require a provider account. Preserve provider-owned authentication, subprocess isolation, bounded output, account continuity and explicit stale/unavailable states. Do not introduce provider SDK or executable bundling.

Never commit credentials, raw provider output, personal paths, logs, internal validation reports, signing material or generated build artifacts. Review screenshots for private content and identify real captures accurately.

Use [issues](https://github.com/Praneshsivasankaran/Llumi/issues/new/choose) for bugs and focused proposals. Report vulnerabilities through the [security policy](SECURITY.md). See [distribution requirements](docs/DISTRIBUTION.md) before proposing release automation.
