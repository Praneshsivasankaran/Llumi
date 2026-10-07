#!/usr/bin/env python3
"""Validate local release-note rendering, safe content and publication isolation."""
import copy
import hashlib
from html.parser import HTMLParser
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("windows_preview", ROOT / "scripts/build-windows-release-preview.py")
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class Page(HTMLParser):
    def __init__(self, text):
        super().__init__()
        self.tags, self.links, self.assets, self.events, self.data, self.meta = [], [], [], [], [], []
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        self.tags.append(tag)
        self.events.extend(key for key in attrs if key.startswith("on"))
        if tag == "a":
            self.links.append(attrs.get("href", ""))
        if tag in ("img", "script", "iframe"):
            self.assets.append(attrs.get("src", ""))
        if tag == "link":
            self.assets.append(attrs.get("href", ""))
            if attrs.get("rel") == "canonical":
                self.fail_canonical = True
        if tag == "meta":
            self.meta.append(attrs)

    def handle_data(self, value):
        self.data.append(value)


def hashes(paths):
    return {path: hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}


class PreviewTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.output = self.root / "preview"
        self.item = builder.load_preview()

    def tearDown(self):
        self.temporary.cleanup()

    def source(self, item):
        path = self.root / "fixture.json"
        path.write_text(json.dumps(item, ensure_ascii=False), encoding="utf-8")
        return path

    def test_local_pages_preserve_every_reviewed_note_and_mac_release_schema(self):
        detail = builder.build(self.output)
        self.assertEqual(detail.relative_to(self.output).as_posix(), "releases/windows/2.1.3/index.html")
        parsed = Page(detail.read_text(encoding="utf-8"))
        text = "".join(parsed.data)
        for section in self.item["sections"]:
            self.assertIn(section["title"], text)
            for note in section["items"]:
                self.assertEqual(text.count(note), 1)
        for value in ("Llumi 2.1.3", "Windows", "Local preview", "Not released yet", builder.HOLD):
            self.assertIn(value, text)
        exported = json.loads((self.output / "releases-preview.json").read_text(encoding="utf-8"))
        self.assertEqual(exported, {"schema_version": 1, "releases": [self.item]})
        self.assertIsNone(exported["releases"][0]["published_at"])
        self.assertEqual(exported["releases"][0]["status"], "preview")
        self.assertIn("Do not deploy", (self.output / "REVIEW-ONLY.txt").read_text(encoding="utf-8"))

    def test_navigation_and_assets_resolve_offline_with_no_download_or_publication_surfaces(self):
        builder.build(self.output)
        for path in self.output.rglob("*.html"):
            page = Page(path.read_text(encoding="utf-8"))
            self.assertFalse(set(page.tags) & {"script", "iframe", "object", "embed", "form", "base", "time"})
            self.assertFalse(page.events)
            self.assertFalse(getattr(page, "fail_canonical", False))
            self.assertIn({"name": "robots", "content": "noindex, nofollow"}, page.meta)
            self.assertTrue(page.links)
            for value in page.links + page.assets:
                link = urlsplit(value)
                self.assertFalse(link.scheme or link.netloc or link.query or link.fragment, value)
                target = (path.parent / unquote(link.path)).resolve()
                self.assertTrue(target.is_relative_to(self.output.resolve()), value)
                self.assertTrue(target.is_file(), value)
            self.assertNotIn("download", " ".join(page.links).lower())
        self.assertFalse(list(self.output.rglob("appcast*")))
        for path in self.output.glob("*.css"):
            css = path.read_text(encoding="utf-8")
            self.assertNotIn("@import", css)
            self.assertNotIn("url(", css)

    def test_untrusted_note_text_cannot_introduce_markup_links_or_remote_assets(self):
        malicious = '<img src="https://example.invalid/pixel" onerror="alert(1)"> & <script>bad()</script>'
        item = copy.deepcopy(self.item)
        item["title"] = item["summary"] = item["notice"] = malicious
        item["sections"][0]["title"] = malicious
        item["sections"][0]["items"] = [malicious, 'javascript:alert("not a link")']
        detail = builder.build(self.output, self.source(item))
        for path in self.output.rglob("*.html"):
            page = Page(path.read_text(encoding="utf-8"))
            self.assertNotIn("script", page.tags)
            self.assertEqual(page.tags.count("img"), 1)
            self.assertFalse(page.events)
            self.assertFalse(any("example.invalid" in link or "javascript:" in link for link in page.links + page.assets))
            self.assertIn(malicious, "".join(page.data))
        self.assertIn('javascript:alert("not a link")', "".join(Page(detail.read_text(encoding="utf-8")).data))

    def test_rejects_claimed_publication_downloads_unsafe_routes_and_malformed_content_before_writing(self):
        patches = [
            {"status": "published", "published_at": "2026-10-07"},
            {"published_at": "2026-10-07"}, {"status": "review"},
            {"download_url": "https://example.invalid/Llumi.exe"},
            {"version": "../../escape"}, {"platform": "macos"},
            {"build": True}, {"build": 0}, {"title": " "}, {"summary": None},
            {"notice": ""}, {"sections": []}, {"sections": [None]},
            {"sections": [{"title": "Notes", "items": [1]}]},
            {"source_documents": []},
        ]
        for change in patches:
            with self.subTest(change=change):
                item = copy.deepcopy(self.item)
                item.update(change)
                with self.assertRaises(ValueError):
                    builder.build(self.output, self.source(item))
                self.assertFalse(self.output.exists())
        missing_date = copy.deepcopy(self.item)
        del missing_date["published_at"]
        with self.assertRaises(ValueError):
            builder.load_preview(self.source(missing_date))
        for raw in ('{"version":"2.1.3","version":"2.1.4"}', " " * 65537):
            path = self.root / "invalid.json"
            path.write_text(raw, encoding="utf-8")
            with self.assertRaises(ValueError):
                builder.load_preview(path)

    def test_build_is_repeatable_and_does_not_modify_public_sources_or_builder(self):
        protected = [path for path in (ROOT / "site").rglob("*") if path.is_file()]
        protected += [ROOT / "scripts/build-site.py"]
        protected += [path for path in (ROOT / ".github/workflows").glob("*") if path.is_file()]
        before = hashes(protected)
        builder.build(self.output)
        built = hashes(path for path in self.output.rglob("*") if path.is_file())
        builder.build(self.output)
        self.assertEqual(built, hashes(built))
        self.assertEqual(before, hashes(protected))

    def test_output_guard_preserves_existing_data_and_refuses_public_destinations(self):
        self.output.mkdir()
        keep = self.output / "owner-notes.txt"
        keep.write_text("retain this file", encoding="utf-8")
        with self.assertRaises(ValueError):
            builder.build(self.output)
        self.assertEqual(keep.read_text(encoding="utf-8"), "retain this file")
        self.assertEqual(list(self.output.iterdir()), [keep])
        for destination in (ROOT, ROOT.parent, ROOT / "site", ROOT / "site/preview", ROOT / "_site", ROOT / "_site/preview"):
            with self.subTest(destination=destination), self.assertRaises(ValueError):
                builder.build(destination)

    def test_output_links_are_refused_without_writing_the_link_target(self):
        self.output.mkdir()
        target = self.root / "elsewhere"
        target.mkdir()
        link = self.output / "releases"
        try:
            link.symlink_to(target, target_is_directory=True)
        except OSError as error:
            self.skipTest(f"This test host does not allow symlink fixtures: {error}")
        with self.assertRaises(ValueError):
            builder.build(self.output)
        self.assertEqual(list(target.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
