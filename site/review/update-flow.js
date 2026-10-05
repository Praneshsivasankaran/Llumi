/* Local review only: deterministic simulation, with no requests or storage. */
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.LlumiUpdateFlow = api;
  if (typeof document !== "undefined") api.mount(document);
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";
  const currentVersion = "1.1.2";
  const targetVersion = "1.1.3";
  const cancelable = new Set(["checking", "available", "downloading", "ready"]);

  function initialState(mode = "manual") {
    return { mode: mode === "automatic" ? "automatic" : "manual", phase: mode === "automatic" ? "preferences" : "settings", automaticOptIn: false, installedVersion: currentVersion, acknowledgedVersions: [], popup: false };
  }

  function reduce(state, action) {
    if (!action || typeof action.type !== "string") return state;
    if (action.type === "mode") return initialState(action.mode);
    if (action.type === "reset") return initialState(state.mode);
    if (action.type === "enable" && state.mode === "automatic" && state.phase === "preferences") return { ...state, phase: "checking", automaticOptIn: true };
    if (action.type === "check" && state.mode === "manual" && state.phase === "settings") return { ...state, phase: "checking" };
    const transitions = { checked: ["checking", "available"], download: ["available", "downloading"], downloaded: ["downloading", "ready"] };
    const transition = transitions[action.type];
    if (transition && state.phase === transition[0]) return { ...state, phase: transition[1], popup: false };
    if (action.type === "later" && state.mode === "automatic" && state.phase === "ready") return { ...state, phase: "deferred", popup: false };
    if (action.type === "install" && ["ready", "deferred"].includes(state.phase)) return { ...state, phase: "installed", installedVersion: targetVersion, popup: false };
    if (action.type === "launch" && state.phase === "installed") return { ...state, phase: "launched", popup: !state.acknowledgedVersions.includes(targetVersion) };
    if (action.type === "acknowledge" && state.popup) return { ...state, popup: false, acknowledgedVersions: [...new Set([...state.acknowledgedVersions, targetVersion])] };
    if (action.type === "relaunch" && !state.popup && state.phase !== "installed") return { ...state, phase: "relaunch", popup: false };
    if (action.type === "firstInstall") return { ...state, installedVersion: targetVersion, phase: "first-install", popup: false, acknowledgedVersions: [...new Set([...state.acknowledgedVersions, targetVersion])] };
    if ((action.type === "cancel" || action.type === "fail") && cancelable.has(state.phase)) return { ...state, phase: action.type === "cancel" ? "canceled" : "failed", popup: false };
    if (action.type === "retry" && ["failed", "canceled", "relaunch", "first-install"].includes(state.phase)) {
      if (state.installedVersion === targetVersion) return { ...state, phase: "launched", popup: false };
      return { ...state, phase: state.automaticOptIn ? "checking" : state.mode === "automatic" ? "preferences" : "settings", popup: false };
    }
    return state;
  }

  function presentation(state) {
    const auto = state.mode === "automatic";
    const items = {
      preferences: { label: "Settings", title: "Updates, automatically", description: "Turn on automatic updates for future versions of Llumi.", product: "Turn on automatic updates", action: "enable", status: "Preview: automatic updates are off. This choice affects only the simulation.", step: 0, setting: true },
      settings: { label: "Settings", title: "Updates", description: "Check for a new version whenever you're ready.", product: "Check for updates", action: "check", status: "Preview: Llumi 1.1.2 is open. No update has been installed.", step: 0 },
      checking: { label: "Updates", title: "Checking for updates…", description: "Looking for a newer version of Llumi.", next: "Show the available update", action: "checked", status: "Preview: a check is in progress. This is not a network request.", step: 1 },
      available: { label: "Updates", title: "Llumi 1.1.3 is available", description: "A new version of Llumi is ready to download.", [auto ? "next" : "product"]: auto ? "Next: automatic download" : "Download update", action: "download", status: auto ? "Preview: automatic updates are on. Next, the update downloads automatically." : "Preview: the update is available. You choose when to download it.", step: 1, notes: true },
      downloading: { label: "Updates", title: "Downloading Llumi 1.1.3…", description: "The update is downloading. Your current version is still available.", next: "Finish the download", action: "downloaded", status: "Preview: download in progress. No file is being downloaded.", step: 2, progress: true },
      ready: { label: "Updates", title: auto ? "Update ready" : "Ready to install", description: auto ? "Restart Llumi to install version 1.1.3, or choose Later to keep using this version." : "The downloaded update is ready. Installation is the next step.", product: auto ? "Restart and update" : "Install and reopen", action: "install", later: auto, status: auto ? "Preview: download complete. Llumi asks before restarting. Choosing Later does not install or show a success confirmation." : "Preview: download complete. You choose when to install and reopen Llumi.", step: 3, notes: true },
      deferred: { label: "Usage", title: "An update is ready", description: "Keep using Llumi 1.1.2. Restart when you're ready to install the update.", product: "Restart and update", action: "install", status: "Preview: restart postponed. The downloaded update is waiting; 1.1.2 remains installed and no success confirmation is shown.", step: 3 },
      installed: { label: "Updates", title: "Llumi 1.1.3 is installed", description: "Open the new version to finish the update.", next: "Open Llumi 1.1.3", action: "launch", status: "Preview: installation completed. The confirmation waits for a successful new-version launch.", step: 4 },
      launched: { label: "Usage", title: "Welcome back", description: "Llumi 1.1.3 is open and ready.", status: state.popup ? "Preview: the new version launched successfully. The update confirmation is open." : "Preview: confirmation dismissed for 1.1.3. An ordinary relaunch will not repeat it.", step: 5 },
      relaunch: { label: "Usage", title: "Welcome back", description: `Llumi ${state.installedVersion} is open.`, product: state.installedVersion === currentVersion ? "Return to updates" : undefined, action: "retry", status: "Preview: ordinary relaunch. No update confirmation is shown.", step: state.installedVersion === targetVersion ? 5 : 0 },
      canceled: { label: "Updates", title: "Update canceled", description: "Your current version of Llumi is still available.", product: "Return to updates", action: "retry", status: "Preview: the update was canceled. 1.1.2 remains installed; no success confirmation is shown.", step: 0 },
      failed: { label: "Updates", title: "Couldn't complete the update", description: "Your current version of Llumi is still available. You can try again.", product: "Try again", action: "retry", status: "Preview: the update failed. 1.1.2 remains installed; no success confirmation is shown.", step: 0 },
      "first-install": { label: "Usage", title: "Llumi is ready", description: "Welcome to Llumi 1.1.3.", status: "Preview: first installation. No update confirmation is shown.", step: 5 },
    };
    return items[state.phase];
  }

  function mount(doc) {
    const dialog = doc.getElementById("update-complete");
    if (!dialog) return;
    let state = initialState();
    let current;
    const product = doc.getElementById("flow-product-action");
    const next = doc.getElementById("flow-next");
    const steps = [...doc.getElementById("flow-steps").children];
    function dispatch(type, extra = {}) {
      const previousFocus = doc.activeElement;
      state = reduce(state, { type, ...extra });
      render();
      if (!state.popup && (type === "later" || ([product, next, doc.getElementById("flow-later")].includes(previousFocus) && previousFocus.hidden))) {
        const destination = !product.hidden ? product : !next.hidden ? next : doc.querySelector('[data-flow-action="relaunch"]');
        destination.focus({ preventScroll: true });
      }
    }
    function render() {
      current = presentation(state);
      doc.getElementById("flow-version").textContent = `Version ${state.installedVersion}`;
      doc.getElementById("flow-stage-label").textContent = current.label;
      doc.getElementById("flow-state-title").textContent = current.title;
      doc.getElementById("flow-state-description").textContent = current.description;
      doc.getElementById("flow-status").textContent = current.status;
      product.hidden = !current.product;
      if (current.product) product.textContent = current.product;
      next.hidden = !current.next;
      if (current.next) next.textContent = current.next;
      doc.getElementById("flow-later").hidden = !current.later;
      doc.getElementById("flow-progress").hidden = !current.progress;
      doc.getElementById("flow-inline-notes").hidden = !current.notes;
      doc.getElementById("flow-automatic-setting").hidden = !current.setting;
      doc.getElementById("flow-switch").classList.toggle("on", state.automaticOptIn);
      doc.querySelectorAll('input[name="update-mode"]').forEach(input => { input.checked = input.value === state.mode; });
      steps.forEach((step, index) => {
        step.textContent = [state.mode === "automatic" ? "Opt in" : "Check manually", "Update available", "Download", "Install", "Open the new version"][index];
        step.dataset.progress = index < current.step ? "complete" : "pending";
        if (index === current.step) step.setAttribute("aria-current", "step"); else step.removeAttribute("aria-current");
      });
      doc.querySelectorAll('[data-flow-action="cancel"], [data-flow-action="fail"]').forEach(button => { button.disabled = !cancelable.has(state.phase); });
      doc.querySelector('[data-flow-action="relaunch"]').disabled = state.popup || state.phase === "installed";
      if (state.popup && !dialog.open) dialog.showModal();
      if (!state.popup && dialog.open) {
        dialog.close();
        doc.querySelector('[data-flow-action="relaunch"]').focus({ preventScroll: true });
      }
    }
    product.addEventListener("click", () => dispatch(current.action));
    next.addEventListener("click", () => dispatch(current.action));
    doc.getElementById("flow-later").addEventListener("click", () => dispatch("later"));
    doc.querySelectorAll('input[name="update-mode"]').forEach(input => input.addEventListener("change", () => dispatch("mode", { mode: input.value })));
    doc.querySelectorAll("[data-flow-action]").forEach(button => button.addEventListener("click", () => dispatch(button.dataset.flowAction)));
    doc.getElementById("complete-continue").addEventListener("click", () => dispatch("acknowledge"));
    doc.getElementById("complete-notes").addEventListener("click", () => dispatch("acknowledge"));
    dialog.addEventListener("cancel", event => { event.preventDefault(); dispatch("acknowledge"); });
    dialog.addEventListener("close", () => { if (state.popup) dispatch("acknowledge"); });
    dialog.addEventListener("keydown", event => {
      if (event.key !== "Tab") return;
      const first = doc.getElementById("complete-notes");
      const last = doc.getElementById("complete-continue");
      event.preventDefault();
      (doc.activeElement === first ? last : first).focus();
    });
    render();
  }
  return { initialState, reduce, presentation, mount };
});
