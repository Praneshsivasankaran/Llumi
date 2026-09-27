# Llumi website

The canonical production home is **https://tryllumi.com/**, hosted on Cloudflare Workers with static assets. `www.tryllumi.com` and HTTP redirect to the HTTPS apex while preserving paths and query strings. Privacy and support live at `/privacy/` and `/support/`.

The approved website uses HTML, CSS, one small browser script and a Python standard-library builder. It has no analytics, external fonts, cookies, forms or runtime network requests. `PRIVACY.md` remains the authoritative substantive policy, rendered with exact visible-text parity checks.

## Development and verification

```sh
python scripts/build-site.py
python scripts/test-site.py
python scripts/check-public-tree.py
python macos/Scripts/privacy-check.py
node --test site/tests/routing.test.mjs
python -m http.server 4173 --bind 127.0.0.1 --directory _site
```

Optional browser validation:

```sh
npm ci --prefix site/tests
npx --prefix site/tests playwright install chromium firefox webkit
node site/tests/check.cjs
```

The browser harness covers Chromium, Firefox and WebKit; desktop and narrow layouts; accessibility, keyboard behavior, appearance, reduced motion, asset loading and absent third-party requests. WebKit testing is not physical Safari acceptance. Output remains in ignored `_site/` and `.review/`.

## Configuration and deployment

`site/config.json` supplies canonical/OG URLs, the canonical GitHub repository and download status. Both platform downloads stay disabled until their actual Llumi release is public. The final macOS target is Llumi 1.1.1; Windows 2.0.2.0 remains pending Microsoft Store certification. No unsigned Windows download is offered.

`wrangler.jsonc` declares only the two owner-confirmed website domains. `site/worker.mjs` implements canonical redirects, then delegates to static assets. Deployment is explicit with `wrangler deploy` after build, tests and authorization. Do not alter unrelated DNS or mail records. The GitHub website workflow validates and packages output; it does not deploy automatically.

## Historical Store URL compatibility

Preserve these exact, case-sensitive routes during certification:

- https://praneshsivasankaran.github.io/AgentMeter/
- https://praneshsivasankaran.github.io/AgentMeter/privacy/
- https://praneshsivasankaran.github.io/AgentMeter/support/

The account Pages repository `Praneshsivasankaran/Praneshsivasankaran.github.io` contains a frozen copy of the historical public pages under `AgentMeter/`. This preserves the URLs independently of the product repository rename. Do not recreate a repository named AgentMeter, which would replace GitHub's old-repository redirect. Verify the old routes after any Pages configuration changes.

See [release handoff](RELEASE-HANDOFF.md) for draft assets and the separate public-download gate. Website publication does not authorize either GitHub release publication or Microsoft Publish now.
