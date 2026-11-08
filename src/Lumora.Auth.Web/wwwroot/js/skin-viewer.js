// 3D skin preview on the "Скин" page, powered by skinview3d (MIT).
// Falls back to the flat texture image when the library cannot be loaded.
(() => {
    // Served by the site itself (MIT, from npm skinview3d@3.1.0): no third-party scripts under the CSP.
    const LIB = "/lib/skinview3d/skinview3d.bundle.js";
    let loading = null;

    function loadLibrary() {
        if (window.skinview3d) return Promise.resolve();
        loading ??= new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = LIB;
            script.onload = resolve;
            script.onerror = reject;
            document.head.appendChild(script);
        });
        return loading;
    }

    function selectedModel(canvas) {
        const checked = document.querySelector(`input[data-skin-model-for="#${canvas.id}"]:checked`);
        if (checked) return checked.value === "slim" ? "slim" : "default";
        return canvas.dataset.skinModel === "slim" ? "slim" : "default";
    }

    async function init() {
        for (const canvas of document.querySelectorAll("canvas[data-skin-viewer]:not([data-ready])")) {
            canvas.dataset.ready = "1";
            const box = canvas.closest("[data-skin-box]");
            try {
                await loadLibrary();
            } catch {
                box?.classList.add("no-3d");
                continue;
            }
            const viewer = new skinview3d.SkinViewer({
                canvas,
                width: box?.clientWidth || 300,
                height: box?.clientHeight || 400,
            });
            viewer.autoRotate = true;
            viewer.autoRotateSpeed = 0.5;
            viewer.animation = new skinview3d.WalkingAnimation();
            viewer.animation.speed = 0.55;
            viewer.zoom = 0.85;
            canvas._viewer = viewer;
            canvas._skin = canvas.dataset.skinUrl || null;
            if (canvas._skin) viewer.loadSkin(canvas._skin, { model: selectedModel(canvas) });

            if (box) new ResizeObserver(() => viewer.setSize(box.clientWidth, box.clientHeight)).observe(box);
        }
    }

    document.addEventListener("change", (e) => {
        const input = e.target;
        if (input.matches("input[data-skin-file]")) {
            const canvas = document.querySelector(input.dataset.skinFile);
            const file = input.files?.[0];
            if (!canvas?._viewer || !file) return;
            canvas._skin = URL.createObjectURL(file);
            canvas.closest("[data-skin-box]")?.classList.add("has-preview");
            canvas._viewer.loadSkin(canvas._skin, { model: selectedModel(canvas) });
        } else if (input.matches("input[data-skin-model-for]")) {
            const canvas = document.querySelector(input.dataset.skinModelFor);
            if (canvas?._viewer && canvas._skin) canvas._viewer.loadSkin(canvas._skin, { model: selectedModel(canvas) });
        }
    });

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
    else init();
    // Blazor enhanced navigation swaps the page without a reload.
    window.Blazor?.addEventListener?.("enhancedload", init);
})();
