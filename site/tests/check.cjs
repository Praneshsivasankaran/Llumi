const { chromium, firefox, webkit } = require("playwright");
const AxeBuilder = require("@axe-core/playwright").default;
const { HtmlValidate } = require("html-validate");
const fs = require("fs");
const path = require("path");
const assert = require("node:assert/strict");
const root = path.resolve(__dirname, "../..");
const output = path.join(root, ".review");
fs.mkdirSync(output, { recursive: true });
const results = [];
(async () => {
  const validator = new HtmlValidate({
    extends: ["html-validate:recommended"],
    rules: {
      "no-inline-style": "off",
      "long-title": "off",
      "prefer-native-element": "off",
    },
  });
  for (const route of [
    "index.html",
    "privacy/index.html",
    "support/index.html",
  ]) {
    const result = await validator.validateFile(
      path.join(root, "_site", route),
    );
    results.push({
      html: route,
      valid: result.valid,
      messages: result.results.flatMap((r) => r.messages),
    });
  }
  for (const [engine, type] of Object.entries({ chromium, webkit, firefox })) {
    const browser = await type.launch({ headless: true });
    const context = await browser.newContext({
      viewport: { width: 1440, height: 1000 },
      colorScheme: "light",
    });
    const page = await context.newPage();
    const errors = [],
      badResponses = [],
      remote = [];
    page.on("pageerror", (e) => errors.push(e.message));
    page.on("response", (r) => {
      if (r.status() >= 400) badResponses.push(r.url() + ":" + r.status());
    });
    page.on("request", (r) => {
      if (!r.url().startsWith("http://127.0.0.1:4173")) remote.push(r.url());
    });
    for (const width of [1440, 1024, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto("http://127.0.0.1:4173/");
      await page.locator(".app-screenshot").scrollIntoViewIfNeeded();
      await page.waitForFunction(() => {
        const img = document.querySelector(".app-screenshot");
        return img.complete && img.naturalWidth > 0;
      });
      assert.equal(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
        true,
        engine + width + " overflow",
      );
      assert.equal(await page.locator("button[disabled]").count(), 4);
      const scan = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
        .analyze();
      results.push({
        engine,
        width,
        overflow: false,
        axe: scan.violations.map((v) => ({
          id: v.id,
          impact: v.impact,
          nodes: v.nodes.map((n) => ({
            target: n.target,
            summary: n.failureSummary,
          })),
        })),
      });
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto("http://127.0.0.1:4173/");
    await page.waitForFunction(() => document.querySelector('.hero .notch-toggle').getAttribute('aria-expanded') === 'true');
    await page.keyboard.press(engine === "webkit" ? "Alt+Tab" : "Tab");
    assert.equal(
      await page
        .locator(".skip")
        .evaluate((el) => el === document.activeElement),
      true,
    );
    assert.notEqual(
      await page
        .locator(".skip")
        .evaluate((el) => getComputedStyle(el).outlineStyle),
      "none",
    );
    await page.keyboard.press("Enter");
    assert.equal(
      await page
        .locator("#main")
        .evaluate((el) => el === document.activeElement),
      true,
    );
    const notch = page.locator(".hero .notch-toggle");
    await notch.focus();
    await page.keyboard.press("Escape");
    await page.keyboard.press("Enter");
    assert.equal(await notch.getAttribute("aria-expanded"), "true");
    await page.keyboard.press("Escape");
    assert.equal(await notch.getAttribute("aria-expanded"), "false");
    await notch.hover();
    assert.equal(await notch.getAttribute("aria-expanded"), "true");
    await page.locator("h1").hover();
    assert.equal(await notch.getAttribute("aria-expanded"), "false");
    await page.getByLabel("Dark", { exact: true }).check();
    assert.equal(
      await page.locator(".appearance-panel").getAttribute("data-appearance"),
      "dark",
    );
    const darkScan = await new AxeBuilder({ page })
      .include(".appearance")
      .withTags(["wcag2aa"])
      .analyze();
    results.push({ engine, darkAxe: darkScan.violations });
    await page.getByLabel("System", { exact: true }).check();
    await page.emulateMedia({ colorScheme: "dark" });
    await page.waitForFunction(
      () =>
        document.querySelector(".appearance-panel").dataset.appearance ===
        "dark",
    );
    assert.equal(
      await page.locator(".appearance-panel").getAttribute("data-appearance"),
      "dark",
    );
    await page.emulateMedia({ colorScheme: "light" });
    await page.waitForFunction(
      () =>
        document.querySelector(".appearance-panel").dataset.appearance ===
        "light",
    );
    assert.equal(
      await page.locator(".appearance-panel").getAttribute("data-appearance"),
      "light",
    );
    await page.getByLabel("Light", { exact: true }).check();
    await page.locator("[data-activity=both]").click();
    assert.equal(await page.locator(".activity-claude").isVisible(), true);
    await page.locator("[data-activity=closed]").click();
    assert.equal(
      await page.locator(".activity-monitor").getAttribute("aria-hidden"),
      "true",
    );
    await page.locator("[data-activity=codex]").click();
    await page.locator(".nav-download").click();
    await page.waitForFunction(
      () =>
        location.hash === "#download" &&
        document.querySelector("#download").getBoundingClientRect().top >= 0 &&
        document.querySelector("#download").getBoundingClientRect().top <
          innerHeight / 2,
    );
    await page.emulateMedia({ reducedMotion: "reduce" });
    assert.equal(
      await page.evaluate(
        () => getComputedStyle(document.documentElement).scrollBehavior,
      ),
      "auto",
    );
    assert.equal(
      await page
        .locator(".activity-monitor")
        .evaluate((el) => getComputedStyle(el).transitionDuration),
      "0s",
    );
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("http://127.0.0.1:4173/");
    assert.equal(await page.locator('.hero .notch-toggle').getAttribute('aria-expanded'), 'true');
    await page.locator('.hero .notch-toggle').press('Escape');
    await page.locator(".hero .notch-toggle").click();
    assert.equal(
      await page.locator(".hero .notch-toggle").getAttribute("aria-expanded"),
      "true",
    );
    assert.equal(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
      true,
    );
    await page.locator(".hero .notch-toggle").click();
    for (const route of ["privacy/", "support/"]) {
      await page.goto("http://127.0.0.1:4173/" + route);
      const scan = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
        .analyze();
      results.push({ engine, route, axe: scan.violations });
      assert.equal(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
        true,
      );
    }
    if (engine === "chromium") {
      await page.goto("http://127.0.0.1:4173/");
      await page.locator(".app-screenshot").scrollIntoViewIfNeeded();
      await page.waitForFunction(() => {
        const img = document.querySelector(".app-screenshot");
        return img.complete && img.naturalWidth > 0;
      });
      await page.evaluate(() => {
        document.activeElement.blur();
        window.scrollTo(0, 0);
      });
      await page.screenshot({
        path: path.join(output, "mobile-homepage.png"),
        fullPage: true,
      });
      await page.setViewportSize({ width: 1440, height: 1000 });
      await page.goto("http://127.0.0.1:4173/");
      await page.locator(".app-screenshot").scrollIntoViewIfNeeded();
      await page.waitForFunction(() => {
        const img = document.querySelector(".app-screenshot");
        return img.complete && img.naturalWidth > 0;
      });
      await page.evaluate(() => {
        document.activeElement.blur();
        window.scrollTo(0, 0);
      });
      await page.screenshot({
        path: path.join(output, "desktop-homepage.png"),
        fullPage: true,
      });
      for (const [name, selector] of [
        ["hero", ".hero"],
        ["features", ".features"],
        ["privacy", ".privacy-section"],
        ["download", ".download-section"],
      ])
        await page
          .locator(selector)
          .screenshot({ path: path.join(output, name + ".png") });
    }
    results.push({
      engine,
      version: browser.version(),
      interactions: "pass",
      errors,
      badResponses,
      remote,
    });
    await browser.close();
    fs.writeFileSync(
      path.join(output, "validation.json"),
      JSON.stringify(results, null, 2),
    );
  }
  console.log(JSON.stringify(results, null, 2));
  assert.equal(
    results.some(
      (r) =>
        r.valid === false ||
        r.axe?.length ||
        r.darkAxe?.length ||
        r.errors?.length ||
        r.badResponses?.length ||
        r.remote?.length,
    ),
    false,
    "Validation findings must be resolved",
  );
})().catch((e) => {
  fs.writeFileSync(
    path.join(output, "validation.json"),
    JSON.stringify(results, null, 2),
  );
  console.error(e);
  process.exit(1);
});
