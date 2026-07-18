// Comportamentos globais, pequenos e sem dependencias: header, progresso de leitura,
// voltar ao topo e revelacao progressiva de secoes durante o scroll.
(function () {
    var revealObserver;
    var mutationObserver;
    var ticking = false;
    var initialized = false;

    function updateScrollUi() {
        var header = document.querySelector('.cs-site-header');
        var progress = document.querySelector('.cs-scroll-progress');
        var backToTop = document.querySelector('.cs-back-to-top');
        var scrollTop = window.scrollY || document.documentElement.scrollTop || 0;
        var max = Math.max(1, document.documentElement.scrollHeight - window.innerHeight);

        if (header) {
            header.classList.toggle('cs-site-header--scrolled', scrollTop > 8);
        }
        if (progress) {
            progress.style.width = Math.min(100, (scrollTop / max) * 100) + '%';
        }
        if (backToTop) {
            backToTop.classList.toggle('is-visible', scrollTop > 520);
        }

        ticking = false;
    }

    function requestScrollUpdate() {
        if (!ticking) {
            ticking = true;
            window.requestAnimationFrame(updateScrollUi);
        }
    }

    function prepareRevealElements(root) {
        if (!revealObserver) return;

        var scope = root && root.querySelectorAll ? root : document;
        var selectors = [
            '[data-reveal]',
            '.cs-feature-card',
            '.cs-panel',
            '.cs-data-shell',
            '.cs-section',
            '.cs-impact-strip',
            '.cs-institutions',
            '.cs-about',
            '.cs-tp-stat',
            '.cs-tp-main',
            '.cs-tp-side',
            '.cs-tp-campaign',
            '.campaign-stat',
            '.cs-card',
            '.cs-footer__cta'
        ].join(',');

        scope.querySelectorAll(selectors).forEach(function (element, index) {
            if (element.dataset.revealObserved === 'true') return;
            element.dataset.revealObserved = 'true';
            element.classList.add('js-reveal-ready');
            element.style.setProperty('--reveal-delay', Math.min(index % 4, 3) * 70 + 'ms');
            revealObserver.observe(element);
        });
    }

    function setupReveal() {
        if (!('IntersectionObserver' in window)
            || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
            return;
        }

        revealObserver = new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (!entry.isIntersecting) return;
                entry.target.classList.add('is-revealed');
                revealObserver.unobserve(entry.target);
            });
        }, { rootMargin: '0px 0px -8% 0px', threshold: 0.08 });

        prepareRevealElements(document);

        mutationObserver = new MutationObserver(function (mutations) {
            mutations.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (node.nodeType === Node.ELEMENT_NODE) {
                        prepareRevealElements(node.parentElement || node);
                    }
                });
            });
            requestScrollUpdate();
        });

        mutationObserver.observe(document.body, { childList: true, subtree: true });
    }

    function setupBackToTop() {
        document.addEventListener('click', function (event) {
            var button = event.target.closest && event.target.closest('.cs-back-to-top');
            if (button) {
                window.scrollTo({ top: 0, behavior: 'smooth' });
            }
        });
    }

    window.conexaoSolidaria = window.conexaoSolidaria || {};
    window.conexaoSolidaria.checkHealth = async function () {
        try {
            var response = await fetch('/health', { cache: 'no-store', headers: { 'Accept': 'text/plain' } });
            return response.ok;
        } catch (_) {
            return false;
        }
    };

    function init() {
        if (initialized) return;
        initialized = true;
        setupBackToTop();
        setupReveal();
        updateScrollUi();
    }

    window.addEventListener('scroll', requestScrollUpdate, { passive: true });
    window.addEventListener('resize', requestScrollUpdate, { passive: true });
    window.addEventListener('load', init, { once: true });
    document.addEventListener('DOMContentLoaded', init, { once: true });
    document.addEventListener('enhancedload', function () {
        prepareRevealElements(document);
        requestScrollUpdate();
    });

    if (document.readyState !== 'loading') init();
})();
