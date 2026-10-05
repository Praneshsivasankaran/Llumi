const { test } = require("node:test");
const assert = require("node:assert/strict");
const { initialState, reduce, presentation } = require("../review/update-flow.js");

function run(state, ...actions) { return actions.reduce((current, type) => reduce(current, { type }), state); }
function install(mode) { return run(initialState(mode), mode === "automatic" ? "enable" : "check", "checked", "download", "downloaded", "install"); }

for (const mode of ["manual", "automatic"]) {
  test(`${mode}: confirmation waits for installation and successful new-version launch`, () => {
    let state = initialState(mode);
    const actions = [mode === "automatic" ? "enable" : "check", "checked", "download", "downloaded", "install"];
    for (const type of actions) { state = reduce(state, { type }); assert.equal(state.popup, false); }
    assert.equal(state.installedVersion, "1.1.3");
    state = reduce(state, { type: "launch" });
    assert.equal(state.popup, true);
    assert.equal(state.phase, "launched");
    assert.equal(state.automaticOptIn, mode === "automatic");
  });
  test(`${mode}: dismissal is remembered for this version during ordinary relaunch`, () => {
    let state = run(install(mode), "launch", "acknowledge", "relaunch");
    assert.equal(state.popup, false);
    assert.deepEqual(state.acknowledgedVersions, ["1.1.3"]);
    assert.match(presentation(state).status, /ordinary relaunch/i);
    state = run(state, "retry", "launch", "acknowledge");
    assert.equal(state.popup, false);
    assert.deepEqual(state.acknowledgedVersions, ["1.1.3"]);
  });
  for (const outcome of ["fail", "cancel"]) {
    test(`${mode}: ${outcome} does not install or announce success`, () => {
      const state = run(initialState(mode), mode === "automatic" ? "enable" : "check", "checked", "download", outcome, "launch");
      assert.equal(state.installedVersion, "1.1.2");
      assert.equal(state.popup, false);
      assert.equal(state.phase, outcome === "fail" ? "failed" : "canceled");
    });
  }
}

test("invalid, skipped and repeated steps cannot manufacture completion", () => {
  const state = run(initialState(), "launch", "install", "downloaded", "acknowledge", "unknown");
  assert.deepEqual(state, initialState());
  assert.equal(reduce(state, null), state);
  assert.equal(reduce(state, { type: 42 }), state);
});
test("automatic updates ask to restart; Later preserves the current version without success", () => {
  const ready = run(initialState("automatic"), "enable", "checked", "download", "downloaded");
  assert.equal(presentation(ready).product, "Restart and update");
  assert.equal(presentation(ready).later, true);
  const later = reduce(ready, { type: "later" });
  assert.equal(later.phase, "deferred");
  assert.equal(later.installedVersion, "1.1.2");
  assert.equal(later.popup, false);
  assert.equal(run(later, "launch").popup, false);
  assert.equal(run(later, "install", "launch").popup, true);
});
test("first installation and ordinary launch never claim an update", () => {
  for (const state of [run(initialState(), "relaunch"), run(initialState(), "firstInstall", "relaunch")]) assert.equal(state.popup, false);
});
test("mode change and explicit start-over reset the review simulation only", () => {
  const complete = run(install("automatic"), "launch", "acknowledge");
  assert.deepEqual(reduce(complete, { type: "reset" }), initialState("automatic"));
  assert.deepEqual(reduce(complete, { type: "mode", mode: "manual" }), initialState());
});
test("installation is not canceled retroactively and cannot relaunch around the success gate", () => {
  const installed = install("manual");
  for (const action of ["cancel", "fail", "relaunch"]) assert.deepEqual(reduce(installed, { type: action }), installed);
});
test("all phases have readable display copy without persistent or network state", () => {
  let state = initialState("automatic");
  for (const type of [null, "enable", "checked", "download", "downloaded", "install", "launch", "acknowledge", "relaunch"]) {
    if (type) state = reduce(state, { type });
    const view = presentation(state);
    assert.equal(typeof view.title, "string");
    assert.equal(typeof view.description, "string");
    assert.equal(typeof view.status, "string");
    assert.match(view.status, /^Preview:/);
  }
});
