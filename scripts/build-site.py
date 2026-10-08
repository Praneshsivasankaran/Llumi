#!/usr/bin/env python3
"""Build static Pages output with no dependencies; PRIVACY.md stays authoritative."""
import argparse
from datetime import date
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
    config = json.loads((ROOT / "site/config.json").read_text(encoding="utf-8"))
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


def load_releases(include_review=False):
    metadata = json.loads((ROOT / "site/releases.json").read_text(encoding="utf-8"))
    if metadata.get("schema_version") != 1 or not isinstance(metadata.get("releases"), list):
        raise ValueError("Unsupported release notes metadata")
    seen = set()
    selected = []
    for item in metadata["releases"]:
        if item.get("platform") not in ("macos", "windows"):
            raise ValueError("Unknown release platform")
        if not isinstance(item.get("version"), str) or not re.fullmatch(r"\d+\.\d+\.\d+", item["version"]):
            raise ValueError("Release version must be a numeric display version")
        if type(item.get("build")) is not int or item["build"] < 1:
            raise ValueError("Release build must be a positive integer")
        identity = (item["platform"], item["version"])
        if identity in seen:
            raise ValueError("Duplicate release notes route")
        seen.add(identity)
        if item.get("status") == "published":
            try:
                date.fromisoformat(item["published_at"])
            except (KeyError, TypeError, ValueError):
                raise ValueError("Published release requires a verified publication date") from None
        elif item.get("status") == "submitted":
            if item["platform"] != "windows" or item.get("published_at") is not None:
                raise ValueError("Store submissions must be Windows entries without a publication date")
            try:
                date.fromisoformat(item["submitted_at"])
            except (KeyError, TypeError, ValueError):
                raise ValueError("Store submission requires a verified submission date") from None
        elif item.get("status") != "preview" or item.get("published_at") is not None or item.get("submitted_at") is not None:
            raise ValueError("Preview releases must not claim a publication or submission date")
        for key in ("title", "summary"):
            if not isinstance(item.get(key), str) or not item[key].strip():
                raise ValueError("Release notes require readable title and summary")
        if "notice" in item and (not isinstance(item["notice"], str) or not item["notice"].strip()):
            raise ValueError("Release notice must be readable text")
        if not isinstance(item.get("sections"), list) or not item["sections"]:
            raise ValueError("Release notes require reviewed sections")
        for section in item["sections"]:
            if not isinstance(section.get("title"), str) or not section["title"].strip():
                raise ValueError("Release section requires a title")
            if not isinstance(section.get("items"), list) or not section["items"] or any(
                not isinstance(text, str) or not text.strip() for text in section["items"]
            ):
                raise ValueError("Release sections require readable notes")
        if item["status"] in ("published", "submitted") or include_review:
            selected.append(item)
    return selected


def release_route(item):
    return f'releases/{item["platform"]}/{item["version"]}/'


def release_metadata(item):
    platform = "macOS" if item["platform"] == "macos" else "Windows"
    label = {"preview": "Local preview", "submitted": "Microsoft Store review", "published": "Released"}[item["status"]]
    badge = "release-badge preview" if item["status"] != "published" else "release-badge"
    if item["status"] == "preview":
        when = '<span class="release-date">Not released yet</span>'
    else:
        submitted = item["status"] == "submitted"
        published = date.fromisoformat(item["submitted_at"] if submitted else item["published_at"])
        readable = f"{published.day} {published.strftime('%B')} {published.year}"
        if submitted:
            readable = "Submitted " + readable
        when = f'<time class="release-date" datetime="{published.isoformat()}">{readable}</time>'
    return f'<div class="release-meta"><span class="release-platform">{platform}</span><span class="{badge}">{label}</span>{when}</div>'


def release_card(item):
    url = f'{item["platform"]}/{item["version"]}/'
    return (
        '<article class="release-entry">' + release_metadata(item)
        + f'<h2><a href="{url}">Llumi {html.escape(item["version"])}</a></h2>'
        + f'<h3>{html.escape(item["title"])}</h3><p>{html.escape(item["summary"])}</p>'
        + f'<a class="release-link" href="{url}">Read release notes <span aria-hidden="true">↗</span></a></article>'
    )


def release_detail(item):
    sections = "".join(
        '<section class="release-section"><h2>' + html.escape(section["title"]) + '</h2><ul>'
        + "".join('<li>' + html.escape(text) + '</li>' for text in section["items"])
        + '</ul></section>' for section in item["sections"]
    )
    notice = '<p class="release-notice">' + html.escape(item["notice"]) + '</p>' if item.get("notice") else ""
    return (
        '<div class="release-detail wrap"><a class="release-back" href="../../">← All releases</a>'
        + '<header class="release-detail-header">' + release_metadata(item)
        + f'<h1>Llumi {html.escape(item["version"])}</h1><p class="release-detail-title">{html.escape(item["title"])}</p>'
        + f'<p class="release-summary">{html.escape(item["summary"])}</p></header>'
        + '<div class="release-notes">' + sections + notice + '</div></div>'
    )


def build(output, base_url="", include_review=False):
    config = load_config()
    base_url = base_url or config.get("canonical_base_url") or ""
    if base_url:
        parsed = urlsplit(base_url)
        if parsed.scheme != "https" or not parsed.netloc or parsed.query or parsed.fragment:
            raise ValueError("The canonical Pages base URL must be HTTPS without query/fragment")
        base_url = base_url.rstrip("/") + "/"
    media = ["codex.svg", "claude.svg", "llumi-macos-usage.webp", "llumi-macos-usage-small.webp", "llumi-social.png"]
    releases = load_releases(include_review)
    output.mkdir(parents=True, exist_ok=True)
    allowed = {"index.html", "privacy/index.html", "support/index.html", "releases/index.html", "styles.css", "release.css", "mark.svg", "app.js", "appcast.xml", ".nojekyll"} | {"media/" + name for name in media}
    allowed |= {release_route(item) + "index.html" for item in releases}
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
    archive = (ROOT / "site/releases.html").read_text(encoding="utf-8")
    preview = "".join(release_card(item) for item in releases if item["status"] == "preview")
    history = "".join(release_card(item) for item in releases if item["status"] in ("published", "submitted"))
    published = [item for item in releases if item["status"] == "published"]
    latest = max(published, key=lambda item: (item["published_at"], item["build"])) if published else None
    latest_public = (("macOS" if latest["platform"] == "macos" else "Windows") + " " + latest["version"]) if latest else "pending"
    archive = archive.replace("{{RELEASE_PREVIEW}}", preview).replace("{{RELEASE_HISTORY}}", history).replace("{{LATEST_PUBLIC_RELEASE}}", html.escape(latest_public))
    pages.append(("releases/", "Llumi Release Notes", "Release notes and patch notes for Llumi.", archive))
    for item in releases:
        pages.append((release_route(item), f'Llumi {item["version"]} — Release Notes', item["summary"], release_detail(item)))
    for route, title, description, content in pages:
        prefix = "../" * len(route.strip("/").split("/")) if route else "./"
        extras = f'<link rel="stylesheet" href="{prefix}release.css">' if route.startswith("releases/") else ""
        if include_review:
            extras += '<meta name="robots" content="noindex, nofollow">'
        current = ' aria-current="page"' if route.startswith("releases/") else ""
        values = {"TITLE": html.escape(title), "DESCRIPTION": html.escape(description, quote=True),
                  "PREFIX": prefix, "CONTENT": content,
                  "PAGE_CLASS": "release-page" if route.startswith("releases/") else ("text-page" if route else "home"),
                  "PAGE_HEAD": extras, "RELEASE_CURRENT": current,
                  "GITHUB": html.escape(config["github_url"], quote=True),
                  "CANONICAL": f'<link rel="canonical" href="{html.escape(base_url + route, quote=True)}"><meta property="og:url" content="{html.escape(base_url + route, quote=True)}">' if base_url else "",
                  "SOCIAL_IMAGE": f'<meta property="og:image" content="{html.escape(base_url + "media/llumi-social.png", quote=True)}"><meta property="og:image:alt" content="Llumi Raspberry gauge">' if base_url else ""}
        for key, platform, label in [("MAC", "macos", "macOS"), ("WINDOWS", "windows", "Windows")]:
            item = config[platform]
            values[key + "_CTA"] = download_button(item, label, platform == "macos")
            values[key + "_DOWNLOAD"] = values[key + "_CTA"]
            values[key + "_COMPATIBILITY"] = html.escape(item["compatibility"])
            values[key + "_VERSION"] = html.escape(("Version " + item["version"]) if item["version"] else "Release version pending")
            if platform == "windows":
                values[key + "_STATUS"] = "Available on Microsoft Store" if item["download_url"] else "Microsoft Store download coming soon"
            else:
                values[key + "_STATUS"] = "Direct download" if item["download_url"] else "Release pending"
        values["MAC_SIGNING"] = '<p>Developer ID signed and Apple notarized</p>' if config["macos"].get("signed_notarized") else ""
        rendered = layout
        for key, value in values.items():
            rendered = rendered.replace("{{" + key + "}}", value)
        if "{{" in rendered:
            raise ValueError("Unresolved page template token")
        target = output / route / "index.html"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("\n".join(line.rstrip() for line in rendered.splitlines()) + "\n", encoding="utf-8")
    for name in ("styles.css", "release.css", "mark.svg", "app.js"):
        shutil.copyfile(ROOT / "site" / name, output / name)
    # Preserve Sparkle's signed feed byte for byte; template rendering invalidates it.
    shutil.copyfile(ROOT / "site/appcast.xml", output / "appcast.xml")
    (output / "media").mkdir(exist_ok=True)
    for name in media:
        shutil.copyfile(ROOT / "site/media" / name, output / "media" / name)
    (output / ".nojekyll").write_text("", encoding="utf-8")
    print(f"Built {len(pages)} static Llumi pages; policy parity preserved; download config validated; local review {'enabled' if include_review else 'excluded'}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "_site")
    parser.add_argument("--base-url", default="")
    parser.add_argument("--include-review", action="store_true", help="Include unreleased local review notes; never deploy this output")
    args = parser.parse_args()
    build(args.output.resolve(), args.base_url, args.include_review)
