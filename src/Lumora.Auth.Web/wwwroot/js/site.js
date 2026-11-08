// Small site behaviours without inline handlers (the Content-Security-Policy forbids them).
(() => {
    document.addEventListener("click", (e) => {
        const target = e.target;
        if (!(target instanceof Element)) return;

        // Mobile menu: the burger opens it, picking an item closes it.
        if (target.closest("[data-nav-toggle]")) {
            document.getElementById("lm-topnav")?.classList.toggle("open");
            return;
        }
        if (target.closest("[data-nav-close] a")) {
            document.getElementById("lm-topnav")?.classList.remove("open");
        }

        // Copy buttons: <button data-copy="text">.
        const copy = target.closest("[data-copy]");
        if (copy) {
            navigator.clipboard?.writeText(copy.getAttribute("data-copy") || "").then(() => {
                copy.classList.add("copied");
                setTimeout(() => copy.classList.remove("copied"), 1400);
            });
        }
    });

    // "Download" button: point it at the build for the visitor's system.
    function detectOs() {
        const p = (navigator.userAgentData?.platform || navigator.platform || navigator.userAgent || "").toLowerCase();
        if (p.includes("win")) return "windows";
        if (p.includes("mac")) return "macos";
        if (p.includes("linux") && !/android/i.test(navigator.userAgent)) return "linux";
        return null;
    }

    function pickDownload() {
        const os = detectOs();
        const button = document.querySelector("[data-os-download]");
        if (!os) return;
        const links = [...document.querySelectorAll(`[data-os="${os}"]`)];
        if (links.length === 0) return;
        // Apple Silicon is the common Mac today; Intel stays one click away.
        const best = links.find(a => /silicon/i.test(a.dataset.osName || "")) || links[0];
        best.classList.replace("btn-outline-secondary", "btn-primary");
        if (button) {
            button.setAttribute("href", best.getAttribute("href"));
            const label = button.querySelector("[data-os-label]");
            if (label) label.textContent = `Скачать для ${best.dataset.osName}`;
        }
    }

    // The download links arrive with streaming rendering, so wait for them.
    function init() {
        pickDownload();
        const section = document.getElementById("download");
        if (section && !section.dataset.watched) {
            section.dataset.watched = "1";
            new MutationObserver(pickDownload).observe(section, { childList: true, subtree: true });
        }
    }

    // Live consoles: keep following new lines unless the admin scrolled up to read.
    const following = new WeakMap();
    new MutationObserver(() => {
        document.querySelectorAll("[data-autoscroll]").forEach((el) => {
            if (!following.has(el)) {
                following.set(el, true);
                el.addEventListener("scroll", () => following.set(el, el.scrollHeight - el.scrollTop - el.clientHeight < 40));
                el.scrollTop = el.scrollHeight;
            } else if (following.get(el)) {
                el.scrollTop = el.scrollHeight;
            }
        });
    }).observe(document.body, { childList: true, subtree: true });

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
    else init();
    window.Blazor?.addEventListener?.("enhancedload", init);
})();
