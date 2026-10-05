/* Requires the explicitly local review build at 127.0.0.1:4174. */
const { chromium, webkit, firefox } = require("playwright");
const AxeBuilder = require("@axe-core/playwright").default;
const { HtmlValidate } = require("html-validate");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const base = "http://127.0.0.1:4174";
const output = process.env.LLUMI_FLOW_SCREEN_DIR ? path.resolve(process.env.LLUMI_FLOW_SCREEN_DIR) : path.resolve(__dirname, "../../.review/release-notes");
let checks = 0;
function check(value, expected, label) { assert.equal(value, expected, label); checks += 1; }

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const validator = new HtmlValidate({ extends: ["html-validate:recommended"], rules: { "no-inline-style": "off", "long-title": "off", "prefer-native-element": "off" } });
  const response = await fetch(base + "/review/update-flow/");
  check(response.ok, true, "local review route responds");
  const html = await validator.validateString(await response.text());
  check(html.valid, true, JSON.stringify(html.results.flatMap(result => result.messages)));
  const results = [];

  for (const [name, engine] of Object.entries({ chromium, webkit, firefox })) {
    const browser = await engine.launch({ headless: true });
    try {
      const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, colorScheme: "light" });
      const errors = [], remote = [], badResponses = [];
      context.on("request", request => { if (!request.url().startsWith(base + "/")) remote.push(request.url()); });
      context.on("page", page => page.on("pageerror", error => errors.push(error.message)));
      context.on("response", response => { if (response.status() >= 400) badResponses.push(response.url() + ":" + response.status()); });
      await context.route("**/*", route => route.request().url().startsWith(base + "/") ? route.continue() : route.abort());
      const page = await context.newPage();
      await page.goto(base + "/review/update-flow/");
      async function capture(filename, fullPage = true) {
        await page.evaluate(wholePage => {
          if (wholePage && document.activeElement instanceof HTMLElement) document.activeElement.blur();
          scrollTo({ top: 0, left: 0, behavior: "instant" });
        }, fullPage);
        await page.screenshot({ path: path.join(output, filename), fullPage });
      }
      async function scan(label) {
        const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
        check(result.violations.length, 0, name + " " + label + ": " + JSON.stringify(result.violations.map(item => ({ id: item.id, targets: item.nodes.map(node => node.target) }))));
      }
      for (const width of [1440, 390, 320]) {
        await page.setViewportSize({ width, height: 1000 });
        check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, name + width + " overflow");
        await scan("width " + width);
      }
      await page.setViewportSize({ width: 1440, height: 1000 });
      const product = page.locator("#flow-product-action");
      const next = page.locator("#flow-next");
      const popup = page.locator("#update-complete");
      const relaunch = page.locator('[data-flow-action="relaunch"]');
      const reset = page.locator('[data-flow-action="reset"]');
      const clickProduct = () => product.click();
      const clickNext = () => next.click();
      async function setMode(mode) { await page.locator(`input[name="update-mode"][value="${mode}"]`).check(); }
      async function reachReady(mode) {
        await setMode(mode);
        await reset.click();
        await clickProduct();
        await clickNext();
        if (name === "chromium" && mode === "manual") await capture("update-flow-manual.png");
        if (mode === "manual") await clickProduct(); else await clickNext();
        await clickNext();
        check(await popup.isVisible(), false, name + mode + " no confirmation before installation");
      }
      for (const mode of ["manual", "automatic"]) {
        await reachReady(mode);
        if (mode === "automatic") {
          check(await product.innerText(), "Restart and update", name + " automatic restart prompt");
          await scan("automatic restart prompt");
          if (name === "chromium") await capture("update-flow-ready.png");
          await page.locator("#flow-later").click();
          check(await popup.isVisible(), false, name + " Later no confirmation");
          check(await page.locator("#flow-version").innerText(), "Version 1.1.2", name + " Later keeps old version");
          check(await product.evaluate(element => element === document.activeElement), true, name + " Later returns focus to restart action");
        }
        await clickProduct();
        check(await popup.isVisible(), false, name + mode + " installation waits for launch");
        await clickNext();
        check(await popup.isVisible(), true, name + mode + " completion after new launch");
        await scan(mode + " completion");
        check(await page.locator("#complete-continue").evaluate(element => element === document.activeElement), true, name + mode + " dialog initial focus");
        for (let tab = 0; tab < 4; tab += 1) {
          await page.keyboard.press("Tab");
          check(await popup.evaluate(element => element.contains(document.activeElement)), true, name + mode + " dialog focus stays inside");
        }
        if (name === "chromium" && mode === "automatic") await capture("update-flow-complete.png", false);
        if (mode === "manual") await page.keyboard.press("Escape"); else await page.locator("#complete-continue").click();
        check(await popup.isVisible(), false, name + mode + " confirmation dismissed");
        check(await relaunch.evaluate(element => element === document.activeElement), true, name + mode + " visible focus restoration");
        await relaunch.click();
        check(await popup.isVisible(), false, name + mode + " one-time completion");
      }
      for (const mode of ["manual", "automatic"]) {
        await setMode(mode);
        for (const action of ["cancel", "fail"]) {
          await reset.click();
          await clickProduct();
          await page.locator(`[data-flow-action="${action}"]`).click();
          check(await popup.isVisible(), false, name + mode + action + " no confirmation");
          check(await page.locator("#flow-version").innerText(), "Version 1.1.2", name + mode + action + " no installation");
        }
      }
      await reset.click();
      await relaunch.click();
      check(await popup.isVisible(), false, name + " ordinary pre-update relaunch no confirmation");
      await reachReady("manual"); await clickProduct(); await clickNext();
      const notePagePromise = context.waitForEvent("page");
      await page.locator("#complete-notes").click();
      const notePage = await notePagePromise;
      await notePage.waitForLoadState();
      check(notePage.url(), base + "/releases/macos/1.1.3/", name + " exact-version patch notes link");
      check(await notePage.getByRole("heading", { name: "Llumi 1.1.3", exact: true }).count(), 1, name + " exact-version notes heading");
      check(await popup.isVisible(), false, name + " reading notes dismisses confirmation");
      if (name === "chromium") await notePage.screenshot({ path: path.join(output, "release-notes-1.1.3.png"), fullPage: true });
      await notePage.close();
      if (name === "chromium") {
        await page.goto(base + "/releases/");
        await capture("release-notes-history.png");
      }
      await page.emulateMedia({ reducedMotion: "reduce" });
      check(await page.evaluate(() => getComputedStyle(document.documentElement).scrollBehavior), "auto", name + " reduced motion");
      check(errors.length, 0, name + " JS errors " + JSON.stringify(errors));
      check(remote.length, 0, name + " external requests " + JSON.stringify(remote));
      check(badResponses.length, 0, name + " failed assets " + JSON.stringify(badResponses));
      results.push({ engine: name, widths: [1440, 390, 320], manual: "passed", automatic: "passed", later: "passed", dismissal: "passed", exactVersionNotes: "passed", canceled: "passed", failed: "passed", ordinaryRelaunch: "passed", accessibility: "passed", externalRequests: remote.length, scriptErrors: errors.length });
    } finally { await browser.close(); }
  }
  const result = { checks, results };
  fs.writeFileSync(path.join(output, "update-flow-checks.json"), JSON.stringify(result, null, 2) + "\n");
  console.log(JSON.stringify(result, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
