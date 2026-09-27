# Llumi website

A dependency-free static site: HTML, CSS, one small browser script and a Python
standard-library builder. Desktop application source is separate and unchanged.
The website is a local-review candidate, not a published Llumi release.

## Local development

From the repository root, using Python 3:

```sh
python3 scripts/build-site.py
python3 scripts/test-site.py
python3 scripts/check-public-tree.py
python3 macos/Scripts/privacy-check.py
python3 -m http.server 4173 --bind 127.0.0.1 --directory _site
```

Open `http://localhost:4173/`. Rebuild and refresh after edits. The generated
`_site/` and review-only `.review/` directories are ignored. No npm install is
needed to build or serve the website. The site has no external font requests,
analytics, cookies, trackers, forms or runtime network requests.

## Source and configuration

- `site/index.html`: Navigation → Hero → Features/product showcase → Privacy → Download → Footer, with shared navigation/footer in `site/layout.html`.
- `site/styles.css` and `site/app.js`: responsive light-first styling, automatic one-time monitor expansion, manual disclosure, activity steps, appearance selection and reduced-motion-aware transitions. The monitor stays expanded after its demonstration; reduced motion shows the expanded state immediately.
- `site/config.json`: single source for versions, actual download URLs, GitHub URL, canonical base URL and the macOS signing claim.
- `site/media/README.md`: exact provenance of real media and illustrations.
- `PRIVACY.md`: authoritative policy, rendered verbatim with exact visible-text parity tests.
- `site/support.html`: troubleshooting and public GitHub issue/security routing.

Set a platform's `download_url` only after the final Llumi artifact exists and
has been checked. Null generates genuinely disabled buttons and visible pending
copy in both the hero and download section. Set the release version at the same
time. The Windows source candidate has a private development version; the site
intentionally has no final Windows release version yet. The macOS candidate is
1.1.1; this is not a claim that its DMG is published.

Never link an AgentMeter binary as Llumi. `signed_notarized` remains false until
fresh evidence exists for the exact final Llumi artifact. The builder rejects a
signing claim without a download and obvious legacy AgentMeter asset filenames.
These checks do not replace checking signing/notarization receipts.

Compatibility is based on the current source and review artifact: macOS 14+,
universal Apple Silicon + Intel review binary; Windows 10 version 2004+ / 11,
x64 source target. Final packages still require physical release acceptance.

`canonical_base_url` stays null until an authorized canonical destination is
chosen. A future build can override it with `--base-url https://example.com/path/`.
Canonical, `og:url` and absolute `og:image` are then emitted consistently. Without
one, title, description and Open Graph text are still present; no domain is invented.
Relative page/media links work at both `/` and the existing `/AgentMeter/` prefix.

## Validation

The standard-library tests check policy parity, internal links/assets and anchors,
brand integrity, safe configuration, disabled downloads, canonical rendering and
absence of trackers/external resources. They do not replace physical review.

For optional local browser checks, `site/tests/` contains a separate npm-based
validation harness. These tools never ship with the site:

```sh
npm ci --prefix site/tests
npx --prefix site/tests playwright install chromium firefox webkit
node site/tests/check.cjs
```

Keep the local server above running. The harness covers Chromium, Firefox and
WebKit, 320/390/768/1024/1440 px widths, axe WCAG A/AA checks, HTML validation,
keyboard/focus, hover and tap, light/dark/system, activity states, reduced motion,
assets and screenshots. Output stays in ignored `.review/`. Optional Lighthouse:
run it against localhost and save reports into `.review/`, with your installed
Chrome/Chromium executable configured. Browser-engine testing does not establish
physical-device Safari, Windows application acceptance or release readiness.

## Existing public URLs: preserve exactly

- Website: https://praneshsivasankaran.github.io/AgentMeter/
- Privacy: https://praneshsivasankaran.github.io/AgentMeter/privacy/
- Support: https://praneshsivasankaran.github.io/AgentMeter/support/

Microsoft Store metadata may still reference these case-sensitive paths. The
new build preserves all three routes. No redirect or metadata migration is
needed for this local source change. Do not rename the repository or remove
these paths without a separately authorized migration.

## Deployment boundary

Existing `pages.yml` deploys site changes pushed to **main**, or a manual workflow
dispatch on main. Pull requests build but do not deploy. Ordinary pushes to
`codex/llumi-website-v1` do not trigger Pages. This task does not edit the workflow,
Pages settings, DNS, domain, repository name, releases or Store metadata.

Do not merge to main or dispatch Pages before physical website review and explicit
publication authorization. Other repository CI may validate application source
on a development-branch push; those workflows do not sign or publish releases.
