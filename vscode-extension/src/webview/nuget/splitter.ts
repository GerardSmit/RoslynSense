/// <reference path="./state.ts" />

/**
 * The drag handle between the package list and the details pane.
 *
 * A split that cannot be moved is a guess about how wide a package id is, and it is wrong for
 * anyone whose packages are named `Microsoft.Extensions.DependencyInjection.Abstractions`. The
 * position is a percentage so it survives the panel being resized, and it is persisted with the
 * rest of the view state.
 */
namespace NG {
    const MinPercent = 20;
    const MaxPercent = 80;
    const StepPercent = 4;

    export function wireSplitter(): void {
        const splitter = el<HTMLElement>('splitter');
        const pane = el<HTMLElement>('pane-packages');

        apply(state.splitPercent);

        splitter.addEventListener('pointerdown', (event) => {
            event.preventDefault();
            // Capture keeps the drag alive over the iframe's other elements and past its edges.
            splitter.setPointerCapture(event.pointerId);
            splitter.classList.add('dragging');
            document.body.classList.add('resizing');
        });

        splitter.addEventListener('pointermove', (event) => {
            if (!splitter.hasPointerCapture(event.pointerId)) {
                return;
            }
            const bounds = pane.getBoundingClientRect();
            if (bounds.width > 0) {
                apply(((event.clientX - bounds.left) / bounds.width) * 100);
            }
        });

        const end = (event: PointerEvent) => {
            if (!splitter.hasPointerCapture(event.pointerId)) {
                return;
            }
            splitter.releasePointerCapture(event.pointerId);
            splitter.classList.remove('dragging');
            document.body.classList.remove('resizing');
            persist();
        };

        splitter.addEventListener('pointerup', end);
        splitter.addEventListener('pointercancel', end);

        // A separator that only responds to a mouse is unusable without one.
        splitter.addEventListener('keydown', (event) => {
            const step =
                event.key === 'ArrowLeft' ? -StepPercent : event.key === 'ArrowRight' ? StepPercent : 0;

            if (step === 0 && event.key !== 'Home' && event.key !== 'End') {
                return;
            }

            event.preventDefault();
            apply(
                event.key === 'Home' ? MinPercent
                : event.key === 'End' ? MaxPercent
                : state.splitPercent + step
            );
            persist();
        });

        // Double-click restores the default, the standard escape hatch from a bad drag.
        splitter.addEventListener('dblclick', () => {
            apply(42);
            persist();
        });
    }

    export function applySplit(percent: number): void {
        apply(percent);
    }

    function apply(percent: number): void {
        const clamped = Math.min(MaxPercent, Math.max(MinPercent, Math.round(percent)));
        state.splitPercent = clamped;

        el<HTMLElement>('pane-packages').style.setProperty('--list-width', `${clamped}%`);
        el<HTMLElement>('splitter').setAttribute('aria-valuenow', String(clamped));
    }
}
