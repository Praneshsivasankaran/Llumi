#!/usr/bin/env python3
"""Build static Pages output with no dependencies; PRIVACY.md stays authoritative."""
import argparse
import html
import json
import shutil
from pathlib import Path
import re
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]


def render_privacy(source):
    heading, separator, body = source.strip().partition("\n")
    if heading != "# Privacy" or not separator:
        raise ValueError("Expected PRIVACY.md's # Privacy heading")
    paragraphs = []
    for block in re.split(r"\n\s*\n", body.strip()):
        # The current policy uses paragraphs and inline code only. Stop instead of
        # silently dropping meaning when richer Markdown is introduced.
        if re.search(r"(?m)^\s*(?:#|[-*+] |\d+\. |>)|!?\[.*\]\(|\*\*|__", block) or chr(96) * 3 in block:
            raise ValueError("Policy formatting changed; extend rendering and parity tests")
        if block.count(chr(96)) % 2:
            raise ValueError("Unbalanced inline code in privacy policy")
        safe = html.escape(block)
        safe = re.sub(chr(96) + "([^" + chr(96) + "]+)" + chr(96), r"<code>\1</code>", safe)
        paragraphs.append(f"      <p>{safe}</p>")
    return "\n".join(paragraphs)


def config_url(value, name):
    parsed = urlsplit(value)
    if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password or parsed.fragment:
        raise ValueError(f"{name} must be a public HTTPS URL")
    return value


def load_config():
    config = json.loads((ROOT / "site/config.json").read_text())
    config_url(config["github_url"], "GitHub URL")
    if config.get("canonical_base_url"):
        config_url(config["canonical_base_url"], "Canonical URL")
    for platform in ("macos", "windows"):
        item = config[platform]
        if item["download_url"]:
            config_url(item["download_url"], platform + " download")
            if not item["version"]:
                raise ValueError("Live downloads require a version")
            if "agentmeter" in urlsplit(item["download_url"]).path.rsplit("/", 1)[-1].lower():
                raise ValueError("Historical AgentMeter artifacts are not Llumi downloads")
        if item.get("signed_notarized") and not item["download_url"]:
            raise ValueError("Signing claim requires a final live Llumi download")
    return config


def download_button(item, platform, primary=False):
    classes = "button primary" if primary else "button"
    label = "Download for " + platform
    if item["download_url"]:
        return f'<a class="{classes}" href="{html.escape(item["download_url"], quote=True)}">{label}</a>'
    return f'<button class="{classes}" type="button" disabled aria-label="{label} — not yet available">{label}</button>'


def build(output, base_url=""):
    config = load_config()
    base_url = base_url or config.get("canonical_base_url") or ""
    if base_url:
        parsed = urlsplit(base_url)
        if parsed.scheme != "https" or not parsed.netloc or parsed.query or parsed.fragment:
            raise ValueError("The canonical Pages base URL must be HTTPS without query/fragment")
        base_url = base_url.rstrip("/") + "/"
    media = ["codex.svg", "claude.svg", "llumi-macos-usage.webp", "llumi-macos-usage-small.webp", "llumi-social.png"]
    output.mkdir(parents=True, exist_ok=True)
    allowed = {"index.html", "privacy/index.html", "support/index.html", "styles.css", "mark.svg", "app.js", ".nojekyll"} | {"media/" + name for name in media}
    existing = {p.relative_to(output).as_posix() for p in output.rglob("*") if p.is_file()}
    if existing - allowed or any(p.is_symlink() for p in output.rglob("*")):
        raise ValueError("Unexpected files or links in site output; use a fresh output directory")
    layout = (ROOT / "site/layout.html").read_text(encoding="utf-8")
    policy = render_privacy((ROOT / "PRIVACY.md").read_text(encoding="utf-8"))
    privacy = (
        '<p class="eyebrow">Your coding stays yours.</p><h1>Llumi Privacy Policy</h1>\n'
        '<p class="lead">Developer/Publisher: <strong>Pranesh S</strong></p>\n'
        '<div class="policy">\n' + policy + '\n</div>\n'
        '<p><a href="../">Back to Llumi</a></p>'
    )
    pages = [
        ("", "Llumi — Track your AI coding usage", "Monitor your Codex and Claude Code usage on macOS and Windows. Free and open source.", (ROOT / "site/index.html").read_text(encoding="utf-8")),
        ("privacy/", "Llumi Privacy Policy", "Llumi privacy policy. Developer/Publisher: Pranesh S.", privacy),
        ("support/", "Llumi Support", "Help with Llumi installation, usage and provider compatibility.", (ROOT / "site/support.html").read_text(encoding="utf-8")),
    ]
    for route, title, description, content in pages:
        values = {"TITLE": html.escape(title), "DESCRIPTION": html.escape(description, quote=True),
                  "PREFIX": "../" if route else "./", "CONTENT": content,
                  "PAGE_CLASS": "text-page" if route else "home",
                  "GITHUB": html.escape(config["github_url"], quote=True),
                  "CANONICAL": f'<link rel="canonical" href="{html.escape(base_url + route, quote=True)}"><meta property="og:url" content="{html.escape(base_url + route, quote=True)}">' if base_url else "",
                  "SOCIAL_IMAGE": f'<meta property="og:image" content="{html.escape(base_url + "media/llumi-social.png", quote=True)}"><meta property="og:image:alt" content="Llumi Raspberry gauge">' if base_url else ""}
        for key, platform, label in [("MAC", "macos", "macOS"), ("WINDOWS", "windows", "Windows")]:
            item = config[platform]
            values[key + "_CTA"] = download_button(item, label, platform == "macos")
            values[key + "_DOWNLOAD"] = values[key + "_CTA"]
            values[key + "_COMPATIBILITY"] = html.escape(item["compatibility"])
            values[key + "_VERSION"] = html.escape(("Version " + item["version"]) if item["version"] else "Release version pending")
            values[key + "_STATUS"] = "Direct download" if item["download_url"] else "Not yet available"
        values["MAC_SIGNING"] = '<p>Signed and notarized by Apple</p>' if config["macos"].get("signed_notarized") else ""
        rendered = layout
        for key, value in values.items():
            rendered = rendered.replace("{{" + key + "}}", value)
        if "{{" in rendered:
            raise ValueError("Unresolved page template token")
        target = output / route / "index.html"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("\n".join(line.rstrip() for line in rendered.splitlines()) + "\n", encoding="utf-8")
    for name in ("styles.css", "mark.svg", "app.js"):
        shutil.copyfile(ROOT / "site" / name, output / name)
    (output / "media").mkdir(exist_ok=True)
    for name in media:
        shutil.copyfile(ROOT / "site/media" / name, output / "media" / name)
    (output / ".nojekyll").write_text("", encoding="utf-8")
    print("Built three static Llumi pages; policy parity preserved; download config validated.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "_site")
    parser.add_argument("--base-url", default="")
    args = parser.parse_args()
    build(args.output.resolve(), args.base_url)
