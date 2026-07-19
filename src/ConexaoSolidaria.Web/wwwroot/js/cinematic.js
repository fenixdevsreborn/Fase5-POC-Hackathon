// Landing cinematográfica "O Laço" — sequência de quadros no canvas dirigida pelo scroll.
// Módulo ES carregado sob demanda pela Landing (OnAfterRenderAsync) e destruído no Dispose.
// GSAP/ScrollTrigger/Lenis vêm de arquivos locais em lib/. Com prefers-reduced-motion o
// módulo nem inicializa (fallback CSS estático).
//
// Decisões de performance (o que fazia a cena travar/pular):
//  - ImageBitmap via createImageBitmap(): decodifica FORA da main thread. Com <img> o
//    decode acontecia no primeiro drawImage, travando o scroll.
//  - JANELA de decodificação: só a vizinhança do quadro atual vive como ImageBitmap;
//    o resto fica como blob comprimido (~24 KB). Manter os 744 bitmaps decodificados
//    custaria ~2,7 GB (1280×720×4 bytes cada) — era o que degradava o scroll assim
//    que o carregamento em background terminava.
//  - Canvas limitado à resolução nativa dos quadros: acima disso o drawImage só paga
//    upscale mais caro sem ganhar nitidez (o compositor estica o resto via CSS).
//  - Desenho num loop de rAF, só quando o índice muda — o onUpdate do ScrollTrigger
//    dispara muito mais vezes que a taxa de quadros útil.
//  - Lenis com `lerp` (resposta proporcional) em vez de `duration` (que arrastava).

// 24 fps = taxa nativa dos MP4 fonte: todo quadro único do vídeo entra na sequência.
// Acima disso o ffmpeg só duplicaria quadros — mesma imagem, mais peso.
const FPS = 24;
// Quadros por clipe (6s, 7s, 6s, 6s, 6s a 24 fps), acumulados. Numeração global 0001..0744.
const SEGMENTS = [144, 312, 456, 600, 744];
const TOTAL_FRAMES = SEGMENTS[SEGMENTS.length - 1];

let loadedLibs = null;

function loadScript(src) {
    return new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = src;
        s.async = true;
        s.onload = resolve;
        s.onerror = () => reject(new Error('script load failed: ' + src));
        document.head.appendChild(s);
    });
}

async function ensureLibs() {
    if (loadedLibs) return loadedLibs;
    if (!window.gsap) await loadScript('lib/gsap/gsap.min.js');
    if (!window.ScrollTrigger) await loadScript('lib/gsap/ScrollTrigger.min.js');
    if (!window.Lenis) await loadScript('lib/lenis/lenis.min.js');
    window.gsap.registerPlugin(window.ScrollTrigger);
    loadedLibs = { gsap: window.gsap, ScrollTrigger: window.ScrollTrigger, Lenis: window.Lenis };
    return loadedLibs;
}

// Progresso normalizado (0..1) do início e do fim de cada clipe. Os textos são
// posicionados a partir daqui, então a sincronia acompanha automaticamente qualquer
// mudança de FPS ou de duração — nada de faixas escritas à mão.
function clipBounds(i) {
    const from = i === 0 ? 0 : SEGMENTS[i - 1];
    const to = SEGMENTS[i];
    return [from / (TOTAL_FRAMES - 1), (to - 1) / (TOTAL_FRAMES - 1)];
}

// Faixa de exibição do texto dentro do clipe: entra logo após o corte e sai antes do
// próximo, para nunca haver dois textos no mesmo quadro.
function overlayRange(i) {
    const [a, b] = clipBounds(i);
    const span = b - a;
    if (i === 0) return [a, b];
    if (i === SEGMENTS.length - 1) return [a + span * 0.10, 1];
    return [a + span * 0.08, b - span * 0.06];
}

export async function init(stageId) {
    // Acessibilidade: sem movimento, sem cena — o CSS mostra o fallback estático.
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
        document.documentElement.classList.add('cine-reduced');
        return null;
    }

    const stage = document.getElementById(stageId);
    if (!stage) return null;
    const wrap = stage.parentElement;

    const { gsap, ScrollTrigger, Lenis } = await ensureLibs();
    wrap.classList.add('is-live');                       // layout cinematográfico (CSS)
    document.documentElement.classList.add('cine-page'); // header transparente sobre a cena

    const canvas = stage.querySelector('.cine-canvas');
    const ctx = canvas.getContext('2d', { alpha: false });
    const dir = Math.min(window.innerWidth, window.innerHeight * 1.78) <= 900 ? 'mobile' : 'desktop';

    // ---- Carregamento dos quadros --------------------------------------------------------
    // Duas camadas: `blobs` (todos os quadros, comprimidos) e `frames` (ImageBitmaps só na
    // janela ao redor do quadro atual). A decodificação segue fora da main thread.
    const state = { progress: 0 };
    const currentIndex = () =>
        Math.max(0, Math.min(TOTAL_FRAMES - 1, Math.round(state.progress * (TOTAL_FRAMES - 1))));

    const blobs = new Array(TOTAL_FRAMES).fill(null);
    const frames = new Array(TOTAL_FRAMES).fill(null);
    const decoding = new Set();
    // ±24 quadros = ±1 s de cena decodificada (~180 MB no pior caso, desktop).
    const WINDOW = 24;
    let disposed = false;

    async function loadFrame(i) {
        if (disposed || blobs[i]) return;
        try {
            const url = `cinematic/${dir}/frame-${String(i + 1).padStart(4, '0')}.webp`;
            const res = await fetch(url);
            if (!res.ok) return;
            const blob = await res.blob();
            if (disposed) return;
            blobs[i] = blob;
            if (Math.abs(i - currentIndex()) <= WINDOW) void decodeFrame(i);
        } catch { /* quadro faltando não trava a cena */ }
    }

    async function decodeFrame(i) {
        if (disposed || frames[i] || !blobs[i] || decoding.has(i)) return;
        decoding.add(i);
        try {
            const bmp = await createImageBitmap(blobs[i]);
            // Num scrub rápido a janela pode ter passado longe enquanto decodificava.
            if (disposed || Math.abs(i - currentIndex()) > WINDOW * 2) { bmp.close?.(); return; }
            frames[i] = bmp;
        } catch { /* decode falhou: o nearestReady cobre com o vizinho */ }
        finally { decoding.delete(i); }
    }

    // Decodifica a vizinhança do quadro atual e devolve o resto ao estado de blob.
    let lastCenter = -1;
    function syncWindow(center) {
        if (lastCenter >= 0 && Math.abs(center - lastCenter) < 4) return;
        lastCenter = center;
        for (let i = 0; i < TOTAL_FRAMES; i++) {
            if (Math.abs(i - center) <= WINDOW) void decodeFrame(i);
            else if (frames[i]) { frames[i].close?.(); frames[i] = null; }
        }
    }

    async function loadRange(from, to, chunk) {
        for (let i = from; i < to && !disposed; i += chunk) {
            const batch = [];
            for (let j = i; j < Math.min(i + chunk, to); j++) batch.push(loadFrame(j));
            await Promise.all(batch);
        }
    }

    // O primeiro clipe bloqueia a revelação da hero (blobs + janela inicial decodificada);
    // o resto entra em background.
    await loadRange(0, SEGMENTS[0], 12);
    await Promise.all(Array.from({ length: WINDOW + 1 }, (_, i) => decodeFrame(i)));
    stage.classList.add('cine-ready');
    const backgroundLoad = loadRange(SEGMENTS[0], TOTAL_FRAMES, 8);

    // ---- Canvas ------------------------------------------------------------------------
    let lastDrawn = -1;

    function resize() {
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        // Teto = largura nativa dos quadros: um backing store maior só encarece cada
        // drawImage (telas 2K/4K chegavam a 4× mais pixels que a fonte tem).
        const maxW = dir === 'desktop' ? 1280 : 640;
        const fit = Math.min(1, maxW / Math.max(1, canvas.clientWidth * dpr));
        canvas.width = Math.round(canvas.clientWidth * dpr * fit);
        canvas.height = Math.round(canvas.clientHeight * dpr * fit);
        lastDrawn = -1;               // força repintura no novo tamanho
        drawIndex(currentIndex());
    }

    function nearestReady(i) {
        if (frames[i]) return i;
        for (let d = 1; d < TOTAL_FRAMES; d++) {
            if (i - d >= 0 && frames[i - d]) return i - d;
            if (i + d < TOTAL_FRAMES && frames[i + d]) return i + d;
        }
        return -1;
    }

    function drawIndex(target) {
        const idx = nearestReady(target);
        if (idx < 0 || idx === lastDrawn) return;
        const bmp = frames[idx];
        const cw = canvas.width, ch = canvas.height;
        if (!cw || !ch) return;
        const scale = Math.max(cw / bmp.width, ch / bmp.height);   // cover
        const w = bmp.width * scale, h = bmp.height * scale;
        ctx.drawImage(bmp, (cw - w) / 2, (ch - h) / 2, w, h);
        lastDrawn = idx;
    }

    window.addEventListener('resize', resize);
    resize();

    // ---- ScrollTrigger com Lenis e Inércia Suave (scrub: 0.8) -----------------------
    let lenis = null;
    if (Lenis) {
        try {
            lenis = new Lenis({
                lerp: 0.08,
                smoothWheel: true,
                syncTouch: true
            });
            lenis.on('scroll', ScrollTrigger.update);
            function lenisRaf(time) {
                if (!disposed && lenis) {
                    lenis.raf(time);
                    requestAnimationFrame(lenisRaf);
                }
            }
            requestAnimationFrame(lenisRaf);
        } catch { /* fallback para scroll nativo suave */ }
    }

    window.addEventListener('scroll', () => ScrollTrigger.update(), { passive: true });

    // O stage é preso por CSS `position: sticky`. O scrub suaviza transições entre quadros.
    const pinTrigger = ScrollTrigger.create({
        trigger: wrap,
        start: 'top top',
        end: 'bottom bottom',
        scrub: 0.8,
        onUpdate: self => {
            state.progress = self.progress;
            document.documentElement.classList.toggle('cine-over-stage', self.progress < 0.97);
        },
    });

    let rafId = 0;
    function tick() {
        if (disposed) return;
        const idx = currentIndex();
        syncWindow(idx);
        drawIndex(idx);
        updateLayers(state.progress);
        rafId = requestAnimationFrame(tick);
    }
    rafId = requestAnimationFrame(tick);

    // ---- Camadas: logo e textos --------------------------------------------------------
    const logoScreen = stage.querySelector('.cine-logo--screen');
    const logoFinal = stage.querySelector('.cine-logo--final');
    const finalBlock = stage.querySelector('.cine-final');
    const overlays = [1, 2, 3, 4, 5].map(n => stage.querySelector('.cine-o' + n));
    const ranges = overlays.map((_, i) => overlayRange(i));

    // A logo some na primeira metade do clipe 1 e volta no fecho do clipe 5.
    const [c1a, c1b] = clipBounds(0);
    const [c5a, c5b] = clipBounds(SEGMENTS.length - 1);
    const LOGO_DISSOLVE = [c1a + (c1b - c1a) * 0.06, c1a + (c1b - c1a) * 0.55];
    const LOGO_REFORM = [c5a + (c5b - c5a) * 0.45, c5a + (c5b - c5a) * 0.92];

    function band(p, [a, b]) {
        if (p <= a) return 0;
        if (p >= b) return 1;
        return (p - a) / (b - a);
    }

    // Progresso linear simples (sem curva que começa lenta e acelera)
    const linear = t => Math.max(0, Math.min(1, t));

    let lastLayers = -1;
    function updateLayers(p) {
        if (Math.abs(p - lastLayers) < 0.0003) return;
        lastLayers = p;

        if (logoScreen) {
            const d = linear(band(p, LOGO_DISSOLVE));
            logoScreen.style.opacity = String(1 - d);
            logoScreen.style.transform = `translate(-50%,-50%) scale(${1 + d * 0.4})`;
        }
        if (logoFinal) {
            const r = linear(band(p, LOGO_REFORM));
            logoFinal.style.opacity = String(r);
            logoFinal.style.transform = `translate(-50%,0) scale(${0.7 + r * 0.3})`;
            finalBlock?.classList.toggle('is-live', r > 0.98);
        }

        overlays.forEach((el, i) => {
            if (!el) return;
            const [a, b] = ranges[i];
            const span = Math.max(b - a, 0.001);
            const fade = Math.min(span * 0.3, 0.07);
            const inP = i === 0 ? 1 : linear(band(p, [a, a + fade]));
            const outP = i === overlays.length - 1 ? 0 : linear(band(p, [b - fade, b]));
            const vis = inP * (1 - outP);
            el.style.opacity = String(vis);
            el.style.setProperty('--cine-shift', `${(1 - inP) * 18 - outP * 14}px`);
            el.style.pointerEvents = vis > 0.4 ? 'auto' : 'none';
        });
    }

    updateLayers(0);
    ScrollTrigger.refresh();

    // Âncoras (/#como-funciona etc.): reposiciona na tela
    function scrollToHash() {
        if (!location.hash) return;
        let target = null;
        try { target = document.querySelector(location.hash); } catch { return; }
        if (!target || !wrap.contains(target)) return;
        const y = Math.max(0, target.getBoundingClientRect().top + window.scrollY - 90);
        window.scrollTo({ top: y, behavior: 'instant' });
        ScrollTrigger.update();
    }

    scrollToHash();
    window.addEventListener('hashchange', scrollToHash);

    // ---- API para o Blazor -------------------------------------------------------------
    return {
        destroy() {
            disposed = true;
            try { lenis?.destroy(); } catch {}
            cancelAnimationFrame(rafId);
            window.removeEventListener('resize', resize);
            window.removeEventListener('hashchange', scrollToHash);
            pinTrigger.kill();
            ScrollTrigger.getAll().forEach(t => t.kill());
            frames.forEach(b => b?.close?.());   // libera a memória dos bitmaps
            frames.fill(null);
            blobs.fill(null);
            decoding.clear();
            wrap.classList.remove('is-live');
            stage.classList.remove('cine-ready');
            document.documentElement.classList.remove('cine-page', 'cine-over-stage');
            void backgroundLoad;
        },
    };
}
