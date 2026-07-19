// Landing cinematográfica "O Laço" — sequência de quadros no canvas dirigida pelo scroll.
// Módulo ES carregado sob demanda pela Landing (OnAfterRenderAsync) e destruído no Dispose.
// GSAP/ScrollTrigger/Lenis são carregados dinamicamente (arquivos locais em lib/), para não
// pesar nas demais páginas. Com prefers-reduced-motion o módulo nem inicializa (fallback CSS).

const FPS = 10;
// Clipes: 6s + 7s + 6s + 6s + 6s = 31s -> 310 quadros, numerados globalmente 0001..0310.
const SEGMENTS = [60, 130, 190, 250, 310];
const TOTAL_FRAMES = SEGMENTS[SEGMENTS.length - 1];
const SCROLL_LENGTH_VH = 620; // altura total da rolagem da cena (por viewport)

// Faixas de progresso (0..1) de cada camada de texto e da logo.
const RANGES = {
    logoDissolve: [0.02, 0.12],
    overlay1: [0.00, 0.15],   // hero
    overlay2: [0.17, 0.36],   // selos + confiabilidade
    overlay3: [0.39, 0.55],   // como funciona
    overlay4: [0.58, 0.75],   // impacto + instituições
    overlay5: [0.86, 1.00],   // final: sobre + logo re-formada + campanha + CTA
    logoReform: [0.88, 0.97],
};

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

function frameUrl(dir, index) {
    return `cinematic/${dir}/frame-${String(index + 1).padStart(4, '0')}.webp`;
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
    wrap.classList.add('is-live'); // ativa o layout cinematográfico (CSS)

    const canvas = stage.querySelector('.cine-canvas');
    const ctx = canvas.getContext('2d');
    const dir = Math.min(window.innerWidth, window.innerHeight * 1.78) <= 900 ? 'mobile' : 'desktop';

    // ---- Carregamento progressivo dos quadros -------------------------------------------
    const images = new Array(TOTAL_FRAMES).fill(null);
    const ready = new Array(TOTAL_FRAMES).fill(false);
    let disposed = false;

    function loadFrame(i) {
        return new Promise(resolve => {
            if (disposed || ready[i]) return resolve();
            const img = new Image();
            img.onload = () => { images[i] = img; ready[i] = true; resolve(); };
            img.onerror = () => resolve(); // quadro faltando não trava a cena
            img.src = frameUrl(dir, i);
        });
    }

    async function loadRange(from, to, chunk) {
        for (let i = from; i < to && !disposed; i += chunk) {
            const batch = [];
            for (let j = i; j < Math.min(i + chunk, to); j++) batch.push(loadFrame(j));
            await Promise.all(batch);
        }
    }

    // Segmento 1 bloqueia a revelação da hero; o resto em background.
    await loadRange(0, SEGMENTS[0], 10);
    stage.classList.add('cine-ready');
    const backgroundLoad = loadRange(SEGMENTS[0], TOTAL_FRAMES, 6);

    // ---- Canvas -------------------------------------------------------------------------
    function resize() {
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = Math.round(canvas.clientWidth * dpr);
        canvas.height = Math.round(canvas.clientHeight * dpr);
        draw(state.frame);
    }

    function nearestReady(i) {
        if (ready[i]) return i;
        for (let d = 1; d < TOTAL_FRAMES; d++) {
            if (i - d >= 0 && ready[i - d]) return i - d;
            if (i + d < TOTAL_FRAMES && ready[i + d]) return i + d;
        }
        return -1;
    }

    function draw(frameFloat) {
        const idx = nearestReady(Math.max(0, Math.min(TOTAL_FRAMES - 1, Math.round(frameFloat))));
        if (idx < 0) return;
        const img = images[idx];
        const cw = canvas.width, ch = canvas.height;
        if (!cw || !ch) return;
        // cover fit
        const scale = Math.max(cw / img.width, ch / img.height);
        const w = img.width * scale, h = img.height * scale;
        ctx.drawImage(img, (cw - w) / 2, (ch - h) / 2, w, h);
    }

    const state = { frame: 0 };
    window.addEventListener('resize', resize);
    resize();

    // ---- Lenis + ScrollTrigger ----------------------------------------------------------
    const lenis = new Lenis({ duration: dir === 'mobile' ? 0.9 : 1.25, smoothWheel: true });
    lenis.on('scroll', ScrollTrigger.update);
    gsap.ticker.add(t => lenis.raf(t * 1000));
    gsap.ticker.lagSmoothing(0);

    // O stage fica preso por CSS `position: sticky` (imune a ancestrais com transform,
    // que quebrariam o pin/fixed do ScrollTrigger). O trigger só rastreia o progresso.
    const pinTrigger = ScrollTrigger.create({
        trigger: wrap,                         // wrapper alto (define a duração da rolagem)
        start: 'top top',
        end: 'bottom bottom',
        scrub: true,
        onUpdate: self => {
            state.frame = self.progress * (TOTAL_FRAMES - 1);
            draw(state.frame);
            updateLayers(self.progress);
        },
    });

    // ---- Camadas: logo e textos ---------------------------------------------------------
    const logoScreen = stage.querySelector('.cine-logo--screen');
    const logoFinal = stage.querySelector('.cine-logo--final');
    const overlays = [1, 2, 3, 4, 5].map(n => stage.querySelector('.cine-o' + n));

    function bandProgress(p, [a, b]) {
        if (p <= a) return 0;
        if (p >= b) return 1;
        return (p - a) / (b - a);
    }

    function updateLayers(p) {
        // Logo na tela do notebook: nítida no repouso, dissolve em luz ao rolar (reversível).
        if (logoScreen) {
            const d = bandProgress(p, RANGES.logoDissolve);
            logoScreen.style.opacity = String(1 - d);
            logoScreen.style.filter = `blur(${d * 26}px) brightness(${1 + d * 2.2})`;
            logoScreen.style.transform = `translate(-50%,-50%) scale(${1 + d * 0.55})`;
        }
        // Logo final: re-forma a partir da luz (inverso), depois o loop ambiente assume.
        if (logoFinal) {
            const r = bandProgress(p, RANGES.logoReform);
            logoFinal.style.opacity = String(r);
            logoFinal.style.filter = `blur(${(1 - r) * 24}px) brightness(${1 + (1 - r) * 2})`;
            logoFinal.style.transform = `translate(-50%,0) scale(${0.6 + r * 0.4})`;
            logoFinal.closest('.cine-final')?.classList.toggle('is-live', r > 0.98);
        }
        overlays.forEach((el, i) => {
            if (!el) return;
            const key = 'overlay' + (i + 1);
            const [a, b] = RANGES[key];
            // A hero (i=0) já nasce visível no repouso; as demais entram em fade.
            const inP = i === 0 ? 1 : bandProgress(p, [a, Math.min(a + 0.045, b)]);
            const outP = i === overlays.length - 1 ? 0 : bandProgress(p, [Math.max(b - 0.045, a), b]);
            const vis = inP * (1 - outP);
            el.style.opacity = String(vis);
            el.style.transform = `translateY(${(1 - inP) * 28 - outP * 22}px)`;
            el.style.pointerEvents = vis > 0.55 ? 'auto' : 'none';
        });
    }

    updateLayers(0);
    ScrollTrigger.refresh();

    // Âncoras (/#como-funciona etc.): o browser pula antes de o wrapper ganhar a altura
    // cinematográfica — reposiciona depois que o layout final existe.
    function scrollToHash() {
        if (!location.hash) return;
        let target = null;
        try { target = document.querySelector(location.hash); } catch { return; }
        if (!target || !wrap.contains(target)) return;
        const y = Math.max(0, target.getBoundingClientRect().top + window.scrollY - 90);
        lenis.scrollTo(y, { immediate: true });
        ScrollTrigger.update();
    }

    scrollToHash();
    window.addEventListener('hashchange', scrollToHash);

    // ---- API para o Blazor --------------------------------------------------------------
    return {
        destroy() {
            disposed = true;
            window.removeEventListener('resize', resize);
            window.removeEventListener('hashchange', scrollToHash);
            pinTrigger.kill();
            ScrollTrigger.getAll().forEach(t => t.kill());
            gsap.ticker.remove(lenis.raf);
            lenis.destroy();
            images.fill(null);
            wrap.classList.remove('is-live');
            stage.classList.remove('cine-ready');
            void backgroundLoad;
        },
    };
}
