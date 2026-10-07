#!/usr/bin/env python3
"""Build the Windows 2.1.3 review page locally; never part of public site output.

The entry uses schema_version 1's release shape from llumi-macos-1.1.3:
site/releases.json and scripts/build-site.py. The renderer and scoped Raspberry
styles below adapt that tag's release_detail/release_card and site/release.css.
The public site builder, download configuration and signed feed stay untouched.
"""
import argparse
import html
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "site/windows-2.1.3-preview.json"
VERSION = "2.1.3"
ROUTE = f"releases/windows/{VERSION}/index.html"
OUTPUT_FILES = {ROUTE, "releases/index.html", "styles.css", "release-preview.css",
                "mark.svg", "releases-preview.json", "REVIEW-ONLY.txt"}
HOLD = "Unreleased local preview. Microsoft Store publication is pending owner approval."

# Only the release styles used by this isolated page; no Mac download or updater UI.
RELEASE_CSS = """/* Adapted from llumi-macos-1.1.3:site/release.css. */
.release-page { padding-bottom: 38px; }
.preview-brand { display: flex; align-items: center; gap: 10px; padding-top: 30px; font-size: 20px; font-weight: 650; }
.preview-brand img { width: 36px; height: 36px; }
.release-detail { max-width: 888px; margin-inline: auto; padding-top: 40px; padding-bottom: 55px; }
.release-back { display: inline-flex; gap: 9px; color: #75269b; font-size: 12px; font-weight: 650; margin-bottom: 37px; }
.release-back:hover, .release-link:hover { text-decoration: underline; text-underline-offset: 4px; }
.release-meta { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; color: #75677f; font-size: 11px; }
.release-platform { font-size: 11px; font-weight: 650; color: #51465b; }
.release-badge { display: inline-flex; align-items: center; padding: 6px 9px; border: 1px solid #dfc6ec; border-radius: 6px; background: #fff8; color: #75259a; font-size: 9px; font-weight: 700; letter-spacing: .055em; text-transform: uppercase; line-height: 1; }
.release-date { margin-left: auto; font-size: 11px; color: #817589; }
.release-detail-header { padding-bottom: 34px; border-bottom: 1px solid var(--border); }
.release-detail h1 { margin-top: 23px; font-size: clamp(38px, 5vw, 57px); line-height: 1.12; letter-spacing: -.055em; }
.release-detail-title { margin-top: 16px; font-size: 26px; font-weight: 650; color: #302735; letter-spacing: -.035em; }
.release-summary { margin-top: 16px; font-size: 16px; color: var(--muted); max-width: 660px; }
.release-notice { margin-top: 25px; padding: 15px 18px; border: 1px solid #e9ddf0; border-radius: 10px; color: #6e507d; background: #f8f2fc; font-size: 12px; line-height: 1.65; }
.release-section { padding-top: 31px; }
.release-section h2 { font-size: 22px; letter-spacing: -.035em; }
.release-section ul { padding-left: 20px; margin: 17px 0 0; font-size: 14px; line-height: 1.7; color: #625969; }
.release-section li + li { margin-top: 11px; }
.release-entry { margin-top: 30px; padding: 31px 34px 32px; border: 1px solid #e3cfea; border-radius: 23px; background: radial-gradient(ellipse at 100% 0%, #ead1ef8a, transparent 57%), linear-gradient(115deg, #f8f1fc, #fcf8fc); box-shadow: inset 0 1px 0 #fff, 0 16px 34px -27px #76369840; }
.release-entry h2 { margin-top: 24px; font-size: 30px; letter-spacing: -.045em; }
.release-entry h3 { margin-top: 15px; font-size: 25px; letter-spacing: -.035em; }
.release-entry p { margin-top: 13px; color: var(--muted); font-size: 14px; }
.release-link { display: inline-flex; margin-top: 23px; color: #75269b; font-size: 12px; font-weight: 650; }
@media (max-width: 600px) {
  .release-detail { padding-top: 31px; }
  .release-back { margin-bottom: 30px; }
  .release-date { flex-basis: 100%; margin-left: 0; padding-top: 3px; }
  .release-detail-title { font-size: 23px; }
  .release-summary { font-size: 15px; }
  .release-entry { padding: 25px; border-radius: 18px; }
}
"""


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate release metadata key")
        result[key] = value
    return result


def readable(value):
    return isinstance(value, str) and bool(value.strip())


def load_preview(source=SOURCE):
    raw = source.read_bytes()
    if len(raw) > 65536:
        raise ValueError("Release preview metadata is too large")
    item = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)
    allowed = {"platform", "version", "build", "status", "published_at", "title", "summary", "sections", "notice", "source_documents"}
    if not isinstance(item, dict) or set(item) - allowed:
        raise ValueError("Unsupported release preview metadata")
    if item.get("platform") != "windows" or item.get("version") != VERSION:
        raise ValueError("This local preview is only for Windows 2.1.3")
    if item.get("status") != "preview" or "published_at" not in item or item["published_at"] is not None:
        raise ValueError("A local preview cannot claim publication")
    if type(item.get("build")) is not int or item["build"] < 1:
        raise ValueError("Release build must be a positive integer")
    if not all(readable(item.get(key)) for key in ("title", "summary", "notice")):
        raise ValueError("Release preview requires readable text")
    if not isinstance(item.get("sections"), list) or not item["sections"]:
        raise ValueError("Release preview requires reviewed sections")
    for section in item["sections"]:
        if not isinstance(section, dict) or set(section) != {"title", "items"} or not readable(section["title"]):
            raise ValueError("Release section requires a title and items")
        if not isinstance(section["items"], list) or not section["items"] or not all(map(readable, section["items"])):
            raise ValueError("Release section requires readable notes")
    if not isinstance(item.get("source_documents"), list) or not item["source_documents"] or not all(map(readable, item["source_documents"])):
        raise ValueError("Release preview requires source document provenance")
    return item


def metadata():
    return ('<div class="release-meta"><span class="release-platform">Windows</span>'
            '<span class="release-badge preview">Local preview</span>'
            '<span class="release-date">Not released yet</span></div>')


def detail(item):
    sections = "".join('<section class="release-section"><h2>' + html.escape(section["title"]) + '</h2><ul>'
                       + "".join('<li>' + html.escape(text) + '</li>' for text in section["items"])
                       + '</ul></section>' for section in item["sections"])
    return ('<div class="release-detail wrap"><a class="release-back" href="../../index.html">← Local release preview</a>'
            + '<header class="release-detail-header">' + metadata()
            + f'<h1>Llumi {VERSION}</h1><p class="release-detail-title">{html.escape(item["title"])}</p>'
            + f'<p class="release-summary">{html.escape(item["summary"])}</p></header>'
            + '<div class="release-notes">' + sections
            + f'<p class="release-notice">{html.escape(item["notice"])}</p></div></div>')


def archive(item):
    return ('<div class="release-detail wrap"><h1>Release notes</h1><p class="release-summary">Windows local review</p>'
            + '<article class="release-entry">' + metadata() + f'<h2>Llumi {VERSION}</h2>'
            + f'<h3>{html.escape(item["title"])}</h3><p>{html.escape(item["summary"])}</p>'
            + f'<a class="release-link" href="windows/{VERSION}/index.html">Read release notes ↗</a></article>'
            + f'<p class="release-notice">{html.escape(HOLD)}</p></div>')


def page(content, prefix, title):
    # Navigation/assets are fixed local paths. Metadata never becomes markup or a URL.
    return f'''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow"><title>{html.escape(title)}</title>
<link rel="icon" href="{prefix}mark.svg"><link rel="stylesheet" href="{prefix}styles.css">
<link rel="stylesheet" href="{prefix}release-preview.css"></head><body class="release-page">
<header class="wrap"><a class="preview-brand" href="{prefix}releases/index.html"><img src="{prefix}mark.svg" alt="" width="36" height="36">Llumi</a></header>
<main>{content}</main><footer class="wrap"><p class="release-notice">{html.escape(HOLD)}</p></footer>
</body></html>
'''


def build(output, source=SOURCE):
    item = load_preview(source)
    output = Path(output)
    if output.is_symlink() or (hasattr(output, "is_junction") and output.is_junction()):
        raise ValueError("Use a local output directory without links")
    output = output.resolve()
    if ROOT == output or ROOT.is_relative_to(output) or any(output.is_relative_to(ROOT / name) for name in ("site", "_site")):
        raise ValueError("Use a separate review directory, never source or public site output")
    existing = list(output.rglob("*")) if output.exists() else []
    if any(path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()) for path in existing):
        raise ValueError("Use a local output directory without links")
    if {path.relative_to(output).as_posix() for path in existing if path.is_file()} - OUTPUT_FILES:
        raise ValueError("Unexpected files in release preview output; use a fresh directory")
    pages = {ROUTE: page(detail(item), "../../../", f"Llumi {VERSION} — Local Release Preview"),
             "releases/index.html": page(archive(item), "../", "Llumi — Local Release Preview")}
    for relative, content in pages.items():
        target = output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
    for name in ("styles.css", "mark.svg"):
        shutil.copyfile(ROOT / "site" / name, output / name)
    (output / "release-preview.css").write_text(RELEASE_CSS, encoding="utf-8")
    # The unchanged entry can later join the Mac schema's releases list after review.
    (output / "releases-preview.json").write_text(json.dumps({"schema_version": 1, "releases": [item]}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "REVIEW-ONLY.txt").write_text(HOLD + "\nOpen releases/windows/2.1.3/index.html locally. Do not deploy this output.\n", encoding="utf-8")
    return output / ROUTE


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "windows/artifacts/windows-release-preview")
    args = parser.parse_args()
    print("Built local-only release preview:", build(args.output))
