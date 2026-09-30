// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

(() => {
    const centerRoadmapFocus = track => {
        if (track.scrollWidth <= track.clientWidth) return;

        const explicitFocus = track.querySelector('[data-roadmap-focus="true"]');
        const completedSteps = track.querySelectorAll('.roadmap-step.completed');
        const focus = explicitFocus
            ?? completedSteps[completedSteps.length - 1]
            ?? track.querySelector('.roadmap-step.planned, .roadmap-step.upcoming, .roadmap-step');
        if (!focus) return;

        const trackBounds = track.getBoundingClientRect();
        const focusBounds = focus.getBoundingClientRect();
        const focusLeft = track.scrollLeft + focusBounds.left - trackBounds.left;
        const desiredLeft = focusLeft - ((track.clientWidth - focusBounds.width) / 2);
        const maximumLeft = track.scrollWidth - track.clientWidth;
        track.scrollLeft = Math.max(0, Math.min(desiredLeft, maximumLeft));
    };

    const positionRoadmaps = () => {
        window.requestAnimationFrame(() =>
            window.requestAnimationFrame(() =>
                document.querySelectorAll('.roadmap-track').forEach(centerRoadmapFocus)));
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', positionRoadmaps, { once: true });
    } else {
        positionRoadmaps();
    }
    window.addEventListener('pageshow', positionRoadmaps);
})();

(() => {
    const loader = document.querySelector('[data-support-summary-loader]');
    if (!loader) return;

    const isSupportSummaryUrl = value => {
        const url = new URL(value, window.location.href);
        if (url.origin !== window.location.origin) return false;
        const segments = url.pathname.split('/').filter(Boolean);
        return segments.at(-1)?.toLocaleLowerCase() === 'supportsummary';
    };
    const showLoader = () => {
        loader.hidden = false;
        document.body.classList.add('teamhub-page-loading');
    };
    const hideLoader = () => {
        loader.hidden = true;
        document.body.classList.remove('teamhub-page-loading');
    };

    document.addEventListener('click', event => {
        if (event.defaultPrevented
            || event.button !== 0
            || event.metaKey
            || event.ctrlKey
            || event.shiftKey
            || event.altKey
            || !(event.target instanceof Element)) return;

        const link = event.target.closest('a[href]');
        if (!link
            || link.target === '_blank'
            || link.hasAttribute('download')
            || link.getAttribute('aria-disabled') === 'true'
            || !isSupportSummaryUrl(link.href)) return;

        const target = new URL(link.href, window.location.href);
        if (target.pathname === window.location.pathname
            && target.search === window.location.search
            && target.hash
            && target.hash !== window.location.hash) return;
        showLoader();
    });

    document.addEventListener('submit', event => {
        if (event.defaultPrevented || !(event.target instanceof HTMLFormElement)) return;
        if (isSupportSummaryUrl(event.target.action || window.location.href)) showLoader();
    });

    window.addEventListener('pageshow', hideLoader);
})();
