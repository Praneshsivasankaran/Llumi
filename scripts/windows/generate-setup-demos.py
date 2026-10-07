#!/usr/bin/env python3
"""Render Windows setup illustrations and reduced-motion overviews.

Requires Pillow and the Windows Segoe UI/Consolas system fonts. The artwork is
synthetic: no terminal, browser, account, credentials or network is accessed.
Commands match Windows setup. Installation waits are deliberately abbreviated.
Run from any directory; generated assets are committed for normal app builds.
"""

from functools import lru_cache
from pathlib import Path
import os
import re

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "windows/src/AgentMeter/Assets/SetupDemos"
FONT_DIR = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
SIZE = (1280, 480)
SCALE = 2
FRAMES = 20
FRAME_MS = 500
BG, SURFACE, INK, MUTED = "#F4F7FB", "#FFFFFF", "#172334", "#536174"
BLUE, PALE_BLUE, LINE = "#0067C0", "#E8F2FC", "#D7E0EB"

PROVIDERS = (
    ("CodexSetup", "Codex", "npm install -g @openai/codex", "codex login", "ChatGPT"),
    ("ClaudeSetup", "Claude Code", "irm https://claude.ai/install.ps1 | iex", "claude auth login", "Claude"),
)


def validate_source_commands():
    """Fail before rendering if illustrations drift from the copyable commands."""
    source = (ROOT / "windows/src/AgentMeter/ProviderSetup.cs").read_text(encoding="utf-8-sig")
    for method, column in (("InstallCommand", 2), ("SignInCommand", 3)):
        pattern = (rf'internal\s+static\s+string\s+{method}\(string\s+provider\)\s*=>\s*'
                   r'provider\s*==\s*"Codex"\s*\?\s*"([^"\\]*)"\s*:\s*"([^"\\]*)"\s*;')
        match = re.search(pattern, source)
        if match is None:
            raise ValueError(f"Review the generator after ProviderSetup.{method} changes.")
        expected = tuple(provider[column] for provider in PROVIDERS)
        if match.groups() != expected:
            raise ValueError(f"Setup illustration commands differ from ProviderSetup.{method}.")


@lru_cache(maxsize=None)
def font(size, style="regular"):
    filename = {"regular": "segoeui.ttf", "bold": "seguisb.ttf", "mono": "consola.ttf"}[style]
    return ImageFont.truetype(str(FONT_DIR / filename), size * SCALE)


def rect(draw, bounds, fill, radius=14, outline=None):
    draw.rounded_rectangle(tuple(int(v * SCALE) for v in bounds),
                           radius=radius * SCALE, fill=fill, outline=outline,
                           width=SCALE if outline else 1)


def text(draw, xy, value, size=32, fill=INK, style="regular"):
    draw.text(tuple(v * SCALE for v in xy), value, font=font(size, style), fill=fill)


def center_text(draw, bounds, value, size=32, fill=INK, style="regular"):
    face = font(size, style)
    box = draw.textbbox((0, 0), value, font=face)
    x = (bounds[0] + bounds[2]) * SCALE / 2 - (box[2] + box[0]) / 2
    y = (bounds[1] + bounds[3]) * SCALE / 2 - (box[3] + box[1]) / 2
    draw.text((x, y), value, font=face, fill=fill)


def canvas(provider):
    image = Image.new("RGB", (SIZE[0] * SCALE, SIZE[1] * SCALE), BG)
    draw = ImageDraw.Draw(image)
    text(draw, (32, 17), provider + " setup", 40, style="bold")
    return image, draw


def button(draw, bounds, label):
    rect(draw, bounds, BLUE, 9)
    center_text(draw, bounds, label, 32, "white", "bold")


def window(draw, title, terminal=False):
    rect(draw, (32, 140, 1248, 402), SURFACE, 16, LINE)
    rect(draw, (33, 141, 1247, 205), "#EDF1F6", 15)
    draw.rectangle((33 * SCALE, 184 * SCALE, 1247 * SCALE, 205 * SCALE), fill="#EDF1F6")
    if terminal:
        rect(draw, (56, 154, 104, 190), BLUE, 5)
        center_text(draw, (56, 154, 104, 190), ">_", 25, "white", "mono")
        text(draw, (118, 149), title, 32)
    else:
        text(draw, (58, 149), title, 32)
    # Familiar Windows window controls, with no screenshots or live UI involved.
    text(draw, (1091, 147), "−  □  ×", 29, MUTED)


def frame(provider, install, login, account, index):
    image, draw = canvas(provider)
    stage = index // 5
    for step in range(4):
        rect(draw, (1027 + step * 56, 34, 1071 + step * 56, 42),
             BLUE if step == stage else "#D2DFEC", 4)
    labels = (
        "Copy the install command",
        "Paste in PowerShell, then press Enter",
        "Open a new PowerShell and run the sign-in command",
        "Finish signing in with your browser",
    )
    rect(draw, (32, 84, 74, 126), BLUE, 21)
    center_text(draw, (32, 84, 74, 126), str(stage + 1), 30, "white", "bold")
    text(draw, (90, 81), labels[stage], 34, style="bold")
    if stage == 0:
        window(draw, "Install " + provider)
        rect(draw, (58, 232, 1222, 316), BG, 9, LINE)
        text(draw, (80, 252), install, 34, style="mono")
        if provider == "Codex":
            text(draw, (60, 339), "Node.js and npm required", 32, MUTED)
        button(draw, (1048, 331, 1222, 383), "Copied" if index >= 2 else "Copy")
    elif stage == 1:
        window(draw, "PowerShell", terminal=True)
        text(draw, (60, 232), "PS > " + install, 34, style="mono")
        text(draw, (60, 314), "Installing…", 34, MUTED)
    elif stage == 2:
        window(draw, "PowerShell · new window", terminal=True)
        text(draw, (60, 232), "PS > " + login, 36, style="mono")
        text(draw, (60, 314), "Press Enter to open browser sign-in.", 32, MUTED)
    else:
        window(draw, "Browser sign-in")
        text(draw, (60, 220), "Sign in to " + account, 42, style="bold")
        text(draw, (60, 291), "Return to Llumi, then Continue to Check Setup.", 34)
        text(draw, (60, 345), "Use your existing account.", 32, MUTED)
    text(draw, (32, 415), "Illustration · installation may take longer", 30, MUTED)
    rect(draw, (32, 464, 1248, 470), "#DCE6F0", 3)
    rect(draw, (32, 464, 32 + round(1216 * (index + 1) / FRAMES), 470), BLUE, 3)
    return image.resize(SIZE, Image.Resampling.LANCZOS)


def overview(provider, login):
    """A useful four-step still for disabled animation or Reduce Motion."""
    image, draw = canvas(provider)
    steps = (
        ("Copy the install command", "Requires Node.js and npm." if provider == "Codex" else "Use Copy below."),
        ("Paste in PowerShell", "Press Enter to install."),
        ("Open a new PowerShell", "Run: " + login),
        ("Finish browser sign-in", "Continue to Check Setup."),
    )
    for index, (heading, detail) in enumerate(steps):
        col, row = index % 2, index // 2
        x, y = 32 + col * 616, 94 + row * 155
        rect(draw, (x, y, x + 600, y + 139), SURFACE, 14, LINE)
        rect(draw, (x + 18, y + 20, x + 60, y + 62), BLUE, 21)
        center_text(draw, (x + 18, y + 20, x + 60, y + 62), str(index + 1), 30, "white", "bold")
        text(draw, (x + 76, y + 18), heading, 32, style="bold")
        text(draw, (x + 76, y + 74), detail, 32)
    text(draw, (32, 415), "Illustration · installation may take longer", 30, MUTED)
    return image.resize(SIZE, Image.Resampling.LANCZOS)


def main():
    validate_source_commands()
    OUT.mkdir(parents=True, exist_ok=True)
    for name, provider, install, login, account in PROVIDERS:
        frames = [frame(provider, install, login, account, index) for index in range(FRAMES)]
        overview(provider, login).save(OUT / (name + ".png"), optimize=True)
        frames[0].save(OUT / (name + ".gif"), save_all=True, append_images=frames[1:],
                       duration=FRAME_MS, loop=0, optimize=True, disposal=2)
        with Image.open(OUT / (name + ".gif")) as animation:
            duration = 0
            assert animation.size == SIZE and animation.n_frames == FRAMES
            assert animation.info["loop"] == 0
            for index in range(animation.n_frames):
                animation.seek(index)
                duration += animation.info["duration"]
            assert duration == 10_000
        print(f"{name}: {SIZE[0]}×{SIZE[1]}, {FRAMES} frames, {duration} ms; four-step PNG overview")


if __name__ == "__main__":
    main()
