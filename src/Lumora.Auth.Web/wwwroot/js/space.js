// Pixel-art space background (same generator as the launcher's SpaceBackdrop):
// dithered sky, nebula, stars, a cube-shaped Earth with an atmosphere, a cube moon and asteroids.
// Usage: <canvas class="lm-space" data-space data-seed="7" data-planet="0.8,0.8" data-planet-size="0.4" data-moon="0.5,0.3"></canvas>
(() => {
    const SKY = [0x05060D, 0x070914, 0x090C1B, 0x0C0F22, 0x100F28, 0x14102D, 0x181131, 0x1C1234];
    const BAYER = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];
    const reduceMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;

    const bayer = (x, y) => (BAYER[(y & 3) * 4 + (x & 3)] + 0.5) / 16;

    function hash01(seed, x, y) {
        let h = Math.imul(seed >>> 0, 0x9E3779B1) >>> 0;
        h = (h ^ Math.imul(x >>> 0, 0x85EBCA77)) >>> 0;
        h = ((h << 13) | (h >>> 19)) >>> 0;
        h = (h ^ Math.imul(y >>> 0, 0xC2B2AE3D)) >>> 0;
        h = Math.imul(h, 0x27D4EB2F) >>> 0;
        h = (h ^ (h >>> 15)) >>> 0;
        h = Math.imul(h, 0x165667B1) >>> 0;
        h = (h ^ (h >>> 13)) >>> 0;
        return (h & 0xFFFFFF) / 0x1000000;
    }

    function valueNoise(x, y, seed) {
        const xi = Math.floor(x), yi = Math.floor(y);
        const xf = x - xi, yf = y - yi;
        const u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
        const a = hash01(seed, xi, yi), b = hash01(seed, xi + 1, yi);
        const c = hash01(seed, xi, yi + 1), d = hash01(seed, xi + 1, yi + 1);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    function fbm(x, y, seed) {
        let sum = 0, amp = 0.5, total = 0;
        for (let o = 0; o < 4; o++) {
            sum += valueNoise(x, y, seed + o * 101) * amp;
            total += amp;
            x *= 2.03; y *= 2.03; amp *= 0.5;
        }
        return sum / total;
    }

    const shade = (c, k) => {
        const r = Math.max(0, Math.min(255, ((c >> 16) & 255) * k));
        const g = Math.max(0, Math.min(255, ((c >> 8) & 255) * k));
        const b = Math.max(0, Math.min(255, (c & 255) * k));
        return (r << 16) | (g << 8) | b;
    };
    const blend = (d, s, a) =>
        ((((d >> 16) & 255) * (1 - a) + ((s >> 16) & 255) * a) << 16) |
        ((((d >> 8) & 255) * (1 - a) + ((s >> 8) & 255) * a) << 8) |
        ((d & 255) * (1 - a) + (s & 255) * a);

    // Isometric cube hit test: face 0 = top, 1 = left (lit), 2 = right (night); a, b in 0..1.
    function cubeHit(fx, fy, s) {
        let a = (fx / s + 2 * (fy + s) / s) / 2;
        let b = (2 * (fy + s) / s - fx / s) / 2;
        if (a >= 0 && a <= 1 && b >= 0 && b <= 1) return { face: 0, a, b };
        let face;
        if (fx <= 0) { face = 1; a = (fx + s) / s; b = (fy + s / 2 - a * s / 2) / s; }
        else { face = 2; a = fx / s; b = (fy + a * s / 2) / s; }
        return a >= 0 && a <= 1 && b >= 0 && b <= 1 ? { face, a, b } : null;
    }

    function generate(w, h, opt) {
        const px = new Uint32Array(w * h);
        const seed = opt.seed;
        const twinkles = [];

        for (let y = 0; y < h; y++) {
            const v = y / (h - 1) * (SKY.length - 1), i = Math.floor(v), f = v - i;
            for (let x = 0; x < w; x++) px[y * w + x] = SKY[Math.min(SKY.length - 1, f > bayer(x, y) ? i + 1 : i)];
        }

        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
            const d = x / w * 0.8 + y / h - 1.05;
            const band = Math.exp(-d * d / 0.09);
            const intensity = Math.max(0, Math.min(1, (fbm(x / 34, y / 34, seed) - 0.42) * 2.4)) * band;
            const level = Math.floor(intensity * 4 + bayer(x, y) - 0.5) / 4;
            if (level <= 0) continue;
            const teal = fbm(x / 22 + 40, y / 22 + 17, seed + 3);
            const color = teal > 0.62 ? 0x2A8C8C : level > 0.5 ? 0x6A3FB8 : 0x3E2878;
            px[y * w + x] = blend(px[y * w + x], color, Math.min(0.7, level * 0.75));
        }

        const tints = [0xFFFFFF, 0xCFE3FF, 0xFFF1C9, 0xBFF5DF];
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
            const r = hash01(seed, x, y);
            if (r > 0.012) continue;
            const tint = tints[Math.floor(hash01(seed + 1, x, y) * tints.length) % tints.length];
            const i = y * w + x;
            px[i] = blend(px[i], tint, 0.3 + hash01(seed + 2, x, y) * 0.7);
            if (r < 0.0014 && x > 1 && y > 1 && x < w - 2 && y < h - 2) {
                if (twinkles.length < 28) twinkles.push({ x, y, tint, speed: 0.8 + hash01(seed + 4, x, y) * 2.2, phase: hash01(seed + 5, x, y) * 6.28 });
                else {
                    px[i] = tint;
                    for (const j of [i - 1, i + 1, i - w, i + w]) px[j] = blend(px[j], tint, 0.35);
                }
            }
        }

        const min = Math.min(w, h);
        for (let k = 0; k < 3; k++) {
            drawStoneCube(px, w, h, Math.floor(w * (0.08 + hash01(seed, k, 91) * 0.5)), Math.floor(h * (0.15 + hash01(seed, k, 92) * 0.7)),
                2 + Math.floor(hash01(seed, k, 93) * 2), 0x4A4A58, seed + k, 0.55);
        }
        const before = px.slice();
        paintEarth(px, w, h, opt.planet[0] * w, opt.planet[1] * h, opt.planetSize * min, seed);
        drawStoneCube(px, w, h, Math.floor(opt.moon[0] * w), Math.floor(opt.moon[1] * h), Math.max(5, Math.floor(min * 0.07)), 0x9A9AA6, seed, 1);
        return { px, twinkles: twinkles.filter(t => px[t.y * w + t.x] === before[t.y * w + t.x]) };
    }

    function paintEarth(px, w, h, cx, cy, radius, seed) {
        const s = radius * 0.86;
        const texels = Math.max(8, Math.min(28, Math.floor(s / 2.4)));
        const atmosphere = Math.max(3, Math.floor(s * 0.06));
        const x0 = Math.max(0, Math.floor(cx - s - atmosphere - 1)), x1 = Math.min(w - 1, Math.floor(cx + s + atmosphere + 1));
        const y0 = Math.max(0, Math.floor(cy - s - atmosphere - 1)), y1 = Math.min(h - 1, Math.floor(cy + s + atmosphere + 1));
        for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
            const fx = x + 0.5 - cx, fy = y + 0.5 - cy, i = y * w + x;
            const hit = cubeHit(fx, fy, s);
            if (!hit) {
                for (let k = 1; k <= atmosphere; k++) {
                    const g = cubeHit(fx, fy, s + k);
                    if (!g) continue;
                    const lit = g.face === 2 ? 0.25 : (fy < -s * 0.5 || fx < 0) ? 1 : 0.6;
                    let alpha = (1 - (k - 1) / atmosphere) * (0.22 + 0.5 * lit);
                    alpha = Math.floor(alpha * 3 + bayer(x, y) - 0.3) / 3;
                    if (alpha > 0) px[i] = blend(px[i], 0x5CE3C8, Math.min(0.7, alpha));
                    break;
                }
                continue;
            }
            const { face, a, b } = hit;
            const u = Math.min(texels - 1, Math.floor(a * texels)), v = Math.min(texels - 1, Math.floor(b * texels));
            const fseed = seed + 101 * (face + 1);
            const e = fbm(u / 4.5, v / 4.5, fseed);
            const jitter = (hash01(fseed + 1, u, v) - 0.5) * 0.08;
            let cold = face === 0 ? 1 - Math.hypot(a - 0.5, b - 0.5) * 3.2 : 0.45 - b * 1.4;
            cold += (hash01(fseed + 2, u, v) - 0.5) * 0.1;
            let color, land = false;
            if (cold > 0.55) color = 0xE6EEF4;
            else if (e < 0.40) color = 0x24438F;
            else if (e < 0.48) color = 0x3563C2;
            else if (e < 0.515) color = cold > 0.4 ? 0xC9D6DE : 0xD9C98A;
            else if (e < 0.64) { color = cold > 0.4 ? 0xDDE6EA : hash01(fseed + 3, u, v) > 0.5 ? 0x5FAA3B : 0x55A035; land = true; }
            else if (e < 0.72) { color = cold > 0.45 ? 0x2F5E3B : 0x3C7A2A; land = true; }
            else color = e > 0.8 ? 0xEDEDED : 0x7E7E86;
            color = shade(color, 1 + jitter);
            const cloud = fbm(u / 3.6 + 40, v / 3.6, fseed + 7) > 0.66;
            if (cloud) color = blend(color, 0xF4F7FA, 0.82);
            let c = shade(color, face === 0 ? 1 : face === 1 ? 0.8 - b * 0.12 : 0.36 - b * 0.08);
            if (face === 2) {
                c = blend(c, 0x0B1024, 0.35);
                if (land && !cloud && hash01(fseed + 9, u, v) < 0.14) c = hash01(seed + 32, x, y) > 0.4 ? 0xFFC45C : 0xE89A3A;
            }
            const t = 1 / texels;
            if ((face === 0 && (a < t * 0.6 || b < t * 0.6)) || (face === 1 && a < t * 0.6)) c = blend(c, 0xBDFCEB, 0.35);
            else if ((face === 1 && a > 1 - t * 0.5) || (face === 2 && a < t * 0.5)) c = shade(c, 0.7);
            px[i] = c;
        }
    }

    function drawStoneCube(px, w, h, cx, cy, s, base, seed, dim) {
        const texels = 6;
        for (let y = cy - s - 1; y <= cy + s + 1; y++) for (let x = cx - s - 1; x <= cx + s + 1; x++) {
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            const hit = cubeHit(x + 0.5 - cx, y + 0.5 - cy, s);
            if (!hit) continue;
            const { face, a, b } = hit;
            const k = face === 0 ? 1 : face === 1 ? 0.74 : 0.52;
            const u = Math.min(texels - 1, Math.floor(a * texels)), v = Math.min(texels - 1, Math.floor(b * texels));
            let c = shade(base, (0.88 + hash01(seed + face * 7, u, v) * 0.22) * k * dim);
            if (hash01(seed + 40 + face, u, v) < 0.14) c = shade(c, 0.72);
            if (a < 0.06 || a > 0.94 || b > 0.94 || (face === 0 && b < 0.06)) c = shade(c, 0.55);
            px[y * w + x] = c;
        }
    }

    const instances = new Set();

    function setup(canvas) {
        if (canvas.dataset.ready) return;
        canvas.dataset.ready = "1";
        const pair = (s, d) => (s || d).split(",").map(Number);
        const opt = {
            seed: Number(canvas.dataset.seed || 7),
            planet: pair(canvas.dataset.planet, "0.82,0.85"),
            planetSize: Number(canvas.dataset.planetSize || 0.42),
            moon: pair(canvas.dataset.moon, "0.6,0.3"),
            scale: Number(canvas.dataset.scale || 4),
        };
        const inst = { canvas, opt, scene: null, low: null, w: 0, h: 0 };
        instances.add(inst);
        const render = () => {
            const rect = canvas.getBoundingClientRect();
            const w = Math.max(2, Math.ceil(rect.width / opt.scale)), h = Math.max(2, Math.ceil(rect.height / opt.scale));
            if (w === inst.w && h === inst.h && inst.scene) return;
            inst.w = w; inst.h = h;
            inst.scene = generate(w, h, opt);
            const img = new ImageData(w, h);
            const d = img.data;
            inst.scene.px.forEach((c, i) => { d[i * 4] = (c >> 16) & 255; d[i * 4 + 1] = (c >> 8) & 255; d[i * 4 + 2] = c & 255; d[i * 4 + 3] = 255; });
            inst.low = document.createElement("canvas");
            inst.low.width = w; inst.low.height = h;
            inst.low.getContext("2d").putImageData(img, 0, 0);
            draw(inst, performance.now() / 1000);
        };
        inst.render = render;
        new ResizeObserver(() => { clearTimeout(inst.t); inst.t = setTimeout(render, 120); }).observe(canvas);
        render();
    }

    function draw(inst, time) {
        const { canvas, opt, low, scene } = inst;
        if (!low) return;
        const dpr = window.devicePixelRatio || 1;
        const cw = Math.round(inst.w * opt.scale * dpr), ch = Math.round(inst.h * opt.scale * dpr);
        if (canvas.width !== cw || canvas.height !== ch) { canvas.width = cw; canvas.height = ch; }
        const ctx = canvas.getContext("2d");
        ctx.imageSmoothingEnabled = false;
        ctx.drawImage(low, 0, 0, cw, ch);
        if (reduceMotion) return;
        const px = opt.scale * dpr;
        for (const t of scene.twinkles) {
            let a = 0.5 + 0.5 * Math.sin(time * t.speed + t.phase);
            a *= a;
            const rgb = `${(t.tint >> 16) & 255},${(t.tint >> 8) & 255},${t.tint & 255}`;
            ctx.fillStyle = `rgba(${rgb},${a})`;
            ctx.fillRect(t.x * px, t.y * px, px, px);
            ctx.fillStyle = `rgba(${rgb},${a * 0.45})`;
            ctx.fillRect((t.x - 1) * px, t.y * px, px, px);
            ctx.fillRect((t.x + 1) * px, t.y * px, px, px);
            ctx.fillRect(t.x * px, (t.y - 1) * px, px, px);
            ctx.fillRect(t.x * px, (t.y + 1) * px, px, px);
        }
    }

    let last = 0;
    function loop(now) {
        if (now - last > 125) {
            last = now;
            for (const inst of instances) {
                if (!inst.canvas.isConnected) { instances.delete(inst); continue; }
                draw(inst, now / 1000);
            }
        }
        requestAnimationFrame(loop);
    }

    function init() {
        document.querySelectorAll("canvas[data-space]").forEach(setup);
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
    else init();
    window.Blazor?.addEventListener?.("enhancedload", init);
    if (!reduceMotion) requestAnimationFrame(loop);
})();
