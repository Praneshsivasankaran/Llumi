# Llumi assets

Approved mark: Raspberry semicircular gauge, one needle, rounded-square app tile.
The source SVGs and native raster exporter are original reconstructions of the approved branding reference; no reference screenshot is embedded.

- `llumi-dark.svg/png`: default application icon, near-black tile, Raspberry arc, light needle.
- `llumi-light.svg/png`: off-white tile, Raspberry arc, dark needle.
- `llumi-mono-dark.svg/png`: transparent light gauge for dark surfaces.
- `llumi-mono-light.svg/png`: transparent dark gauge for light surfaces.
- macOS `Llumi.icns`: 16, 32, 128, 256, 512 points at 1x/2x (up to 1024 px).
- Windows `Llumi.ico`: 16, 20, 24, 32, 40, 48, 64, 128, 256 px.

Regenerate all raster resources with `python3 scripts/export-brand.py`. For individual PNG exports use `swift macos/Scripts/make-icon.swift /tmp/Llumi.iconset`, then `iconutil -c icns /tmp/Llumi.iconset`. Windows ICO frames use the exported `windows-*.png` images. AppKit menu rendering uses a template gauge; Windows tray rendering uses a transparent high-contrast gauge with a contrasting outline. Neither uses the colored app tile.

Provider marks remain separate and unchanged. Historical three-bar artwork survives in Git history and historical release artifacts only. The accepted Windows 2.0.2.0 Store package uses Raspberry tile assets derived from the historical MSIX requirements; retain its existing Microsoft identity and resource scale structure.
