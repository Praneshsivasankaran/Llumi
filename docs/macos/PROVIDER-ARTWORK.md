# Provider artwork

All macOS provider displays share the same image component: Usage, compact/expanded notch, onboarding provider rows and Settings checks. Existing provider names remain beside icons wherever names already appear. Layouts and behavior are unchanged.

Codex uses the official monochrome OpenAI blossom already bundled as `CodexLogo`, from the [official brand archive](https://cdn.openai.com/brand/OpenAI-Logos-2025.zip) and [brand page](https://openai.com/brand/). It replaces the earlier generic code-bracket symbol in Usage, onboarding and Settings, matching the notch. Rendering uses the primary foreground color for light/dark appearance.

The Codex SVG is unchanged: archive entry `OpenAI-logos(new)/SVGs/OpenAI-white-monoblossom.svg`, 2961 bytes, SHA-256 `b94ea61d860fae6f82f43571f36f17111fcf5d348e8e9cc22ae4b441c7560011`.

Claude uses the exact `extension/resources/clawd.svg` from Anthropic's Claude Code for VS Code extension, version 2.1.289, retaining the original color and shape. This is the pixel mascot, distinct from Claude's sunburst.

Source: [official Marketplace listing](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code), linked from [Claude Code's product page](https://claude.com/product/claude-code), and its [vendor-hosted extension package](https://anthropic.gallerycdn.vsassets.io/extensions/anthropic/claude-code/2.1.289/1791068913543/Microsoft.VisualStudio.Services.VSIXPackage).

The bundled SVG is unchanged: 47 × 38 view box, 1751 bytes, SHA-256 `9ca6ebb33e268ff95ded9b927ceb53f22deecf4f1de9988f088437621c85154c`. It contains one pixel-block path, with no scripts or external references. Llumi loads it from its asset catalog without network requests. Only the archive entry was read; the extension was not installed or executed.

This records provenance for the local review build, not a permissive redistribution license. Public packaging must review the provider's applicable brand/asset terms before release.
