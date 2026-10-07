#!/usr/bin/env python3
"""Build local illustrative Windows setup artwork; no commands are executed.

Adapted from llumi-macos-1.1.3:scripts/macos/generate-setup-demos.py.
The 100 frames at 100 ms match Mac pacing, with Windows copy-only commands,
PowerShell chrome and a generic browser sign-in illustration. System fonts
are local build inputs, not bundled. Requires Pillow.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "windows/src/AgentMeter/Assets/SetupGuides"
SIZE = (1280, 720)
BG, INK, MUTED, ACCENT = "#f6f7fa", "#20232b", "#656b78", "#0067c0"
SANS = "C:/Windows/Fonts/segoeui.ttf"
MONO = "C:/Windows/Fonts/consola.ttf"


def font(size, mono=False):
    return ImageFont.truetype(MONO if mono else SANS, size)


def text(draw, xy, value, size=30, fill=INK, mono=False):
    draw.text(xy, value, font=font(size, mono), fill=fill)


def button(draw, rect, label):
    draw.rounded_rectangle(rect, radius=13, fill=ACCENT)
    box = draw.textbbox((0, 0), label, font=font(25))
    text(draw, ((rect[0] + rect[2] - box[2]) / 2, (rect[1] + rect[3] - box[3]) / 2 - 2), label, 25, "white")


def frame(provider, install, login, index):
    image = Image.new("RGB", SIZE, BG)
    draw = ImageDraw.Draw(image)
    stage = 0 if index < 30 else 1 if index < 60 else 2 if index < 80 else 3
    labels = ["Copy the install command", "Paste in PowerShell and press Enter",
              "Run the sign-in command", "Finish signing in"]
    if provider == "Claude Code":
        labels[2] = "Open a new PowerShell window, then sign in"
    text(draw, (64, 36), provider, 39)
    text(draw, (64, 102), labels[stage], 30)
    for step in range(4):
        draw.rounded_rectangle((981 + step * 55, 55, 1020 + step * 55, 63),
                               radius=4, fill=ACCENT if step <= stage else "#dcdfe7")
    panel = (64, 175, 1216, 601)
    draw.rounded_rectangle((64, 180, 1216, 607), radius=22, fill="#e9ebf1")
    draw.rounded_rectangle(panel, radius=22, fill="white", outline="#dce0e8", width=2)
    if stage == 0:
        text(draw, (104, 214), "1. Install " + provider, 29)
        draw.rounded_rectangle((104, 290, 1176, 392), radius=12, fill="#f3f5f9")
        text(draw, (124, 323), install, 27, mono=True)
        button(draw, (994, 465, 1176, 527), "Copied" if index > 15 else "Copy")
    elif stage in (1, 2):
        draw.rounded_rectangle((64, 175, 1216, 234), radius=22, fill="#eef1f6")
        draw.rectangle((65, 206, 1215, 234), fill="#eef1f6")
        draw.rounded_rectangle((93, 192, 126, 221), radius=5, fill="#1765a9")
        text(draw, (97, 190), ">_", 20, "white", mono=True)
        text(draw, (142, 187), "PowerShell", 24, MUTED)
        draw.line((1052, 206, 1068, 206), fill=MUTED, width=2)
        draw.rectangle((1110, 198, 1125, 213), outline=MUTED, width=2)
        draw.line((1170, 198, 1185, 213), fill=MUTED, width=2)
        draw.line((1170, 213, 1185, 198), fill=MUTED, width=2)
        command = install if stage == 1 else login
        text(draw, (105, 278), "PS> " + command, 28, mono=True)
        if stage == 1:
            text(draw, (105, 347), "Installing…", 27, MUTED)
        else:
            text(draw, (105, 347), "Continue in your browser", 27, MUTED)
    else:
        text(draw, (104, 210), "Browser sign-in", 24, MUTED)
        text(draw, (104, 302), "Sign in to " + ("ChatGPT" if provider == "Codex" else "Claude"), 42)
        button(draw, (104, 462, 347, 528), "Continue")
        text(draw, (397, 477), "Return to Llumi → Retry", 27, MUTED)
    text(draw, (64, 642), "Illustration · installation may take longer", 23, MUTED)
    draw.rounded_rectangle((64, 689, 1216, 694), radius=3, fill="#dfe3eb")
    draw.rounded_rectangle((64, 689, 64 + max(4, int(1152 * (index + 1) / 100)), 694), radius=3, fill=ACCENT)
    return image


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, provider, install, login in [
        ("CodexSetup", "Codex", "npm install -g @openai/codex", "codex login"),
        ("ClaudeSetup", "Claude Code", "irm https://claude.ai/install.ps1 | iex", "claude auth login"),
    ]:
        frames = [frame(provider, install, login, index) for index in range(100)]
        frames[0].save(OUT / (name + ".png"), optimize=True)
        frames[0].save(OUT / (name + ".gif"), save_all=True, append_images=frames[1:],
                       duration=100, loop=0, optimize=True, disposal=2)
        print(name + ": 1280×720, 100 frames, 10 seconds; synthetic PowerShell/browser artwork")


if __name__ == "__main__":
    main()
