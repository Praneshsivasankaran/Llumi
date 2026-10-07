# Windows provider artwork

Windows uses a shared renderer for Usage, setup, Settings and the compact/expanded monitor. These are bundled local assets; rendering performs no downloads.

- **Codex:** official OpenAI monochrome blossom, archive entry `OpenAI-logos(new)/SVGs/OpenAI-white-monoblossom.svg` from the [official brand archive](https://cdn.openai.com/brand/OpenAI-Logos-2025.zip). Original 2,961-byte source SHA-256: `b94ea61d860fae6f82f43571f36f17111fcf5d348e8e9cc22ae4b441c7560011`. Bundled as `windows/src/AgentMeter/Assets/CodexLogo.svg`; foreground follows the effective surface appearance.
- **Claude Code:** pixel mascot from Anthropic's Claude Code VS Code extension **2.1.289**, entry `extension/resources/clawd.svg`. Original 1,751-byte source SHA-256: `9ca6ebb33e268ff95ded9b927ceb53f22deecf4f1de9988f088437621c85154c`. Bundled as `windows/src/AgentMeter/Assets/ClaudeCodeMascot.svg`, retaining its 47 × 38 geometry and original `#D97757` color. Sources: [official extension listing](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code) and [vendor-hosted package](https://anthropic.gallerycdn.vsassets.io/extensions/anthropic/claude-code/2.1.289/1791068913543/Microsoft.VisualStudio.Services.VSIXPackage).

The source assets were verified against the immutable `llumi-macos-1.1.3` tag. Windows checkout line endings and the mascot's final newline differ; normalizing CRLF to LF and ensuring exactly one final newline reproduces the source hashes above. No geometry, scripts or external references were added. The older sunburst asset is retained for source history but is not selected by the Windows renderer.

The official Llumi Raspberry gauge is original Llumi artwork. Its embedded Windows icon supplies the welcome, dashboard, tray and application identity. It replaces the historical three-bars identity.

Provider marks remain vendor property under their applicable artwork and trademark terms; this provenance record does not grant redistribution rights or imply endorsement. See [third-party notices](../../THIRD-PARTY-NOTICES.md). The exact delivered package inventory records the hashes of its built payload.
