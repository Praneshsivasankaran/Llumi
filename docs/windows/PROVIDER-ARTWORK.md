# Windows provider artwork

Windows uses a shared renderer for Usage, setup, Settings and the compact/expanded monitor. These are bundled local assets; rendering performs no downloads.

- **Codex:** official OpenAI monochrome blossom, archive entry `OpenAI-logos(new)/SVGs/OpenAI-white-monoblossom.svg` from the [official brand archive](https://cdn.openai.com/brand/OpenAI-Logos-2025.zip). Original 2,961-byte source SHA-256: `b94ea61d860fae6f82f43571f36f17111fcf5d348e8e9cc22ae4b441c7560011`. Bundled as `windows/src/AgentMeter/Assets/CodexLogo.svg`; foreground follows the effective surface appearance.
- **Claude Code:** pixel mascot from Anthropic's Claude Code VS Code extension **2.1.289**, entry `extension/resources/clawd.svg`. Original 1,751-byte source SHA-256: `9ca6ebb33e268ff95ded9b927ceb53f22deecf4f1de9988f088437621c85154c`. Bundled as `windows/src/AgentMeter/Assets/ClaudeCodeMascot.svg`, retaining its 47 × 38 geometry and original `#D97757` color. Sources: [official extension listing](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code) and [vendor-hosted package](https://anthropic.gallerycdn.vsassets.io/extensions/anthropic/claude-code/2.1.289/1791068913543/Microsoft.VisualStudio.Services.VSIXPackage).

The source assets were verified against the immutable `llumi-macos-1.1.3` tag. Windows checkout line endings and the mascot's final newline differ; normalizing CRLF to LF and ensuring exactly one final newline reproduces the source hashes above. No geometry, scripts or external references were added. The older sunburst asset is retained and embedded by the project resource wildcard, but is not selected by the Windows renderer; the package inventory records it separately from the current mascot.

The official Llumi Raspberry gauge is original Llumi artwork. Its embedded Windows icon supplies the welcome, dashboard, tray and application identity. It replaces the historical three-bars identity.

Provider marks remain vendor property under their applicable artwork and trademark terms; this provenance record does not grant redistribution rights or imply endorsement. See [third-party notices](../../THIRD-PARTY-NOTICES.md). The exact delivered package inventory records the hashes of its built payload.

## Illustrative Windows setup guides

`windows/tools/generate-setup-guides.py` adapts the layout and pacing of `llumi-macos-1.1.3:scripts/macos/generate-setup-demos.py`. The Windows artwork replaces Mac Terminal/commands with PowerShell window styling and the existing Windows Install/Sign in commands. It contains synthetic copy, installation and browser sign-in scenes, identified as illustrations, without account identity or actual provider output. It neither executes commands nor opens a browser. Installation duration is illustrative.

The four embedded assets live in `windows/src/AgentMeter/Assets/SetupGuides/`. Each GIF has 100 frames of 100 ms, looping every ten seconds without playback controls or a demo heading. The PNG is its matching first-frame poster. `SetupAnimation` uses the poster when Windows animations are disabled, stops animation work when hidden/disposed and exposes a descriptive accessibility label. Setup controls remain copy-only, retain their real keyboard actions and always render Light.

The generator uses installed Segoe UI and Consolas as rendering inputs. Font files are not bundled, and Python/Pillow are build-time tools only. No vendor extension, shell installer or remote asset is executed to render these guides.

| Asset | SHA-256 |
| --- | --- |
| `CodexSetup.gif` | `02fd0029a13a920d01050c52254fac9b8d7e69f840cb8ebe08cf1bb2eaf6869b` |
| `CodexSetup.png` | `96ab4099735d14cfcdbe68fd426f24698cb684d56e30d38fe0168566ca52cc66` |
| `ClaudeSetup.gif` | `cbedb603fef81a90d20f03bd618f4710e91f48b668a187a5b80123ddb965b11c` |
| `ClaudeSetup.png` | `05492979e236c8dbc53dfd311c644c6aafe286a966a09875d6d2b45062727c0a` |

These source asset hashes and synthetic renders establish provenance and reproducibility, not real-account setup completion or physical acceptance.
