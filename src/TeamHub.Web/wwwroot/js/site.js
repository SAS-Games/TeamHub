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

        const desiredLeft = focus.offsetLeft - ((track.clientWidth - focus.offsetWidth) / 2);
        const maximumLeft = track.scrollWidth - track.clientWidth;
        track.scrollLeft = Math.max(0, Math.min(desiredLeft, maximumLeft));
    };

    const positionRoadmaps = () => {
        window.requestAnimationFrame(() => {
            document.querySelectorAll('.roadmap-track').forEach(centerRoadmapFocus);
        });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', positionRoadmaps, { once: true });
    } else {
        positionRoadmaps();
    }
})();
