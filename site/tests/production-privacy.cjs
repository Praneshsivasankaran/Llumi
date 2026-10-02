// Inspect the actual deployed site without intercepting or suppressing requests.
// Evidence deliberately excludes response bodies, cookies, query values and POST data.
const { createHash, randomUUID } = require("node:crypto");
const fs = require("node:fs/promises");
const path = require("node:path");
const playwright = require("playwright");

const routes = ["/", "/privacy/", "/support/"];
const resources = new Map([
  ["/app.js", "script"],
  ["/styles.css", "stylesheet"],
  ["/mark.svg", "image"],
  ["/media/codex.svg", "image"],
  ["/media/claude.svg", "image"],
  ["/media/llumi-macos-usage.webp", "image"],
  ["/media/llumi-macos-usage-small.webp", "image"],
  ["/media/llumi-social.png", "image"],
]);
const cacheHeaders = { "Cache-Control": "no-cache, no-store", Pragma: "no-cache" };

function safeURL(value) {
  const url = new URL(value);
  if (!["http:", "https:"].includes(url.protocol)) return url.protocol;
  return url.origin + url.pathname; // Never retain query identifiers or fragment values.
}

function checkScripts(scripts, pageURL) {
  const expected = new URL("/app.js", pageURL).href;
  const issues = [];
  if (scripts.length !== 1) issues.push(`Expected one app script; found ${scripts.length}`);
  for (const script of scripts) {
    if (!script.src || new URL(script.src, pageURL).href !== expected)
      issues.push(`Unexpected script: ${script.src ? safeURL(new URL(script.src, pageURL)) : "inline"}`);
    if (script.inline) issues.push("Unexpected inline script content");
    if (script.beacon) issues.push("Cloudflare analytics configuration is present");
  }
  return issues;
}

function decodeAttribute(value) {
  return value.replace(/&#(x[\da-f]+|\d+);?|&(amp|quot|apos|lt|gt);/gi, (_, numeric, name) =>
    numeric ? String.fromCodePoint(Number.parseInt(numeric.replace(/^x/i, ""), /^x/i.test(numeric) ? 16 : 10))
      : ({ amp: "&", quot: '"', apos: "'", lt: "<", gt: ">" })[name.toLowerCase()]);
}

function inspectHTML(html, pageURL) {
  // The supported generated markup contains one external script and no inline JS.
  // Browser DOM inspection below independently covers the browser's HTML parsing.
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi)].map((match) => {
    const src = match[1].match(/\bsrc\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/i);
    return {
      src: src ? decodeAttribute(src[1] ?? src[2] ?? src[3]) : null,
      inline: Boolean(match[2].trim()),
      beacon: /\bdata-cf-beacon\b/i.test(match[1]),
    };
  });
  return checkScripts(scripts, pageURL);
}

function requestIssue(request, { origin, documentURL, exitURL }) {
  const url = new URL(request.url);
  // Explicitly classify same-origin RUM before the general runtime request rule.
  if (url.pathname === "/cdn-cgi/rum" || url.pathname.startsWith("/cdn-cgi/rum/"))
    return "Cloudflare RUM request";
  if (request.method !== "GET") return "Unexpected non-GET request";
  if (url.origin !== origin) return "Unexpected third-party request";
  if (request.type === "document") {
    return request.mainFrame && [documentURL, exitURL].includes(url.href) ? null : "Unexpected document navigation";
  }
  if (url.search || url.hash || resources.get(url.pathname) !== request.type)
    return "Unexpected runtime request or resource";
  return null;
}

async function inspectHTTP(baseURL, route) {
  const samples = [];
  // Inspect ordinary HTML as well as a distinct cache-bypassing request.
  for (const bypass of [false, true]) {
    const target = new URL(route, baseURL);
    if (bypass) target.searchParams.set("llumi_privacy_check", randomUUID());
    const response = await fetch(target, {
      headers: cacheHeaders,
      redirect: "manual",
      signal: AbortSignal.timeout(30000),
    });
    const body = await response.text();
    const issues = inspectHTML(body, target);
    if (response.status !== 200) issues.push(`Expected HTTP 200; received ${response.status}`);
    if (!response.headers.get("content-type")?.includes("text/html")) issues.push("Expected HTML response");
    if (response.headers.has("set-cookie")) issues.push("Unexpected Set-Cookie header");
    samples.push({
      url: safeURL(target), cacheBypass: bypass, status: response.status,
      sha256: createHash("sha256").update(body).digest("hex"), issues,
    });
  }
  return samples;
}

async function inspectBrowserRoute(browser, baseURL, route, { dwellMs = 5000, settleMs = 1500, cacheBypass = true } = {}) {
  const target = new URL(route, baseURL);
  if (cacheBypass) target.searchParams.set("llumi_privacy_check", randomUUID());
  const exitURL = new URL(route === "/support/" ? "/privacy/" : "/support/", baseURL);
  exitURL.searchParams.set("llumi_privacy_exit", randomUUID());
  const attemptKey = "llumi-privacy-check-" + randomUUID();
  const origin = target.origin;
  // No existing profile, storage state, credentials or extension is loaded.
  // Service workers are not blocked; registering one is itself a finding.
  const context = await browser.newContext({ extraHTTPHeaders: cacheHeaders });
  const result = { url: safeURL(target), requests: [], issues: [], dwellMs, settleMs, cacheBypass, pagehideNavigation: false };
  const pendingResponses = [];
  const observe = (observed, source) => {
    const issue = requestIssue(observed, { origin, documentURL: target.href, exitURL: exitURL.href });
    result.requests.push({ ...observed, url: safeURL(observed.url), hasQuery: observed.hasQuery ?? Boolean(new URL(observed.url).search), source, issue });
    if (issue) result.issues.push(`${issue}: ${observed.method} ${safeURL(observed.url)}`);
  };
  // Chromium can omit pagehide keepalive traffic from its request event stream.
  // Record native API attempts as separate evidence, preserving the original call,
  // arguments and return value. This supplements, never replaces, network events.
  await context.addInitScript(({ attemptKey, exitURL }) => {
    // A synchronous, test-only per-tab ledger survives document teardown, where
    // Chromium can discard both request and exposed-binding events. No user
    // profile is used; this storage disappears with the fresh context.
    let recorderReady = false;
    try {
      if (sessionStorage.getItem(attemptKey) === null && location.href !== exitURL)
        sessionStorage.setItem(attemptKey, JSON.stringify({ version: 1, attempts: [] }));
      const ledger = JSON.parse(sessionStorage.getItem(attemptKey));
      recorderReady = ledger?.version === 1 && Array.isArray(ledger.attempts);
    } catch { /* Report unavailable observation below instead of passing. */ }
    Object.defineProperty(window, "__llumiPrivacyRecorderReady", { get: () => recorderReady });
    const scripts = [];
    const seen = new Set();
    const capture = (node) => {
      if (node.nodeType !== Node.ELEMENT_NODE) return;
      const candidates = node.matches("script") ? [node] : [...node.querySelectorAll("script")];
      for (const script of candidates) {
        if (seen.has(script)) continue;
        seen.add(script);
        scripts.push({ src: script.getAttribute("src"), inline: Boolean(script.textContent.trim()), beacon: script.hasAttribute("data-cf-beacon") });
      }
    };
    Object.defineProperty(window, "__llumiPrivacyScripts", { value: scripts });
    new MutationObserver((mutations) => {
      for (const mutation of mutations) for (const node of [...mutation.addedNodes, ...mutation.removedNodes]) capture(node);
    }).observe(document, { childList: true, subtree: true });
    const record = (url, method, type) => {
      try {
        const target = new URL(url, location.href);
        const ledger = JSON.parse(sessionStorage.getItem(attemptKey));
        ledger.attempts.push({ url: ["http:", "https:"].includes(target.protocol) ? target.origin + target.pathname : target.protocol,
          method, type, mainFrame: window === window.top, hasQuery: Boolean(target.search) });
        sessionStorage.setItem(attemptKey, JSON.stringify(ledger));
      } catch { recorderReady = false; }
    };
    navigator.sendBeacon = new Proxy(navigator.sendBeacon, { apply(native, receiver, args) {
      record(args[0], "POST", "beacon");
      return Reflect.apply(native, receiver, args);
    } });
    window.fetch = new Proxy(window.fetch, { apply(native, receiver, args) {
      try {
        const input = args[0];
        record(input instanceof Request ? input.url : input, String(args[1]?.method || (input instanceof Request ? input.method : "GET")).toUpperCase(), "fetch");
      } catch { recorderReady = false; }
      return Reflect.apply(native, receiver, args);
    } });
  }, { attemptKey, exitURL: exitURL.href });
  context.on("request", (request) => {
    let mainFrame = false;
    try { mainFrame = request.frame().parentFrame() === null; } catch { /* worker request */ }
    const observed = {
      url: request.url(), method: request.method(), type: request.resourceType(), mainFrame,
    };
    observe(observed, "browser-network");
  });
  context.on("response", (response) => {
    pendingResponses.push((async () => {
      try {
        if (response.status() >= 400) result.issues.push(`HTTP ${response.status()}: ${safeURL(response.url())}`);
        if (await response.headerValue("set-cookie")) result.issues.push(`Unexpected Set-Cookie: ${safeURL(response.url())}`);
      } catch { result.issues.push(`Response observation failed: ${safeURL(response.url())}`); }
    })());
  });
  context.on("requestfailed", (request) => result.issues.push(`Request failed: ${safeURL(request.url())}`));
  context.on("serviceworker", () => result.issues.push("Unexpected service worker"));
  context.on("page", (page) => {
    page.on("websocket", () => result.issues.push("Unexpected WebSocket"));
    page.on("pageerror", () => result.issues.push("Page JavaScript error"));
  });
  try {
    const page = await context.newPage();
    await page.goto(target.href, { waitUntil: "load", timeout: 30000 });
    // Trigger lazy resources and observe delayed analytics; never route/abort traffic.
    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    await page.waitForTimeout(dwellMs);
    const scripts = await page.evaluate(() => [...document.scripts].map((script) => ({
      src: script.getAttribute("src"), inline: Boolean(script.textContent.trim()),
      beacon: script.hasAttribute("data-cf-beacon"),
    })));
    result.issues.push(...checkScripts(scripts, target));
    result.issues.push(...checkScripts(await page.evaluate(() => window.__llumiPrivacyScripts), target));
    if (!await page.evaluate(() => window.__llumiPrivacyRecorderReady)) result.issues.push("Lifecycle request observation unavailable");
    // Leaving the page fires pagehide/visibility lifecycle handlers. Listen on the
    // context and keep it alive so sendBeacon/keepalive POSTs are observed too.
    await page.goto(exitURL.href, { waitUntil: "load" });
    result.pagehideNavigation = true;
    await page.waitForTimeout(settleMs);
    if (!await page.evaluate(() => window.__llumiPrivacyRecorderReady)) result.issues.push("Lifecycle request observation unavailable after navigation");
    for (const attempt of await page.evaluate((key) => JSON.parse(sessionStorage.getItem(key)).attempts, attemptKey))
      observe(attempt, "browser-api-attempt");
    await Promise.all(pendingResponses);
    if ((await context.cookies()).length) result.issues.push("Unexpected browser cookies");
  } catch (error) {
    result.issues.push(`Browser check failed (${error.name || "Error"})`);
  } finally {
    await context.close();
  }
  return result;
}

async function run() {
  const baseURL = process.env.LLUMI_PRIVACY_BASE_URL || "https://tryllumi.com";
  const base = new URL(baseURL);
  if (base.username || base.password || base.search || base.hash || base.pathname !== "/" ||
      (base.protocol !== "https:" && !(base.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(base.hostname))))
    throw new Error("Use an HTTPS site origin or a local HTTP test origin");
  const engines = (process.env.LLUMI_PRIVACY_ENGINES || "chromium,firefox,webkit").split(",");
  if (!engines.length || engines.some((engine) => !["chromium", "firefox", "webkit"].includes(engine)))
    throw new Error("Unknown browser engine");
  const report = { checkedAt: new Date().toISOString(), origin: base.origin, http: [], browsers: [], issues: [] };
  for (const route of routes) {
    try { report.http.push(...await inspectHTTP(baseURL, route)); }
    catch (error) { report.issues.push(`HTTP check failed for ${route} (${error.name || "Error"})`); }
  }
  for (const engine of engines) {
    let browser;
    try {
      browser = await playwright[engine].launch({ headless: true });
      for (const route of routes) for (const cacheBypass of [false, true]) report.browsers.push({
        engine, version: browser.version(), ...await inspectBrowserRoute(browser, baseURL, route, { cacheBypass }),
      });
    } catch (error) { report.issues.push(`${engine} unavailable or failed (${error.name || "Error"})`); }
    finally { if (browser) await browser.close(); }
  }
  report.passed = !report.issues.length && [...report.http, ...report.browsers].every((sample) => !sample.issues.length);
  const output = process.env.LLUMI_PRIVACY_OUTPUT || path.resolve(__dirname, "../../.review/production-privacy.json");
  await fs.mkdir(path.dirname(output), { recursive: true });
  await fs.writeFile(output, JSON.stringify(report, null, 2) + "\n");
  console.log(JSON.stringify(report, null, 2));
  if (!report.passed) process.exitCode = 1;
}

module.exports = { inspectHTML, requestIssue, inspectHTTP, inspectBrowserRoute };
if (require.main === module) run().catch((error) => { console.error(error.message); process.exitCode = 1; });
