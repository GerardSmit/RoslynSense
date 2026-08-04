/// <reference path="./state.ts" />

/**
 * The Sources tab: the configured feeds, in the order they are consulted.
 *
 * Order is the part people miss — it decides which feed answers first, and with it which one a
 * package published to two feeds resolves from. It is shown and reorderable rather than implied.
 *
 * Text entry for a feed's name and URL happens on the extension host, not here: the value ends up
 * in a NuGet.config the whole team shares, so it gets validated in one place.
 */
namespace NG {
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
    }

    function buildSourceRow(source: NuGetMsg.PackageSource, index: number): HTMLLIElement {
        const li = make('li', source.isEnabled ? 'source-row' : 'source-row disabled');

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
        actions.appendChild(
            move('Move up', '↑', index > 0, () => reorder(index, index - 1))
        );
        actions.appendChild(
            move('Move down', '↓', index < state.sources.length - 1, () => reorder(index, index + 1))
        );

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
        return li;
    }

    function move(label: string, glyph: string, allowed: boolean, act: () => void): HTMLButtonElement {
        const button = make('button', 'linklike arrow', glyph) as HTMLButtonElement;
        button.title = label;
        button.setAttribute('aria-label', label);
        button.disabled = !allowed;
        button.addEventListener('click', act);
        return button;
    }

    function reorder(from: number, to: number): void {
        const names = state.sources.map((source) => source.name);
        const [moved] = names.splice(from, 1);
        names.splice(to, 0, moved);
        post({ type: 'sourceEdit', action: 'reorder', order: names });
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
    }
}
