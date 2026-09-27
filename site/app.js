// Small, progressively enhanced product illustrations. No requests or storage.
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const systemDark = window.matchMedia("(prefers-color-scheme: dark)");

// Demonstrate expansion once on arrival, then leave control with the visitor.
for (const notch of document.querySelectorAll(".notch")) {
  const button = notch.querySelector(".notch-toggle");
  const details = notch.querySelector(".notch-details");
  let pinned = false;
  let interacted = false;
  let timer;
  let observer;
  const expand = (open) => {
    button.setAttribute("aria-expanded", String(open));
    details.hidden = !open;
  };
  const takeControl = () => {
    interacted = true;
    clearTimeout(timer);
    observer?.disconnect();
  };
  const demonstrate = () => {
    if (!interacted) {
      pinned = true;
      expand(true);
    }
  };
  if (reducedMotion.matches) {
    demonstrate();
  } else if ("IntersectionObserver" in window) {
    observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        observer.disconnect();
        timer = setTimeout(demonstrate, 750);
      }
    }, { threshold: 0.5 });
    observer.observe(notch);
  } else {
    demonstrate();
  }
  reducedMotion.addEventListener("change", (event) => {
    if (event.matches) {
      clearTimeout(timer);
      observer?.disconnect();
      demonstrate();
    }
  });
  notch.addEventListener("pointerenter", (event) => {
    if (event.pointerType === "mouse") {
      takeControl();
      expand(true);
    }
  });
  notch.addEventListener("pointerleave", () => {
    if (!pinned) expand(false);
  });
  notch.addEventListener("focusin", takeControl);
  button.addEventListener("click", () => {
    takeControl();
    pinned = !pinned;
    expand(pinned);
  });
  notch.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      takeControl();
      pinned = false;
      expand(false);
      button.focus();
    }
  });
  notch.addEventListener("focusout", (event) => {
    if (!notch.contains(event.relatedTarget)) {
      pinned = false;
      expand(false);
    }
  });
}

const appearancePanel = document.querySelector(".appearance-panel");
const updateAppearance = () => {
  if (!appearancePanel) return;
  const choice = document.querySelector(
    'input[name="appearance"]:checked',
  ).value;
  appearancePanel.dataset.appearance =
    choice === "system" ? (systemDark.matches ? "dark" : "light") : choice;
};
document
  .querySelectorAll('input[name="appearance"]')
  .forEach((input) => input.addEventListener("change", updateAppearance));
systemDark.addEventListener("change", updateAppearance);

for (const button of document.querySelectorAll("[data-activity]")) {
  button.addEventListener("click", () => {
    document
      .querySelectorAll("[data-activity]")
      .forEach((step) =>
        step.setAttribute("aria-pressed", String(step === button)),
      );
    const state = button.dataset.activity;
    document
      .querySelector(".activity-monitor")
      .classList.toggle("is-away", state === "closed");
    document
      .querySelector(".activity-monitor")
      .setAttribute("aria-hidden", String(state === "closed"));
    document.querySelector(".activity-claude").hidden = state !== "both";
    document.querySelector(".activity-status").textContent = {
      codex: "Llumi appears with Codex.",
      both: "Both providers, together.",
      closed: "Sessions closed. Llumi gets out of the way.",
    }[state];
  });
}

// Content is visible before JS and stays visible if observers are unavailable.
if ("IntersectionObserver" in window && !reducedMotion.matches) {
  const observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          entry.target.classList.add("revealed");
          observer.unobserve(entry.target);
        }
      }
    },
    { threshold: 0.08 },
  );
  document
    .querySelectorAll(".reveal")
    .forEach((section) => observer.observe(section));
}
const header = document.querySelector(".header");
const updateHeader = () =>
  header.classList.toggle("scrolled", window.scrollY > 12);
window.addEventListener("scroll", updateHeader, { passive: true });
updateHeader();
