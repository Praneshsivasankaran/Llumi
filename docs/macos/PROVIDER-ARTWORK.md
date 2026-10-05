# Provider artwork

All macOS Claude provider displays use the exact `extension/resources/clawd.svg` from Anthropic's Claude Code for VS Code extension, version 2.1.289: Usage, compact/expanded notch, onboarding provider rows and Settings checks. They share one image component, retaining the original color and shape. Existing provider names remain beside it wherever names already appear. This is the pixel mascot, distinct from Claude's sunburst. Codex artwork, layouts and behavior are unchanged.

Source: [official Marketplace listing](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code), linked from [Claude Code's product page](https://claude.com/product/claude-code), and its [vendor-hosted extension package](https://anthropic.gallerycdn.vsassets.io/extensions/anthropic/claude-code/2.1.289/1791068913543/Microsoft.VisualStudio.Services.VSIXPackage).

The bundled SVG is unchanged: 47 × 38 view box, 1751 bytes, SHA-256 `9ca6ebb33e268ff95ded9b927ceb53f22deecf4f1de9988f088437621c85154c`. It contains one pixel-block path, with no scripts or external references. Llumi loads it from its asset catalog without network requests. Only the archive entry was read; the extension was not installed or executed.

This records provenance for the local review build, not a permissive redistribution license. Public packaging must review the provider's applicable brand/asset terms before release.
