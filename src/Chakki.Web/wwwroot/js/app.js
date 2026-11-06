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
  const rs = (v) => (v < 0 ? "−₹" : "₹") + Math.abs(v).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const round2 = (v) => Math.round((v + Number.EPSILON) * 100) / 100;
  const recalc = (form) => {
    let total = 0, taxTotal = 0;
    const review = [];
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
      // Same arithmetic as the server (Taxes.Split): exclusive tax on top, inclusive tax carved out.
      const priced = round2(kg * rate);
      const pct = parseFloat(opt?.dataset.tax || "0");
      const incl = opt?.dataset.taxIncl === "1";
      const tax = pct > 0 ? (incl ? round2(priced * pct / (100 + pct)) : round2(priced * pct / 100)) : 0;
      const lineTotal = incl ? priced : priced + tax;
      total += lineTotal; taxTotal += tax;
      const amountEl = line.querySelector("[data-ca-line-amount]");
      if (amountEl) amountEl.textContent = rs(lineTotal);
      const taxEl = line.querySelector("[data-ca-line-tax]");
      if (taxEl) taxEl.textContent = tax > 0 ? `${kg || 0} kg × ${rs(rate)} · ${opt.dataset.taxName} ${pct}% ${rs(tax)}${incl ? " incl." : ""}` : (kg && rate ? `${kg} kg × ${rs(rate)}` : "");
      if (kg > 0) review.push([opt?.textContent.split(" — ")[0] || "", kg, rate, lineTotal]);
    });
    const out = form.querySelector("[data-ca-total]");
    if (out) out.textContent = rs(total);
    const taxOut = form.querySelector("[data-ca-total-tax]");
    if (taxOut) taxOut.textContent = taxTotal > 0 ? `${document.documentElement.lang.startsWith("hi") ? "कर सहित" : "incl. tax"} ${rs(taxTotal)}` : "";
    const body = form.querySelector("[data-ca-review-lines]");
    if (body) {
      body.replaceChildren(...review.map(([name, kg, rate, amt]) => {
        const tr = document.createElement("tr");
        [name, `${kg} kg`, rs(rate), rs(amt)].forEach((v, i) => { const td = document.createElement("td"); td.textContent = v; if (i) td.className = "num"; tr.appendChild(td); });
        return tr;
      }));
    }
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

// ==================================================================== Global search, searchable dropdowns, searchable tables
(() => {
  "use strict";
  const hi = (document.documentElement.lang || "").startsWith("hi");
  const t = (en, hin) => (hi ? hin : en);

  // ---------------------------------------------------------------- Global search (Ctrl+K or "/")
  const modalEl = document.getElementById("ca-search");
  const openSearch = () => {
    if (!modalEl || !window.bootstrap) return false;
    window.bootstrap.Modal.getOrCreateInstance(modalEl).show();
    return true;
  };
  if (modalEl) {
    modalEl.addEventListener("shown.bs.modal", () => { const q = document.getElementById("ca-search-q"); q?.focus(); q?.select(); });
    modalEl.addEventListener("keydown", (e) => {
      const hits = [...modalEl.querySelectorAll("[data-ca-search-hit]")];
      if (!hits.length) return;
      const i = hits.indexOf(document.activeElement);
      if (e.key === "ArrowDown") { e.preventDefault(); hits[Math.min(i + 1, hits.length - 1)].focus(); }
      else if (e.key === "ArrowUp") { e.preventDefault(); if (i <= 0) document.getElementById("ca-search-q")?.focus(); else hits[i - 1].focus(); }
      else if (e.key === "Enter" && e.target.id === "ca-search-q") { e.preventDefault(); hits[0].click(); }
    });
  }
  document.addEventListener("click", (e) => {
    const trigger = e.target.closest("[data-ca-search-open]");
    if (trigger && openSearch()) e.preventDefault();
  });
  document.addEventListener("keydown", (e) => {
    const typing = e.target.closest("input, textarea, select, [contenteditable=true], .ts-control");
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { if (openSearch()) e.preventDefault(); }
    else if (e.key === "/" && !typing) { if (openSearch()) e.preventDefault(); }
  });

  // ---------------------------------------------------------------- Searchable dropdowns (Tom Select) on every <select>
  const enhanceSelect = (sel) => {
    if (!window.TomSelect || sel.tomselect || sel.matches("[data-ca-plain], [multiple]") || sel.closest(".ts-wrapper")) return;
    const ts = new window.TomSelect(sel, {
      create: false,
      allowEmptyOption: true,
      maxOptions: 500,
      refreshThrottle: 0,
      searchField: ["text"],
      plugins: ["dropdown_input"],
      render: { no_results: () => `<div class="no-results">${t("No match", "कुछ नहीं मिला")}</div>` },
      onInitialize() { this.control_input?.setAttribute("placeholder", t("Type to search…", "खोजने के लिए लिखें…")); },
    });
    ts.wrapper.classList.add("ca-ts");
  };
  const enhanceSelects = (root) => root.querySelectorAll?.("select").forEach(enhanceSelect);

  // ---------------------------------------------------------------- Searchable tables: a filter box above every data table
  const norm = (s) => (s || "").toLowerCase().replace(/\s+/g, " ").trim();
  const enhanceTable = (table) => {
    if (table.dataset.caFilter || table.matches("[data-ca-no-filter]") || table.closest("[data-ca-no-filter]")) return;
    const body = table.tBodies[0];
    if (!body || body.rows.length < 3) return; // very short tables need no search box
    table.dataset.caFilter = "1";
    const box = document.createElement("div");
    box.className = "ca-table-filter";
    box.innerHTML = `<i class="bi bi-search" aria-hidden="true"></i><input class="form-control form-control-sm" type="search" placeholder="${t("Search in this table…", "इस सूची में खोजें…")}" aria-label="${t("Search in this table", "इस सूची में खोजें")}"><span class="ca-table-count" aria-live="polite"></span>`;
    const anchor = table.closest(".table-responsive, .ca-table-wrap") || table;
    anchor.before(box);
    const input = box.querySelector("input");
    const count = box.querySelector(".ca-table-count");
    const apply = () => {
      const words = norm(input.value).split(" ").filter(Boolean);
      const rows = [...table.tBodies].flatMap((b) => [...b.rows]).filter((r) => !r.matches("[data-ca-empty-row]"));
      let shown = 0;
      rows.forEach((r) => {
        const text = norm(r.textContent + " " + [...r.querySelectorAll("input[value], select")].map((x) => x.value).join(" "));
        const ok = words.every((w) => text.includes(w));
        r.hidden = !ok;
        if (ok) shown++;
      });
      count.textContent = words.length ? `${shown} / ${rows.length}` : "";
      let empty = body.querySelector("[data-ca-empty-row]");
      if (words.length && shown === 0 && rows.length) {
        if (!empty) {
          empty = body.insertRow();
          empty.setAttribute("data-ca-empty-row", "");
          const cell = empty.insertCell();
          cell.colSpan = table.rows[0]?.cells.length || 1;
          cell.className = "text-center text-body-secondary py-3";
          cell.textContent = t("No matching rows", "कोई मिलती पंक्ति नहीं");
        }
      } else empty?.remove();
    };
    input.addEventListener("input", apply);
    table.caApplyFilter = apply;
  };
  const enhanceTables = (root) => root.querySelectorAll?.("table.table").forEach(enhanceTable);

  const enhance = (root) => { enhanceSelects(root); enhanceTables(root); };
  enhance(document);
  if (window.htmx) {
    window.htmx.onLoad((el) => {
      enhance(el);
      // A row swapped in by HTMX (e.g. attendance) keeps the current table filter.
      el.closest?.("table")?.caApplyFilter?.();
    });
  }

  // Grinding order form clones its last line: give the clone a fresh dropdown.
  document.addEventListener("click", (e) => {
    if (!e.target.closest("[data-ca-add-line]")) return;
    setTimeout(() => {
      document.querySelectorAll("[data-ca-line]").forEach((line) => {
        const wrappers = line.querySelectorAll(".ts-wrapper");
        const sel = line.querySelector("select");
        if (sel && !sel.tomselect && wrappers.length) {
          wrappers.forEach((w) => w.remove());
          sel.classList.remove("tomselected", "ts-hidden-accessible");
          sel.removeAttribute("tabindex"); sel.removeAttribute("id");
          sel.style.display = "";
          sel.selectedIndex = 0;
          enhanceSelect(sel);
          sel.dispatchEvent(new Event("change", { bubbles: true }));
        }
      });
    }, 0);
  }, true);
})();

// ==================================================================== New order wizard, customer fields, buyback preview
(() => {
  "use strict";
  const rs = (v) => (v < 0 ? "−₹" : "₹") + Math.abs(v).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  // ---------------------------------------------------------------- Existing customer chosen: hide the new-customer fields
  const syncCustomer = (form) => {
    const sel = form.querySelector("[data-ca-customer]");
    if (!sel) return;
    const existing = sel.value && sel.value !== "0";
    form.querySelectorAll("[data-ca-new-customer]").forEach((el) => {
      el.classList.toggle("d-none", existing);
      if (existing) el.querySelectorAll("input").forEach((i) => (i.value = ""));
    });
  };
  document.addEventListener("change", (e) => {
    if (e.target.matches("[data-ca-customer]")) syncCustomer(e.target.form);
  });
  document.querySelectorAll("[data-ca-customer]").forEach((s) => syncCustomer(s.form));

  // ---------------------------------------------------------------- Step-by-step wizard
  document.querySelectorAll("[data-ca-wizard]").forEach((form) => {
    const steps = [...form.querySelectorAll("[data-ca-step]")];
    const tabs = [...form.querySelectorAll("[data-ca-step-tab]")];
    const back = form.querySelector("[data-ca-wizard-back]");
    const next = form.querySelector("[data-ca-wizard-next]");
    const submit = form.querySelector("[data-ca-wizard-submit]");
    const cancel = form.querySelector("[data-ca-wizard-cancel]");
    let current = 0;

    const valid = (i) => {
      const step = steps[i];
      if (i === 0) {
        const sel = form.querySelector("[data-ca-customer]");
        const name = form.querySelector("#NewCustomerName");
        const ok = (sel && sel.value !== "0") || (name && name.value.trim().length > 0);
        form.querySelector("[data-ca-customer-error]")?.classList.toggle("d-none", ok);
        if (!ok) name?.focus();
        return ok;
      }
      for (const el of step.querySelectorAll("input, select, textarea")) {
        if (el.closest(".d-none") || el.closest(".ts-wrapper")) continue;
        if (!el.checkValidity()) { el.reportValidity(); return false; }
      }
      return true;
    };
    const show = (i) => {
      current = i;
      steps.forEach((s, n) => s.classList.toggle("d-none", n !== i));
      tabs.forEach((t, n) => { t.classList.toggle("current", n === i); t.classList.toggle("done", n < i); t.setAttribute("aria-current", n === i ? "step" : "false"); });
      back.classList.toggle("d-none", i === 0);
      cancel.classList.toggle("d-none", i !== 0);
      next.classList.toggle("d-none", i === steps.length - 1);
      submit.classList.toggle("d-none", i !== steps.length - 1);
      if (i === steps.length - 1) {
        const sel = form.querySelector("[data-ca-customer]");
        const name = form.querySelector("#NewCustomerName")?.value.trim();
        const out = form.querySelector("[data-ca-review-customer]");
        if (out) out.textContent = sel && sel.value !== "0" ? sel.selectedOptions[0].textContent : `${name} (${document.documentElement.lang.startsWith("hi") ? "नया" : "new"})`;
      }
      form.scrollIntoView({ block: "start", behavior: "smooth" });
      const first = steps[i].querySelector(".ts-control, input:not([type=hidden]), select");
      if (first && i > 0) setTimeout(() => first.focus?.(), 50);
    };
    next.addEventListener("click", () => { if (valid(current)) show(current + 1); });
    back.addEventListener("click", () => show(current - 1));
    tabs.forEach((t, n) => t.addEventListener("click", () => { if (n < current || (n === current + 1 && valid(current))) show(n); }));
    form.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && current < steps.length - 1 && e.target.matches("input")) { e.preventDefault(); next.click(); }
    });
    form.addEventListener("submit", (e) => {
      for (let i = 0; i < steps.length; i++) if (!valid(i)) { e.preventDefault(); show(i); valid(i); return; }
    });
  });

  // ---------------------------------------------------------------- Buyback against a bill: live split
  const bbCalc = (form) => {
    const outstanding = parseFloat(form.dataset.outstanding || "0");
    const sel = form.querySelector("select[name=itemId]");
    const sys = parseFloat(sel?.selectedOptions[0]?.dataset.rate || "0");
    const rateEl = form.querySelector("[data-ca-bb-rate]");
    if (rateEl && !rateEl.value && document.activeElement !== rateEl) rateEl.placeholder = sys.toFixed(2);
    const rate = rateEl?.value ? parseFloat(rateEl.value) : sys;
    form.querySelector("[data-ca-bb-reason-wrap]")?.classList.toggle("d-none", !rateEl?.value || Math.abs(rate - sys) < 0.0001);
    const lineSel = form.querySelector("[data-ca-bb-line]");
    const qtyEl = form.querySelector("[data-ca-bb-qty]");
    if (lineSel && qtyEl) {
      const max = lineSel.selectedOptions[0]?.dataset.max;
      if (max) { qtyEl.max = max; const hint = form.querySelector("[data-ca-bb-max]"); if (hint) hint.textContent = (document.documentElement.lang.startsWith("hi") ? "अधिकतम " : "At most ") + max.replace(/\.00$/, "") + " kg"; }
    }
    const qty = parseFloat(qtyEl?.value || "0");
    const value = Math.round(qty * rate * 100) / 100;
    const fee = Math.min(outstanding, value);
    const set = (k, v) => { const el = form.querySelector(`[data-ca-bb-${k}]`); if (el) el.textContent = rs(v); };
    set("value", value); set("fee", fee); set("due", outstanding - fee); set("cash", value - fee);
  };
  document.addEventListener("input", (e) => { const f = e.target.closest("[data-ca-buyback]"); if (f) bbCalc(f); });
  document.addEventListener("change", (e) => { const f = e.target.closest("[data-ca-buyback]"); if (f) bbCalc(f); });
  document.querySelectorAll("[data-ca-buyback]").forEach(bbCalc);
})();

// Quick-reason chips fill a text box (e.g. "Customer left").
document.addEventListener("click", (e) => {
  const chip = e.target.closest("[data-ca-fill]");
  if (!chip) return;
  const input = document.querySelector(chip.dataset.caFill);
  if (input) { input.value = chip.dataset.value || chip.textContent.trim(); input.focus(); }
});

// Order screen: tapping an earlier step opens the "go back" box for that step.
document.addEventListener("click", (e) => {
  const step = e.target.closest("[data-ca-back-to]");
  if (!step) return;
  const modal = document.getElementById("back-modal");
  if (!modal) return;
  modal.querySelector("[data-ca-back-input]").value = step.dataset.caBackTo;
  modal.querySelector("[data-ca-back-name]").textContent = step.dataset.caBackLabel;
  modal.querySelectorAll("[data-ca-back-note]").forEach((n) => n.classList.toggle("d-none", n.dataset.caBackNote !== step.dataset.caBackTo));
});
document.addEventListener("shown.bs.modal", (e) => e.target.querySelector("input:not([type=hidden])")?.focus());

// Payment amount chips (one grain, this bill, bill + old Udhaar) fill every amount box on the card.
document.addEventListener("click", (e) => {
  const chip = e.target.closest("[data-ca-pay]");
  if (!chip) return;
  const card = chip.closest(".card");
  card.querySelectorAll("[data-ca-pay-amount]").forEach((i) => (i.value = chip.dataset.caPay));
  card.querySelectorAll("[data-ca-pay]").forEach((c) => c.classList.toggle("active", c === chip));
});

// Order form: number the grain lines 0, 1, 2… before sending so a removed line leaves no gap.
document.addEventListener("submit", (e) => {
  const form = e.target.closest?.("[data-ca-order-form]");
  if (!form || e.defaultPrevented) return;
  form.querySelectorAll("[data-ca-line]").forEach((line, i) => {
    line.querySelectorAll("[name]").forEach((el) => (el.name = el.name.replace(/Lines\[\d+\]/, `Lines[${i}]`)));
  });
});
