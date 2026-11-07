// Owner: integration (T10). The browser boundary (Rehearsal 7 finding KD-010): it starts the Limen kernel over the
// .NET WebAssembly engine and owns only browser mechanism (engine boot, the loading shell, the boot fault and its
// retry). It holds no application state: every view comes from the engine (IF41-APP), and every meaning lives there.
import { BrowserKernel } from "@echelon-foundry/limen";

const byId = (id) => document.getElementById(id);

const shell = {
  boot: byId("causa-boot"),
  status: byId("causa-boot-status"),
  fault: byId("causa-boot-fault"),
  faultMessage: byId("causa-boot-fault-message"),
  retry: byId("causa-boot-retry"),
  main: byId("causa-main"),
};

const showLoading = () => {
  shell.fault.hidden = true;
  shell.boot.hidden = false;
  shell.boot.setAttribute("aria-busy", "true");
  shell.status.textContent = "Loading Causa…";
};

const showReady = () => {
  shell.boot.hidden = true;
  shell.boot.setAttribute("aria-busy", "false");
  shell.fault.hidden = true;
  shell.main.hidden = false;
};

// The fault states what happened and what was preserved (the static text in index.html), never the error's internals.
const showFault = (what) => {
  shell.boot.hidden = true;
  shell.boot.setAttribute("aria-busy", "false");
  shell.main.hidden = true;
  shell.faultMessage.textContent = what;
  shell.fault.hidden = false;
  shell.retry.focus();
};

// Loads the engine once per page: dotnet.js from the published _framework and the single [JSExport] entry point of
// Causa.Host (src/Causa.Host/Host.cs).
// A failed dynamic import stays failed for that URL in the module map, so a retry imports a fresh URL.
let enginePromise;
let attempt = 0;
const loadEngine = () => {
  enginePromise ??= (async () => {
    const url = attempt === 0 ? "./_framework/dotnet.js" : `./_framework/dotnet.js?retry=${attempt}`;
    attempt += 1;
    const { dotnet } = await import(url);
    const runtime = await dotnet.create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    return exports.Causa.Host.EngineHost;
  })();
  enginePromise.catch(() => { enginePromise = undefined; });
  return enginePromise;
};

// The Limen transport: each browser message goes to the engine as JSON and its answer comes back as JSON.
const engineTransport = (engine) => ({
  async start() {},
  async dispatch(message) {
    return JSON.parse(engine.Dispatch(JSON.stringify(message)));
  },
});

let kernel;
const start = async () => {
  showLoading();
  try {
    const engine = await loadEngine();
    kernel?.dispose();
    kernel = new BrowserKernel(engineTransport(engine), document);
    await kernel.start();
    showReady();
  } catch (error) {
    console.error("Causa engine start failed", error);
    showFault("The application engine did not load in this browser.");
  }
};

shell.retry.addEventListener("click", () => { void start(); });

// Browser signals the engine needs (IF41-APP BrowserSignal), relayed as Limen events through hidden inputs: the
// browser's connectivity (SM-NETWORK, PERSIST-018) and a tick that fires the engine's reconcile timers (PERSIST-015).
const relay = (id, value) => {
  const el = byId(id);
  if (!el || shell.main.hidden) return;
  el.value = value;
  el.dispatchEvent(new Event("change", { bubbles: true }));
};
window.addEventListener("online", () => relay("causa-connectivity", "online"));
window.addEventListener("offline", () => relay("causa-connectivity", "offline"));
setInterval(() => relay("causa-tick", String(Date.now())), 1000);

// PERSIST-019: unsaved changes exist only in this tab. Leaving warns, and Disconnect asks first.
const unsaved = () => byId("causa-unsaved")?.value === "true";
window.addEventListener("beforeunload", (event) => {
  if (unsaved()) { event.preventDefault(); event.returnValue = ""; }
});
document.addEventListener("click", (event) => {
  const target = event.target instanceof Element ? event.target.closest('[data-event="UIA-DISCONNECT"]') : null;
  if (target && unsaved() && !window.confirm("You have unsaved changes in this tab. Disconnect anyway? They will be lost.")) {
    event.preventDefault();
    event.stopImmediatePropagation();
  }
}, true);

// Scroll context on Back and Forward (UI-011-O4): each history entry keeps its own scroll position, saved into
// history.state as the user scrolls and restored once the kernel has rendered the route the browser popped. Only the
// browser's own scroll offset is kept: no application state.
if ("scrollRestoration" in history) history.scrollRestoration = "manual";
let scrollSave;
window.addEventListener("scroll", () => {
  clearTimeout(scrollSave);
  scrollSave = setTimeout(() => {
    history.replaceState({ ...(history.state ?? {}), causaScrollY: window.scrollY }, "");
  }, 100);
}, { passive: true });
window.addEventListener("popstate", (event) => {
  const y = event.state?.causaScrollY;
  if (typeof y !== "number") return;
  // The kernel renders the popped route on the same popstate; restore after its view has been applied.
  requestAnimationFrame(() => requestAnimationFrame(() => window.scrollTo(0, y)));
});

void start();
