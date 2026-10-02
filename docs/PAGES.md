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

Local output checks cannot detect scripts injected by production hosting. After deployment or hosting analytics changes, verify the actual public site:

```sh
npm run --prefix site/tests test:privacy-regression
npm run --prefix site/tests test:production-privacy
```

The production check requests home, privacy and support HTML both normally and with cache bypass, then loads both URL forms in fresh anonymous Chromium, Firefox and WebKit contexts. It allows only the intended app script and static resources, observes requests without blocking them, waits five seconds, and navigates to another canonical page with a further observation window for pagehide/sendBeacon traffic. A DOM observer also detects transient injected scripts. Because browsers can omit pagehide keepalive requests from network events, passive wrappers additionally record sendBeacon/fetch attempts while forwarding the original arguments and return value unchanged; reports distinguish attempts from browser network events. A unique test-only sessionStorage ledger retains those attempt metadata across document teardown inside the disposable context; it contains no payload or query values and disappears when the context closes. Unexpected external scripts, analytics requests, same-origin `/cdn-cgi/rum` POSTs and other runtime requests fail the check. Ordinary GitHub/Microsoft download anchors are allowed; the checker does not follow those external downloads. The synthetic regressions prove that actual delayed and pagehide telemetry reaches the fixture server and is detected.

Results are saved to ignored `.review/production-privacy.json`; request bodies, cookie values, query identifiers and page contents are not recorded. `LLUMI_PRIVACY_OUTPUT` can select a separate evidence file. A failure or unavailable browser is not a pass. This is a bounded observation, not proof against every future conditional or delayed request. The existing website workflow always tests the checker with synthetic fixtures; its manual `verify_production_privacy` option also runs the deployed check. These checks neither deploy nor change hosting settings.

## Configuration and deployment

`site/config.json` supplies canonical/OG URLs, the canonical GitHub repository and download status. macOS links to the verified public Llumi 1.1.1 DMG. Windows 2.0.2.0 is published in Microsoft Store. Both Windows download links point to product `9NV153Q5K5MQ`, enabled with owner authorization after public metadata verification and physical installation acceptance. No unsigned Windows download is offered.

`wrangler.jsonc` declares only the two owner-confirmed website domains. `site/worker.mjs` implements canonical redirects, then delegates to static assets. Deployment is explicit with `wrangler deploy` after build, tests and authorization. Do not alter unrelated DNS or mail records. The GitHub website workflow validates and packages output; it does not deploy automatically.

## Historical Store URL compatibility

Preserve these exact, case-sensitive historical routes while Store propagation and installed-client migration still depend on them:

- https://praneshsivasankaran.github.io/AgentMeter/
- https://praneshsivasankaran.github.io/AgentMeter/privacy/
- https://praneshsivasankaran.github.io/AgentMeter/support/

The account Pages repository `Praneshsivasankaran/Praneshsivasankaran.github.io` contains a frozen copy of the historical public pages under `AgentMeter/`. This preserves the URLs independently of the product repository rename. Do not recreate a repository named AgentMeter, which would replace GitHub's old-repository redirect. Verify the old routes after any Pages configuration changes.

See [release handoff](RELEASE-HANDOFF.md) for the published macOS artifact receipt and independent release gates. Website publication does not authorize future GitHub releases or Microsoft Publish now.
