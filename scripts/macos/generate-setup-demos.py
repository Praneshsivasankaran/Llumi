#!/usr/bin/env python3
"""Render illustrative, ten-second setup guides. Requires Pillow; no live accounts.

Commands mirror ProviderSetup. Terminal/browser artwork is synthetic and installation
waits are explicitly shortened. System fonts are local build inputs, not bundled.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "macos/AgentMeter/Resources/SetupDemos"
SIZE = (1280, 720)
BG, INK, MUTED, ACCENT = "#f6f5f3", "#222329", "#666972", "#be3155"
SANS = "/System/Library/Fonts/SFNS.ttf"
MONO = "/System/Library/Fonts/SFNSMono.ttf"


def font(size, mono=False):
    return ImageFont.truetype(MONO if mono else SANS, size)


def text(draw, xy, value, size=30, fill=INK, mono=False):
    draw.text(xy, value, font=font(size, mono), fill=fill)


def button(draw, rect, label, active=True):
    draw.rounded_rectangle(rect, radius=15, fill=ACCENT if active else "#eeedef")
    box = draw.textbbox((0, 0), label, font=font(25))
    x = (rect[0] + rect[2] - box[2]) / 2
    y = (rect[1] + rect[3] - box[3]) / 2 - 2
    text(draw, (x, y), label, 25, "white" if active else INK)


def frame(provider, install, login, index):
    image = Image.new("RGB", SIZE, BG)
    draw = ImageDraw.Draw(image)
    stage = 0 if index < 30 else 1 if index < 60 else 2 if index < 80 else 3
    labels = ["Copy install command", "Paste into Terminal and press Return",
              "Run sign-in command", "Finish signing in"]
    text(draw, (64, 40), provider, 39)
    text(draw, (64, 104), labels[stage], 30)
    for step in range(4):
        draw.rounded_rectangle((981 + step * 55, 55, 1020 + step * 55, 63),
                               radius=4, fill=ACCENT if step <= stage else "#dad9db")
    panel = (64, 175, 1216, 601)
    draw.rounded_rectangle(panel, radius=22, fill="white", outline="#dfdee1", width=2)
    if stage == 0:
        text(draw, (104, 215), "1. Install " + provider, 29)
        draw.rounded_rectangle((104, 290, 1176, 392), radius=12, fill="#f4f3f5")
        text(draw, (124, 323), install, 23, mono=True)
        button(draw, (994, 465, 1176, 527), "Copied" if index > 15 else "Copy")
    elif stage in (1, 2):
        draw.rounded_rectangle((64, 175, 1216, 234), radius=22, fill="#f0eff1")
        draw.rectangle((65, 206, 1215, 234), fill="#f0eff1")
        for cx, color in [(95, "#f86b63"), (121, "#f4bf4f"), (147, "#60c957")]:
            draw.ellipse((cx, 196, cx + 16, 212), fill=color)
        text(draw, (567, 188), "Terminal", 24, MUTED)
        if stage == 1:
            text(draw, (105, 278), "$ " + install, 24, mono=True)
            text(draw, (105, 346), "Installing…", 27, MUTED)
        else:
            text(draw, (105, 278), "$ " + login, 28, mono=True)
    else:
        text(draw, (104, 211), "Browser sign-in", 24, MUTED)
        text(draw, (104, 302), "Sign in to " + ("ChatGPT" if provider == "Codex" else "Claude"), 42)
        button(draw, (104, 462, 347, 528), "Continue")
        text(draw, (397, 479), "Return to Llumi → Retry", 27, MUTED)
    text(draw, (64, 643), "Illustration · installation may take longer", 23, MUTED)
    draw.rounded_rectangle((64, 689, 1216, 694), radius=3, fill="#e4e2e5")
    draw.rounded_rectangle((64, 689, 64 + max(4, int(1152 * (index + 1) / 100)), 694), radius=3, fill=ACCENT)
    return image


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, provider, install, login in [
        ("CodexSetup", "Codex", "brew install --cask codex", "codex login"),
        ("ClaudeSetup", "Claude Code", "curl -fsSL https://claude.ai/install.sh | bash", "claude auth login"),
    ]:
        frames = [frame(provider, install, login, i) for i in range(100)]
        frames[0].save(OUT / (name + ".png"), optimize=True)
        frames[0].save(OUT / (name + ".gif"), save_all=True, append_images=frames[1:],
                       duration=100, loop=0, optimize=True, disposal=2)
        print(name + ": 1280×720, 10 seconds; synthetic terminal/browser artwork")


if __name__ == "__main__":
    main()
