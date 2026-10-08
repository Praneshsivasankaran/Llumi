import { test } from "node:test";
import assert from "node:assert/strict";
import worker from "../worker.mjs";

test("www and HTTP redirect to canonical HTTPS, retaining path and query", async () => {
  for (const host of ["https://www.tryllumi.com", "http://www.tryllumi.com", "http://tryllumi.com"]) {
    const result = await worker.fetch(new Request(host + "/privacy/?from=test"), {});
    assert.equal(result.status, 301);
    assert.equal(result.headers.get("location"), "https://tryllumi.com/privacy/?from=test");
  }
});

test("canonical routes pass through to static assets with original status", async () => {
  const request = new Request("https://tryllumi.com/missing/");
  const result = await worker.fetch(request, { ASSETS: { fetch: async (actual) => {
    assert.equal(actual, request);
    return new Response("Not found", { status: 404 });
  } } });
  assert.equal(result.status, 404);
});

test("release version URLs survive canonical redirects", async () => {
  const result = await worker.fetch(new Request("http://www.tryllumi.com/releases/macos/1.1.2/?from=app"), {});
  assert.equal(result.status, 301);
  assert.equal(result.headers.get("location"), "https://tryllumi.com/releases/macos/1.1.2/?from=app");
});
