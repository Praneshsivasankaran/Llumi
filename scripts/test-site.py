#!/usr/bin/env python3
"""Validate generated public pages, policy text and relative navigation."""
from html.parser import HTMLParser
import importlib.util
from pathlib import Path
import re
import unittest
import json
import base64
import xml.etree.ElementTree as ET
from unittest.mock import patch
import tempfile
from urllib.parse import urljoin, urlsplit, unquote

ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "_site"
spec = importlib.util.spec_from_file_location("build_site", ROOT / "scripts/build-site.py")
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class Page(HTMLParser):
    def __init__(self, text):
        super().__init__()
        self.links, self.assets, self.tags, self.ids, self.data = [], [], [], set(), []
        self.titles, self.headings, self.policy = [], [], []
        self.title = self.h1 = False
        self.policy_depth = 0
        self.events = []
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        self.tags.append(tag)
        self.events.extend(k for k in attrs if k.startswith("on"))
        if "id" in attrs:
            self.ids.add(attrs["id"])
        if tag == "a":
            self.links.append(attrs.get("href", ""))
        if tag in ("img", "script", "iframe"):
            self.assets.append(attrs.get("src", ""))
        if tag == "link" and attrs.get("rel") in ("stylesheet", "icon"):
            self.assets.append(attrs.get("href", ""))
        if tag == "title":
            self.title = True
        if tag == "h1":
            self.h1 = True
        if tag == "div":
            if attrs.get("class") == "policy":
                self.policy_depth = 1
            elif self.policy_depth:
                self.policy_depth += 1

    def handle_endtag(self, tag):
        if tag == "title":
            self.title = False
        if tag == "h1":
            self.h1 = False
        if tag == "div" and self.policy_depth:
            self.policy_depth -= 1

    def handle_data(self, data):
        self.data.append(data)
        if self.title:
            self.titles.append(data)
        if self.h1:
            self.headings.append(data)
        if self.policy_depth:
            self.policy.append(data)


class SiteTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.files = {p.relative_to(SITE).as_posix() for p in SITE.rglob("*") if p.is_file()}
        cls.pages = {name: Page((SITE / name).read_text(encoding="utf-8")) for name in cls.files if name.endswith(".html")}

    def test_only_intended_public_output(self):
        expected = {"index.html", "privacy/index.html", "support/index.html", "releases/index.html", "styles.css", "release.css", "mark.svg", "app.js", "appcast.xml", ".nojekyll", "media/codex.svg", "media/claude.svg", "media/llumi-macos-usage.webp", "media/llumi-macos-usage-small.webp", "media/llumi-social.png"}
        expected |= {builder.release_route(item) + "index.html" for item in builder.load_releases()}
        self.assertEqual(self.files, expected)

    def test_preview_notes_are_excluded_from_public_output(self):
        self.assertFalse(any(name.startswith("review/") for name in self.files))
        for item in builder.load_releases(include_review=True):
            if item["status"] == "preview":
                self.assertNotIn(builder.release_route(item) + "index.html", self.files)
                self.assertNotIn(item["version"], "".join(self.pages["releases/index.html"].data))

    def test_release_navigation_and_version_notes(self):
        self.assertIn("./releases/", self.pages["index.html"].links)
        for item in builder.load_releases():
            page = self.pages[builder.release_route(item) + "index.html"]
            self.assertEqual(" ".join("".join(page.headings).split()), "Llumi " + item["version"])
            self.assertIn("../../../styles.css", page.assets)
            self.assertIn("../../../release.css", page.assets)
            self.assertIn("../../", page.links)
            for section in item["sections"]:
                for note in section["items"]:
                    self.assertIn(note, "".join(page.data))

    def test_local_review_build_contains_only_notes_and_preserves_feed(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            builder.build(output, include_review=True)
            files = {p.relative_to(output).as_posix() for p in output.rglob("*") if p.is_file()}
            expected = self.files | {builder.release_route(item) + "index.html"
                for item in builder.load_releases(include_review=True)}
            self.assertEqual(files, expected)
            self.assertFalse((output / "review").exists())
            archive = (output / "releases/index.html").read_text()
            self.assertNotIn("Preview the update flow", archive)
            self.assertNotIn("update-flow", archive)
            for item in builder.load_releases(include_review=True):
                text = (output / builder.release_route(item) / "index.html").read_text()
                self.assertIn('name="robots" content="noindex, nofollow"', text)
                if item["status"] == "preview":
                    self.assertIn("Not released yet", text)
                    self.assertNotIn("<time", text)
            self.assertEqual((output / "appcast.xml").read_bytes(), (ROOT / "site/appcast.xml").read_bytes())
            with self.assertRaises(ValueError):
                builder.build(output, include_review=False)

    def test_removed_browser_preview_output_requires_a_fresh_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            stale = output / "review/update-flow/index.html"
            stale.parent.mkdir(parents=True)
            stale.write_text("Historical browser preview")
            for include_review in (False, True):
                with self.assertRaises(ValueError):
                    builder.build(output, include_review=include_review)

    def test_release_metadata_rejects_invalid_identity_and_release_claims(self):
        original = json.loads((ROOT / "site/releases.json").read_text())
        for patch_key, patch_value in (("version", "../../oops"), ("build", True), ("status", "latest"), ("published_at", "2026-10-05")):
            metadata = json.loads(json.dumps(original))
            preview = next(item for item in metadata["releases"] if item["status"] == "preview")
            preview[patch_key] = patch_value
            with patch.object(builder.json, "loads", return_value=metadata):
                with self.assertRaises(ValueError):
                    builder.load_releases(include_review=True)
        metadata = json.loads(json.dumps(original))
        metadata["releases"].append(metadata["releases"][0])
        with patch.object(builder.json, "loads", return_value=metadata):
            with self.assertRaises(ValueError):
                builder.load_releases(include_review=True)

    def test_release_notes_escape_metadata(self):
        item = dict(builder.load_releases()[0])
        item.update(title='<script>alert("unsafe")</script>', summary='<img src=x onerror="unsafe">')
        item["sections"] = [{"title": "<b>Changed</b>", "items": ["<script>unsafe</script>"]}]
        for rendered in (builder.release_card(item), builder.release_detail(item)):
            self.assertNotIn("<script>", rendered)
            self.assertNotIn("<img src=x", rendered)
            self.assertIn("&lt;script&gt;", rendered)

    def test_signed_appcast_is_preserved_without_rendering(self):
        feed = (SITE / "appcast.xml").read_bytes()
        self.assertEqual(feed, (ROOT / "site/appcast.xml").read_bytes())
        self.assertIn(b'<!-- sparkle-signatures:', feed)
        self.assertNotIn(b'untagged-', feed)
        ns = {'sparkle': 'http://www.andymatuschak.org/xml-namespaces/sparkle'}
        items = ET.fromstring(feed).findall('./channel/item')
        self.assertTrue(items)
        for item in items:
            version = item.findtext('sparkle:shortVersionString', namespaces=ns)
            self.assertRegex(version, r'^\d+\.\d+\.\d+$')
            self.assertRegex(item.findtext('sparkle:version', namespaces=ns), r'^[1-9]\d*$')
            enclosure = item.find('enclosure')
            expected = f'https://github.com/Praneshsivasankaran/Llumi/releases/download/llumi-macos-{version}/Llumi-{version}-macos.dmg'
            self.assertEqual(enclosure.attrib['url'], expected)
            self.assertGreater(int(enclosure.attrib['length']), 0)
            signature = enclosure.attrib['{' + ns['sparkle'] + '}edSignature']
            self.assertEqual(len(base64.b64decode(signature, validate=True)), 64)

    def test_titles_identity_and_visible_content(self):
        for name, title in [("index.html", "Llumi — Track your AI coding usage"), ("privacy/index.html", "Llumi Privacy Policy"), ("support/index.html", "Llumi Support")]:
            page = self.pages[name]
            self.assertEqual("".join(page.titles), title)
            self.assertEqual(" ".join("".join(page.headings).split()), "Llumi." if name == "index.html" else title)
            text = "".join(page.data)
            self.assertIn("Pranesh S", text)
            self.assertGreater(len(text.strip()), 500)
            self.assertNotIn("{{", text)

    def test_privacy_exact_text_parity(self):
        original = (ROOT / "PRIVACY.md").read_text(encoding="utf-8").strip().partition("\n")[2]
        expected = " ".join(original.replace(chr(96), "").split())
        actual = " ".join("".join(self.pages["privacy/index.html"].policy).split())
        self.assertEqual(actual, expected)

    def test_links_and_assets_are_case_correct(self):
        for name, page in self.pages.items():
            for link in page.links + page.assets:
                self.assertTrue(link)
                target = urlsplit(urljoin("https://site.invalid/AgentMeter/" + name, link))
                if target.netloc != "site.invalid":
                    self.assertEqual(target.scheme, "https")
                    continue
                self.assertTrue(target.path.startswith("/AgentMeter/"))
                relative = unquote(target.path.removeprefix("/AgentMeter/"))
                if not relative or relative.endswith("/"):
                    relative += "index.html"
                self.assertIn(relative, self.files, (name, link))
                if target.fragment:
                    self.assertIn(target.fragment, self.pages[relative].ids)

    def test_no_tracking_or_external_resources(self):
        for page in self.pages.values():
            self.assertFalse(set(page.tags) & {"iframe", "form", "object", "embed", "base"})
            self.assertFalse(page.events)
            for asset in page.assets:
                self.assertFalse(urlsplit(asset).netloc)
        css = (SITE / "styles.css").read_text(encoding="utf-8")
        self.assertNotIn("@import", css)
        script = (SITE / "app.js").read_text(encoding="utf-8")
        for forbidden in ("fetch(", "XMLHttpRequest", "localStorage", "document.cookie", "sendBeacon", "eval("):
            self.assertNotIn(forbidden, script)

    def test_unavailable_downloads_have_no_fake_links(self):
        from html.parser import HTMLParser
        class Buttons(HTMLParser):
            def __init__(self):
                super().__init__(); self.downloads = []
            def handle_starttag(self, tag, attrs):
                attrs = dict(attrs)
                if tag == "button" and attrs.get("aria-label", "").startswith("Download for"):
                    self.downloads.append(attrs)
        config = builder.load_config()
        parser = Buttons()
        parser.feed((SITE / "index.html").read_text(encoding="utf-8"))
        unavailable = sum(not config[p]["download_url"] for p in ("macos", "windows"))
        self.assertEqual(len(parser.downloads), 2 * unavailable)
        for button in parser.downloads:
            self.assertIn("disabled", button)
        home = (SITE / "index.html").read_text(encoding="utf-8")
        self.assertEqual("Developer ID signed and Apple notarized" in home,
                         bool(config["macos"].get("signed_notarized")))
        for platform in ("macos", "windows"):
            url = config[platform]["download_url"]
            if url:
                self.assertEqual(self.pages["index.html"].links.count(url), 2)

    def test_configuration_rejects_unsafe_urls_and_legacy_artifacts(self):
        for value in ("javascript:alert(1)", "http://example.com/file.dmg", "https://user:pass@example.com/file"):
            with self.assertRaises(ValueError):
                builder.config_url(value, "test")
        config = builder.load_config()
        config["macos"]["download_url"] = "https://example.com/AgentMeter-1.1.1.dmg"
        with patch.object(builder.json, "loads", return_value=config):
            with self.assertRaises(ValueError):
                builder.load_config()

    def test_configured_download_and_canonical_rendering(self):
        config = builder.load_config()
        config["macos"]["download_url"] = "https://example.com/Llumi-test.dmg"
        with tempfile.TemporaryDirectory() as directory, patch.object(builder, "load_config", return_value=config):
            builder.build(Path(directory), "https://example.com/AgentMeter/")
            home = (Path(directory) / "index.html").read_text(encoding="utf-8")
            self.assertEqual(home.count('href="https://example.com/Llumi-test.dmg"'), 2)
            self.assertIn('rel="canonical" href="https://example.com/AgentMeter/"', home)
            self.assertIn('https://example.com/AgentMeter/media/llumi-social.png', home)
            policy = (Path(directory) / "privacy/index.html").read_text(encoding="utf-8")
            self.assertIn('href="../#download"', policy)

    def test_approved_brand_is_unchanged(self):
        self.assertEqual((SITE / "mark.svg").read_bytes(), (ROOT / "assets/brand/llumi-dark.svg").read_bytes())

    def test_policy_renderer_fails_on_new_structure(self):
        for body in ("# Privacy\n\n- new list", "# Privacy\n\n## new heading", "# Privacy\n\n[link](https://example.invalid)"):
            with self.assertRaises(ValueError):
                builder.render_privacy(body)

    def test_policy_renderer_escapes_html(self):
        rendered = builder.render_privacy("# Privacy\n\n<script>alert(1)</script>")
        self.assertNotIn("<script>", rendered)
        self.assertIn("&lt;script&gt;", rendered)


if __name__ == "__main__":
    unittest.main()
