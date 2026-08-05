/// <reference path="./state.ts" />

/**
 * The Sources tab: the configured feeds, in the order they are consulted.
 *
 * Order is the part people miss — it decides which feed nuget.exe-style resolution consults
 * first. It is shown and reorderable (drag a row, or Alt+↑/↓) rather than implied. Machine-wide
 * feeds stay where they are: NuGet cannot rewrite their config without elevation.
 *
 * Text entry for a feed's name and URL happens on the extension host, not here: the value ends up
 * in a NuGet.config the whole team shares, so it gets validated in one place.
 */
namespace NG {
    /** The name of the row being dragged. dataTransfer carries it too, but getData is blocked
     * during dragover in every engine, so a module variable is the working truth. */
    let dragName: string | null = null;
    /** Which row to re-focus after the reorder round-trips through the host re-render. */
    let pendingFocusName: string | null = null;

    export function renderSources(): void {
        const list = el<HTMLUListElement>('sources-list');
        list.replaceChildren();

        if (state.sources.length === 0) {
            const empty = make('li', 'empty');
            empty.setAttribute('role', 'presentation');
            empty.appendChild(make('span', 'empty-title', 'No feeds are configured'));
            empty.appendChild(document.createTextNode('Add one to search for packages.'));
            list.appendChild(empty);
            return;
        }

        state.sources.forEach((source, index) => list.appendChild(buildSourceRow(source, index)));

        if (pendingFocusName) {
            const row = list.querySelector<HTMLLIElement>(
                `li.source-row[data-name="${CSS.escape(pendingFocusName)}"]`
            );
            row?.focus();
            pendingFocusName = null;
        }
    }

    function buildSourceRow(source: NuGetMsg.PackageSource, index: number): HTMLLIElement {
        const li = make('li', source.isEnabled ? 'source-row' : 'source-row disabled') as HTMLLIElement;
        li.dataset.name = source.name;
        li.tabIndex = 0;
        li.setAttribute(
            'aria-label',
            `${source.name}, feed ${index + 1} of ${state.sources.length}` +
                (source.isMachineWide ? ', machine-wide, not movable' : '')
        );

        const movable = !source.isMachineWide;
        li.draggable = movable;

        const grip = make('span', movable ? 'grip' : 'grip disabled', '⠿');
        grip.setAttribute('aria-hidden', 'true');
        grip.title = movable ? 'Drag to reorder' : 'Machine-wide feeds cannot be moved';
        li.appendChild(grip);

        const enabled = make('input') as HTMLInputElement;
        enabled.type = 'checkbox';
        enabled.checked = source.isEnabled;
        enabled.setAttribute('aria-label', `Use ${source.name}`);
        enabled.addEventListener('change', () =>
            post({
                type: 'sourceEdit',
                action: enabled.checked ? 'enable' : 'disable',
                name: source.name,
            })
        );
        li.appendChild(enabled);

        const text = make('span', 'source-text');
        const title = make('span', 'source-title');
        title.appendChild(make('span', 'id', source.name));

        if (source.isMachineWide) {
            // Machine-wide feeds live outside the user's config chain; NuGet can record them as
            // disabled but cannot rewrite or delete them without elevation.
            title.appendChild(make('span', 'badge', 'machine-wide'));
        }
        if (source.hasCredentials) {
            title.appendChild(make('span', 'badge', 'credentials in config'));
        }
        if (source.isLocal) {
            title.appendChild(make('span', 'badge', 'folder'));
        }
        text.appendChild(title);

        text.appendChild(make('span', 'muted source-url', source.source));
        if (source.configFilePath) {
            const config = make('span', 'md-link source-config', source.configFilePath);
            config.setAttribute('role', 'link');
            config.tabIndex = 0;
            config.title = 'Open this NuGet.config';
            config.addEventListener('click', () =>
                post({ type: 'openFile', path: source.configFilePath! })
            );
            text.appendChild(config);
        }
        li.appendChild(text);

        const actions = make('span', 'source-actions');

        const edit = make('button', 'linklike', 'Edit') as HTMLButtonElement;
        edit.disabled = source.isMachineWide;
        edit.addEventListener('click', () =>
            post({ type: 'sourceEdit', action: 'update', name: source.name })
        );
        actions.appendChild(edit);

        const remove = make('button', 'linklike', 'Remove') as HTMLButtonElement;
        remove.disabled = source.isMachineWide;
        remove.addEventListener('click', () =>
            post({ type: 'sourceEdit', action: 'remove', name: source.name })
        );
        actions.appendChild(remove);

        li.appendChild(actions);

        if (movable) {
            li.addEventListener('dragstart', (event) => {
                dragName = source.name;
                li.classList.add('dragging');
                event.dataTransfer?.setData('text/plain', source.name);
                if (event.dataTransfer) {
                    event.dataTransfer.effectAllowed = 'move';
                }
            });
            li.addEventListener('dragend', () => {
                // Fires on drop and on Esc alike — the one place cleanup always runs.
                dragName = null;
                li.classList.remove('dragging');
                clearDropMarkers();
            });
        }

        li.addEventListener('keydown', (event) => {
            if (!event.altKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) {
                return;
            }
            event.preventDefault();
            if (!movable) {
                return;
            }
            const to = index + (event.key === 'ArrowUp' ? -1 : 1);
            if (to < 0 || to >= state.sources.length) {
                return;
            }
            pendingFocusName = source.name;
            reorder(index, to);
        });

        return li;
    }

    function reorder(from: number, to: number): void {
        const names = state.sources.map((source) => source.name);
        const [moved] = names.splice(from, 1);
        names.splice(to, 0, moved);
        post({ type: 'sourceEdit', action: 'reorder', order: names });
    }

    function clearDropMarkers(): void {
        for (const row of document.querySelectorAll('#sources-list .drop-before, #sources-list .drop-after')) {
            row.classList.remove('drop-before', 'drop-after');
        }
    }

    /** The row under the pointer and which half of it, or null between rows' gaps. */
    function dropTarget(event: DragEvent): { row: HTMLLIElement; before: boolean } | null {
        const target = (event.target as HTMLElement).closest<HTMLLIElement>('li.source-row');
        if (!target || target.classList.contains('dragging')) {
            return null;
        }
        const rect = target.getBoundingClientRect();
        return { row: target, before: event.clientY < rect.top + rect.height / 2 };
    }

    export function wireSources(): void {
        el<HTMLButtonElement>('source-add').addEventListener('click', () =>
            post({ type: 'sourceEdit', action: 'add' })
        );
        el<HTMLButtonElement>('source-open-config').addEventListener('click', () => {
            const config = state.sources.find((source) => source.configFilePath)?.configFilePath;
            if (config) {
                post({ type: 'openFile', path: config });
            }
        });

        const list = el<HTMLUListElement>('sources-list');

        list.addEventListener('dragover', (event) => {
            if (!dragName) {
                return;
            }
            event.preventDefault();
            if (event.dataTransfer) {
                event.dataTransfer.dropEffect = 'move';
            }
            clearDropMarkers();
            const target = dropTarget(event);
            target?.row.classList.add(target.before ? 'drop-before' : 'drop-after');
        });

        list.addEventListener('drop', (event) => {
            if (!dragName) {
                return;
            }
            event.preventDefault();
            const target = dropTarget(event);
            clearDropMarkers();
            if (!target) {
                return;
            }

            const names = state.sources.map((source) => source.name);
            const from = names.indexOf(dragName);
            let to = names.indexOf(target.row.dataset.name ?? '');
            if (from < 0 || to < 0) {
                return;
            }
            if (!target.before) {
                to += 1;
            }
            if (to > from) {
                to -= 1; // Removing the dragged entry first shifts everything after it.
            }
            if (to === from) {
                return;
            }

            pendingFocusName = dragName;
            reorder(from, to);
        });
    }
}
