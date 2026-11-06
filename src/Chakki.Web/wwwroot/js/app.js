/* Chakki ERP behaviour. No inline scripts (strict CSP): everything binds through data-* attributes. */
(() => {
  "use strict";
  const root = document.documentElement;

  // ---------------------------------------------------------------- Light / dark / system
  const MODES = ["system", "light", "dark"];
  const ICONS = { system: "bi-circle-half", light: "bi-sun", dark: "bi-moon-stars" };
  const applyMode = (mode) => {
    root.dataset.caMode = mode;
    const dark = mode === "dark" || (mode === "system" && matchMedia("(prefers-color-scheme: dark)").matches);
    root.setAttribute("data-bs-theme", dark ? "dark" : "light");
    document.querySelectorAll("[data-ca-theme-icon]").forEach((i) => { i.className = `bi ${ICONS[mode]}`; });
  };
  let mode = "system";
  try { mode = localStorage.getItem("chakki-mode") || "system"; } catch { /* storage blocked */ }
  applyMode(mode);
  document.addEventListener("click", (e) => {
    if (!e.target.closest("[data-ca-theme]")) return;
    mode = MODES[(MODES.indexOf(mode) + 1) % MODES.length];
    try { localStorage.setItem("chakki-mode", mode); } catch { /* ignore */ }
    applyMode(mode);
  });

  // ---------------------------------------------------------------- Navigation sheet closes as soon as a module is chosen
  document.addEventListener("click", (e) => {
    if (!e.target.closest("[data-ca-nav-link]")) return;
    const sheet = document.getElementById("ca-nav");
    if (sheet && window.bootstrap) window.bootstrap.Offcanvas.getOrCreateInstance(sheet).hide();
  });

  // ---------------------------------------------------------------- Alerts, confirmations, auto-submit
  const autodismiss = (scope) => scope.querySelectorAll("[data-ca-autodismiss]").forEach((el) => setTimeout(() => el.remove(), 5000));
  autodismiss(document);

  document.addEventListener("submit", (e) => {
    const msg = e.target.getAttribute("data-ca-confirm");
    if (msg && !window.confirm(msg)) { e.preventDefault(); e.stopImmediatePropagation(); }
  }, true);
  document.addEventListener("change", (e) => {
    if (e.target.matches("[data-ca-autosubmit]")) e.target.form?.requestSubmit();
  });

  // ---------------------------------------------------------------- HTMX feedback
  const feedback = () => document.getElementById("ca-action-feedback");
  document.body.addEventListener("htmx:beforeRequest", () => feedback()?.classList.remove("show"));
  document.body.addEventListener("htmx:afterSwap", (e) => {
    if (e.detail.target.id === "ca-action-feedback") {
      e.detail.target.classList.add("show");
      e.detail.target.scrollIntoView({ block: "nearest", behavior: "smooth" });
    }
    autodismiss(e.detail.target);
    initCharts(e.detail.target);
    scrollChat();
  });
  // 422 = business rule message; let HTMX swap it into the feedback box.
  document.body.addEventListener("htmx:beforeSwap", (e) => {
    if (e.detail.xhr.status === 422) { e.detail.shouldSwap = true; e.detail.isError = false; }
  });
  document.body.addEventListener("paymentSettled", () => {
    const zone = document.querySelector(".ca-toast-zone");
    if (!zone) return;
    const t = document.createElement("div");
    t.className = "alert alert-success";
    t.textContent = root.lang === "hi" ? "भुगतान मिल गया" : "Payment received";
    zone.appendChild(t);
    setTimeout(() => t.remove(), 5000);
  });

  // ---------------------------------------------------------------- Grinding order form: live totals and override reason
  const recalc = (form) => {
    let total = 0;
    form.querySelectorAll("[data-ca-line]").forEach((line) => {
      const sel = line.querySelector("select");
      const opt = sel?.selectedOptions[0];
      const sys = parseFloat(opt?.dataset.rate || "0");
      const rateInput = line.querySelector("[data-ca-rate]");
      const kg = parseFloat(line.querySelector("[data-ca-kg]")?.value || "0");
      if (rateInput && document.activeElement !== rateInput && (!rateInput.value || rateInput.dataset.auto === "1")) {
        rateInput.value = sys ? sys.toFixed(2) : ""; rateInput.dataset.auto = "1";
        if (opt) { rateInput.min = opt.dataset.min; rateInput.max = opt.dataset.max; }
      }
      const rate = parseFloat(rateInput?.value || "0");
      const reason = line.querySelector("[data-ca-reason]");
      if (reason) {
        const changed = sys && rate && Math.abs(rate - sys) > 0.0001;
        reason.closest("[data-ca-reason-wrap]")?.classList.toggle("d-none", !changed);
        reason.required = !!changed;
      }
      total += kg * rate;
    });
    const out = form.querySelector("[data-ca-total]");
    if (out) out.textContent = "₹" + total.toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  };
  document.addEventListener("input", (e) => {
    const form = e.target.closest("[data-ca-order-form]");
    if (!form) return;
    if (e.target.matches("[data-ca-rate]")) e.target.dataset.auto = "0";
    recalc(form);
  });
  document.addEventListener("change", (e) => {
    const form = e.target.closest("[data-ca-order-form]");
    if (!form) return;
    if (e.target.matches("select")) { const r = e.target.closest("[data-ca-line]")?.querySelector("[data-ca-rate]"); if (r) { r.value = ""; r.dataset.auto = "1"; } }
    recalc(form);
  });
  document.addEventListener("click", (e) => {
    const add = e.target.closest("[data-ca-add-line]");
    if (add) {
      const form = add.closest("[data-ca-order-form]");
      const lines = form.querySelectorAll("[data-ca-line]");
      const clone = lines[lines.length - 1].cloneNode(true);
      const index = lines.length;
      clone.querySelectorAll("input, select").forEach((el) => {
        el.name = el.name.replace(/\[\d+\]/, `[${index}]`);
        if (el.tagName === "INPUT") { el.value = ""; el.dataset.auto = "1"; }
      });
      lines[lines.length - 1].after(clone);
      recalc(form);
    }
    const remove = e.target.closest("[data-ca-remove-line]");
    if (remove) {
      const form = remove.closest("[data-ca-order-form]");
      if (form.querySelectorAll("[data-ca-line]").length > 1) remove.closest("[data-ca-line]").remove();
      recalc(form);
    }
  });
  document.querySelectorAll("[data-ca-order-form]").forEach(recalc);

  // ---------------------------------------------------------------- Chat composer
  const scrollChat = () => { const s = document.getElementById("chat-scroll"); if (s) s.scrollTop = s.scrollHeight; };
  scrollChat();
  document.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey && e.target.matches("[data-ca-submit-on-enter]")) {
      e.preventDefault();
      if (e.target.value.trim()) e.target.form.requestSubmit();
    }
  });
  document.body.addEventListener("htmx:afterRequest", (e) => {
    const form = e.detail.elt.closest?.("[data-ca-reset-on-success]");
    if (form && e.detail.successful) { form.reset(); document.getElementById("chat-empty")?.remove(); }
  });
  document.addEventListener("click", (e) => {
    const s = e.target.closest("[data-ca-suggest]");
    if (!s) return;
    const box = document.getElementById("chat-q");
    if (box) { box.value = s.dataset.caSuggest; box.form.requestSubmit(); }
  });
  // Show the question immediately while the answer is prepared.
  document.body.addEventListener("htmx:beforeRequest", (e) => {
    const form = e.detail.elt;
    if (!form.matches?.("[data-ca-chat-form]")) return;
    const q = form.querySelector("textarea")?.value.trim();
    if (!q) return;
    document.getElementById("chat-empty")?.remove();
    const div = document.createElement("div");
    div.className = "ca-chat-exchange ca-chat-pending";
    const inner = document.createElement("div"); inner.className = "ca-chat-q";
    const bubble = document.createElement("div"); bubble.className = "ca-chat-bubble"; bubble.textContent = q;
    inner.appendChild(bubble); div.appendChild(inner);
    document.getElementById("chat-thread")?.appendChild(div);
    scrollChat();
  });
  document.body.addEventListener("htmx:afterRequest", () => document.querySelectorAll(".ca-chat-pending").forEach((p) => p.remove()));

  // ---------------------------------------------------------------- Charts (ApexCharts, data from <script type="application/json">)
  function initCharts(scope) {
    if (!window.ApexCharts) return;
    (scope || document).querySelectorAll("[data-ca-chart]:not([data-ready])").forEach((el) => {
      const src = document.getElementById(el.dataset.caChart);
      if (!src) return;
      const cfg = JSON.parse(src.textContent);
      el.dataset.ready = "1";
      const dark = root.getAttribute("data-bs-theme") === "dark";
      const primary = getComputedStyle(root).getPropertyValue("--ca-primary").trim() || "#b45309";
      new window.ApexCharts(el, {
        chart: { type: cfg.type || "bar", height: 260, toolbar: { show: false }, animations: { enabled: false }, background: "transparent", fontFamily: "inherit" },
        theme: { mode: dark ? "dark" : "light" },
        colors: cfg.colors || [primary, "#15803d"],
        series: cfg.series,
        xaxis: { categories: cfg.categories },
        dataLabels: { enabled: false },
        plotOptions: { bar: { borderRadius: 4, columnWidth: "55%" } },
        grid: { borderColor: dark ? "#333" : "#eee" },
        legend: { position: "top" },
      }).render();
    });
  }
  initCharts(document);
})();
