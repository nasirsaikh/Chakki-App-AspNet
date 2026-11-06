/* Runs in <head> before the page paints: resolves "match my device" to light or dark so there is no flash of the
 * wrong colours. The server already renders an explicit light or dark choice. */
(() => {
  const root = document.documentElement;
  if ((root.dataset.caMode || "system") !== "system") return;
  const dark = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
  root.setAttribute("data-bs-theme", dark ? "dark" : "light");
})();
