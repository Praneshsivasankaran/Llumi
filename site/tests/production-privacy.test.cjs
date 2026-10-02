const { test } = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const playwright = require("playwright");
const { inspectHTML, requestIssue, inspectHTTP, inspectBrowserRoute } = require("./production-privacy.cjs");

const cleanHTML = `<!doctype html><html><head><script src="/app.js" defer></script></head><body>
<a href="https://github.com/Praneshsivasankaran/Llumi/releases/">GitHub release</a>
<a href="https://apps.microsoft.com/detail/9NV153Q5K5MQ">Microsoft Store</a></body></html>`;

test("raw HTML permits ordinary download anchors but rejects injected analytics and inline scripts", () => {
  assert.deepEqual(inspectHTML(cleanHTML, "https://site.invalid/"), []);
  const injected = cleanHTML.replace("</body>", '<script type="module" src="https://static.cloudflareinsights.com/beacon.min.js" data-cf-beacon="synthetic"></script></body>');
  assert.ok(inspectHTML(injected, "https://site.invalid/").some((issue) => issue.includes("Unexpected script")));
  assert.ok(inspectHTML(injected, "https://site.invalid/").some((issue) => issue.includes("analytics configuration")));
  assert.ok(inspectHTML(cleanHTML + "<script>/* synthetic */</script>", "https://site.invalid/").some((issue) => issue.includes("inline")));
});

test("request policy rejects same-origin RUM and other runtime channels, not approved assets", () => {
  const policy = { origin: "https://site.invalid", documentURL: "https://site.invalid/?check=fixture" };
  const make = (pathname, method, type) => ({ url: policy.origin + pathname, method, type, mainFrame: true });
  assert.equal(requestIssue(make("/app.js", "GET", "script"), policy), null);
  assert.equal(requestIssue(make("/?check=fixture", "GET", "document"), policy), null);
  for (const request of [make("/cdn-cgi/rum", "POST", "ping"), make("/collect", "POST", "fetch"), make("/app.js", "GET", "fetch"), make("/mark.svg?event=fixture", "GET", "image"),
    { url: "https://static.cloudflareinsights.com/beacon.min.js", method: "GET", type: "script" }]) {
    assert.ok(requestIssue(request, policy), JSON.stringify(request));
  }
});

async function fixture(t, appScript, { engine = "chromium", csp = null, redirect = false } = {}) {
  const received = [];
  const server = http.createServer((request, response) => {
    const url = new URL(request.url, "http://localhost");
    received.push({ pathname: url.pathname, method: request.method });
    if (redirect) { response.writeHead(302, { Location: "https://example.invalid/" }); response.end(); return; }
    if (url.pathname === "/cdn-cgi/rum") { request.resume(); response.writeHead(204); response.end(); return; }
    if (url.pathname === "/app.js" || url.pathname === "/injected.js") {
      response.setHeader("Content-Type", "text/javascript");
      response.end(url.pathname === "/app.js" ? appScript : "/* synthetic injected script */"); return;
    }
    response.setHeader("Content-Type", "text/html");
    if (csp) response.setHeader("Content-Security-Policy", csp);
    response.end(cleanHTML);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const browser = await playwright[engine].launch({ headless: true });
  t.after(() => browser.close());
  return { baseURL: `http://127.0.0.1:${server.address().port}`, browser, received };
}

test("real browser clean fixture passes including external anchors and pagehide", async (t) => {
  const f = await fixture(t, "/* approved app with no requests */");
  const raw = await inspectHTTP(f.baseURL, "/");
  assert.ok(raw.every((sample) => !sample.issues.length));
  const result = await inspectBrowserRoute(f.browser, f.baseURL, "/", { dwellMs: 150, settleMs: 150 });
  assert.deepEqual(result.issues, []);
  assert.equal(result.pagehideNavigation, true);
  assert.equal(result.requests.some((request) => request.url.includes("github.com") || request.url.includes("microsoft.com")), false);
});

test("real browser detects and actually delivers delayed same-origin telemetry", async (t) => {
  const f = await fixture(t, "setTimeout(() => fetch('/cdn-cgi/rum', {method: 'POST', body: 'synthetic'}), 75);");
  const result = await inspectBrowserRoute(f.browser, f.baseURL, "/privacy/", { dwellMs: 200, settleMs: 150 });
  assert.ok(result.issues.some((issue) => issue.includes("Cloudflare RUM request")));
  assert.ok(f.received.some((request) => request.pathname === "/cdn-cgi/rum" && request.method === "POST"));
});

for (const engine of ["chromium", "firefox", "webkit"]) test(`${engine} detects sendBeacon delivered only on pagehide`, async (t) => {
  const f = await fixture(t, "addEventListener('pagehide', () => navigator.sendBeacon('/cdn-cgi/rum', 'synthetic'));", { engine });
  const result = await inspectBrowserRoute(f.browser, f.baseURL, "/support/", { dwellMs: 150, settleMs: 300 });
  assert.equal(result.pagehideNavigation, true);
  assert.ok(result.issues.some((issue) => issue.includes("Cloudflare RUM request")));
  assert.ok(f.received.some((request) => request.pathname === "/cdn-cgi/rum" && request.method === "POST"));
});

test("ordinary URL observes telemetry which skips cache-bypassing URLs", async (t) => {
  const f = await fixture(t, "if (!location.search) navigator.sendBeacon('/cdn-cgi/rum', 'synthetic');");
  const result = await inspectBrowserRoute(f.browser, f.baseURL, "/", { dwellMs: 150, settleMs: 150, cacheBypass: false });
  assert.ok(result.issues.some((issue) => issue.includes("Cloudflare RUM request")));
  assert.ok(f.received.some((request) => request.pathname === "/cdn-cgi/rum" && request.method === "POST"));
});

test("observer detects inline script that executes and removes itself", async (t) => {
  const f = await fixture(t, "const script = document.createElement('script'); script.textContent = 'window.syntheticInlineExecuted=true;document.currentScript.remove()'; document.head.append(script);");
  const result = await inspectBrowserRoute(f.browser, f.baseURL, "/", { dwellMs: 150, settleMs: 150 });
  assert.ok(result.issues.some((issue) => issue.includes("inline")));
});

test("raw HTTP redirects cannot be mistaken for a clean target", async (t) => {
  const f = await fixture(t, "", { redirect: true });
  const raw = await inspectHTTP(f.baseURL, "/");
  assert.ok(raw.every((sample) => sample.status === 302 && sample.issues.some((issue) => issue.includes("Expected HTTP 200"))));
});

test("a browser-blocked script is a failure, not a privacy pass", async (t) => {
  const f = await fixture(t, "/* approved */", { csp: "script-src 'none'" });
  const result = await inspectBrowserRoute(f.browser, f.baseURL, "/", { dwellMs: 150, settleMs: 150 });
  assert.ok(result.issues.some((issue) => issue.includes("Request failed")));
});

test("missing lifecycle ledger fails closed instead of appearing as no requests", async (t) => {
  const f = await fixture(t, "sessionStorage.clear();");
  const result = await inspectBrowserRoute(f.browser, f.baseURL, "/", { dwellMs: 150, settleMs: 150 });
  assert.ok(result.issues.some((issue) => issue.includes("observation unavailable") || issue.includes("Browser check failed")));
});

test("real browser detects dynamically injected scripts absent from initial HTML", async (t) => {
  const f = await fixture(t, "const script = document.createElement('script'); script.src = '/injected.js'; document.head.append(script);");
  const result = await inspectBrowserRoute(f.browser, f.baseURL, "/", { dwellMs: 150, settleMs: 150 });
  assert.ok(result.issues.some((issue) => issue.includes("Unexpected script")));
  assert.ok(result.issues.some((issue) => issue.includes("Unexpected runtime request")));
  assert.ok(f.received.some((request) => request.pathname === "/injected.js"));
});
