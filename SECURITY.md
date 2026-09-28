# Security

Llumi has independent macOS and Windows release channels. Provider authentication stays with the provider's tools; Llumi does not offer its own login or collect credentials.

Report security problems through [GitHub private vulnerability reporting](https://github.com/Praneshsivasankaran/Llumi/security/advisories/new). Do not post exploit details in public issues.

Never attach tokens, cookies, authentication files, raw provider responses, private conversations, terminal history, or source repositories. Start with the affected version, a failure category, and reproduction steps using synthetic data.

Locally installed provider executables are trusted programs. Llumi bounds their usage queries and cleans up owned children, but cannot protect against an attacker who can replace the user's executables or control the user's environment.

The frozen Llumi macOS 1.1.1 DMG is Developer ID signed, Apple notarized and stapled. Historical unsigned AgentMeter betas retain their original status; they are not current Llumi downloads.

Llumi Windows 2.0.2.0 is published in Microsoft Store. The website Windows download remains on hold for final public metadata and installation-path verification. No GitHub Windows binary is offered. Store and direct distribution have separate trust and update requirements. See the [distribution policy](docs/DISTRIBUTION.md).

The supported development line is the current public source. Include the platform, version and installation channel in security reports. Provider updates can change interface behavior; malformed or unverifiable readings must remain unavailable.
