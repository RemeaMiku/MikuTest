(() => {
  if (window.mikuUiLoaded) return;
  window.mikuUiLoaded = true;
  // Hybrid devices may report both mouse and touch; use the actual input mode.
  document.addEventListener(
    "pointerdown",
    (e) => document.documentElement.classList.toggle("touch-input", e.pointerType !== "mouse"),
    true,
  );
  document.addEventListener(
    "pointermove",
    (e) => {
      if (e.pointerType === "mouse") document.documentElement.classList.remove("touch-input");
    },
    { passive: true },
  );
  window.mikuToast = {
    show(message, isError = false) {
      let stack = document.getElementById("action-toasts");
      if (!stack) {
        stack = document.createElement("div");
        stack.id = "action-toasts";
        stack.className = "toast-stack";
        stack.setAttribute("data-permanent", "");
        stack.setAttribute("aria-label", "操作通知");
        document.body.appendChild(stack);
      }
      const toast = document.createElement("div");
      toast.className = `action-toast ${isError ? "toast-error" : "toast-success"}`;
      toast.setAttribute("role", isError ? "alert" : "status");
      const text = document.createElement("span");
      text.textContent = message;
      const close = document.createElement("button");
      close.type = "button";
      close.className = "toast-close";
      close.textContent = "×";
      close.setAttribute("aria-label", "关闭通知");
      let timer;
      const remove = () => {
        clearTimeout(timer);
        toast.remove();
      };
      const start = () => {
        clearTimeout(timer);
        timer = setTimeout(remove, 8000);
      };
      close.addEventListener("click", remove);
      toast.addEventListener("mouseenter", () => clearTimeout(timer));
      toast.addEventListener("mouseleave", start);
      toast.addEventListener("focusin", () => clearTimeout(timer));
      toast.addEventListener("focusout", start);
      toast.append(text, close);
      stack.appendChild(toast);
      stack.scrollTop = stack.scrollHeight;
      start();
    },
  };
  const modes = ["auto", "light", "dark"];
  const system = matchMedia("(prefers-color-scheme: dark)");
  let mode = "auto";
  try {
    const saved = localStorage.getItem("mikutest-theme");
    if (modes.includes(saved)) mode = saved;
  } catch {}
  function apply() {
    document.documentElement.dataset.themeMode = mode;
    document.documentElement.dataset.theme = mode === "auto" ? (system.matches ? "dark" : "light") : mode;
  }
  apply();
  // Blazor enhanced navigation can replace attributes on the document root.
  new MutationObserver(() => {
    const expected = mode === "auto" ? (system.matches ? "dark" : "light") : mode;
    if (
      document.documentElement.dataset.theme !== expected ||
      document.documentElement.dataset.themeMode !== mode
    )
      apply();
  }).observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["data-theme", "data-theme-mode"],
  });
  system.addEventListener("change", apply);
  window.addEventListener("storage", (e) => {
    if (e.key === "mikutest-theme") {
      mode = modes.includes(e.newValue) ? e.newValue : "auto";
      apply();
    }
  });
  document.addEventListener(
    "click",
    (e) => {
      const button = e.target.closest("[data-theme-toggle]");
      if (button) {
        mode = modes[(modes.indexOf(mode) + 1) % modes.length];
        try {
          localStorage.setItem("mikutest-theme", mode);
        } catch {}
        apply();
      }
      const link = e.target.closest("[data-question-target]");
      if (link) {
        const question = document.getElementById(link.dataset.questionTarget);
        if (!question) return;
        e.preventDefault();
        question.focus({ preventScroll: true });
        question.scrollIntoView({
          behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "instant" : "smooth",
          block: "start",
        });
      }
    },
    true,
  );
  let header;
  function growTextArea(element) {
    if (!element.matches("textarea[data-auto-grow]") || !element.getClientRects().length) return;
    element.style.height = "auto";
    const style = getComputedStyle(element);
    const border = parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    element.style.height = `${Math.min(element.scrollHeight + border, parseFloat(style.maxHeight) || 320)}px`;
  }
  document.addEventListener("input", (e) => growTextArea(e.target));
  let growFrame;
  function scheduleGrow() {
    cancelAnimationFrame(growFrame);
    growFrame = requestAnimationFrame(() =>
      document.querySelectorAll("textarea[data-auto-grow]").forEach(growTextArea),
    );
  }
  window.addEventListener("resize", scheduleGrow);
  const resize = new ResizeObserver((entries) => {
    document.documentElement.style.setProperty(
      "--header-height",
      `${entries[0].target.getBoundingClientRect().height}px`,
    );
  });
  function observeHeader() {
    const current = document.querySelector(".shell > header");
    if (header === current) return;
    resize.disconnect();
    header = current;
    if (header) resize.observe(header);
  }
  document.addEventListener("DOMContentLoaded", () => {
    observeHeader();
    scheduleGrow();
    new MutationObserver(() => {
      observeHeader();
      scheduleGrow();
    }).observe(document.body, { childList: true, subtree: true });
  });
})();
