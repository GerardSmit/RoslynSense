/// <reference path="./icons.ts" />

/**
 * The package list.
 *
 * Rows are built once and kept. Selecting one toggles two attributes rather than rebuilding the
 * list, which is what used to re-create every `<img>` — and re-decode every data URI — on each
 * click. Rows are a fixed height with `content-visibility`, so an off-screen row costs no layout
 * without any of the accessibility damage a virtualized list does.
 */
namespace NG {
    export const rows: Row[] = [];
    export let focusedRow: Row | undefined;

    let rowSeq = 0;
    let sentinel: HTMLElement | undefined;

    export function resetList(): void {
        const list = el<HTMLUListElement>('list');
        list.replaceChildren();
        rows.length = 0;
        focusedRow = undefined;
        sentinel = undefined;
        list.removeAttribute('aria-activedescendant');
        resetIcons(list);
    }

    /** Enough rows to fill any plausible viewport; the rest can wait a frame. */
    const ChunkSize = 100;

    export function appendRows(packages: NuGetMsg.PackageSummary[]): void {
        const list = el<HTMLUListElement>('list');

        if (sentinel) {
            sentinel.remove();
            sentinel = undefined;
        }

        // The first chunk paints immediately; the rest arrive on later frames so a solution with
        // a thousand references does not block the first render on building all of them.
        if (packages.length === 0) {
            renderEmptyState(list);
            setCount(state.tab, 0);
            return;
        }

        const generation = listGen;
        const flush = (from: number) => {
            if (generation !== listGen) {
                return;
            }

            const fragment = document.createDocumentFragment();
            const until = Math.min(from + ChunkSize, packages.length);

            for (let i = from; i < until; i++) {
                const row = buildRow(packages[i]);
                rows.push(row);
                fragment.appendChild(row.li);
                attachIcon(row);
            }
            list.appendChild(fragment);

            if (until < packages.length) {
                requestAnimationFrame(() => flush(until));
                return;
            }

            renderEmptyState(list);
            setCount(state.tab, rows.length);
            if (state.tab === 'browse' && state.hasMore) {
                addSentinel(list);
            }
            applyPendingSelection();
        };

        flush(0);
    }

    /** The tab badge. Browse has no meaningful total, so it never carries one. */
    export function setCount(tab: NuGetMsg.Tab, count: number): void {
        const badge = document.querySelector<HTMLElement>(`.count[data-count="${tab}"]`);
        if (badge) {
            badge.textContent = tab === 'browse' || count === 0 ? '' : String(count);
        }
    }

    export function setRows(packages: NuGetMsg.PackageSummary[]): void {
        resetList();
        appendRows(packages);
    }

    export function focusRow(row: Row, showDetails = true): void {
        if (focusedRow === row) {
            return;
        }
        focusedRow?.li.setAttribute('aria-selected', 'false');
        row.li.setAttribute('aria-selected', 'true');
        el<HTMLUListElement>('list').setAttribute('aria-activedescendant', row.li.id);
        focusedRow = row;
        row.li.scrollIntoView({ block: 'nearest' });

        if (showDetails) {
            state.selectedVersion = null;
            requestDetails(row);
        }
        persist();
    }

    export function selectById(id: string): boolean {
        const row = rows.find((r) => r.pkg.id.toLowerCase() === id.toLowerCase());
        if (!row) {
            return false;
        }
        focusRow(row);
        return true;
    }

    function applyPendingSelection(): void {
        if (state.pendingSelect && selectById(state.pendingSelect)) {
            state.pendingSelect = null;
        }
    }

    function buildRow(pkg: NuGetMsg.PackageSummary): Row {
        const li = make('li', 'row');
        li.id = `nr-${++rowSeq}`;
        li.tabIndex = -1;
        li.setAttribute('aria-selected', 'false');

        // Updates is a grid, not a listbox: conflating "focused, showing details" with "checked
        // for update" would mean clicking a row to read its changelog quietly queues a mutation.
        const grid = state.tab === 'updates';
        li.setAttribute('role', grid ? 'row' : 'option');

        let check: HTMLInputElement | undefined;
        if (grid) {
            const cell = make('span', 'cell');
            cell.setAttribute('role', 'gridcell');
            check = make('input') as HTMLInputElement;
            check.type = 'checkbox';
            check.addEventListener('click', (event) => event.stopPropagation());
            check.addEventListener('change', onSelectionChanged);
            cell.appendChild(check);
            li.appendChild(cell);
        }

        const icon = make('span', 'icon');
        icon.setAttribute('aria-hidden', 'true');
        const fallback = make('span', 'icon-fallback', (pkg.id[0] ?? '?').toUpperCase());
        const img = make('img', 'icon-img') as HTMLImageElement;
        img.alt = '';
        img.hidden = true;
        img.decoding = 'async';
        icon.appendChild(fallback);
        icon.appendChild(img);
        li.appendChild(icon);

        const text = make('span', 'row-text');
        if (grid) {
            text.setAttribute('role', 'gridcell');
        }

        const title = make('span', 'row-title');
        title.appendChild(make('span', 'id', pkg.id));

        const badges = make('span', 'badges');
        title.appendChild(badges);
        text.appendChild(title);

        text.appendChild(
            make(
                'span',
                'muted row-meta',
                [pkg.authors, pkg.version, formatCount(pkg.downloads)].filter(Boolean).join(' · ')
            )
        );

        if (pkg.description) {
            text.appendChild(make('span', 'muted row-desc', pkg.description));
        }
        li.appendChild(text);

        const row: Row = {
            pkg,
            li,
            iconImg: img,
            iconFallback: fallback,
            badges,
            check,
            projectPaths: projectsWith(pkg.id),
        };

        if (check) {
            check.setAttribute('aria-label', `Update ${pkg.id}`);
        }

        li.addEventListener('click', () => focusRow(row));
        decorateRow(row);
        return row;
    }

    /** Installed / vulnerable / deprecated / centrally-managed markers, rebuilt on demand. */
    export function decorateRow(row: Row): void {
        row.badges.replaceChildren();

        if (row.pkg.installedVersion) {
            const label =
                row.pkg.installedVersions.length > 1
                    ? `installed ${row.pkg.installedVersions.join(', ')}`
                    : `installed ${row.pkg.installedVersion}`;
            row.badges.appendChild(make('span', 'badge', label));
        }

        if (row.pkg.isGlobalPackageReference) {
            row.badges.appendChild(make('span', 'badge', 'global'));
        } else if (row.pkg.isCentrallyManaged) {
            row.badges.appendChild(make('span', 'badge', 'central'));
        }

        if (row.severity && row.severity !== 'none') {
            // The word, not only a colour: chart colours carry no contrast guarantee, and a
            // colour-only severity is unreadable to a large minority of users.
            row.badges.appendChild(make('span', `sev sev-${row.severity}`, row.severity));
        }

        const advisories = auditFor(row.pkg.id);
        if (advisories.worst >= 0) {
            row.badges.appendChild(
                make('span', 'sev sev-vuln', `vulnerable · ${severityName(advisories.worst)}`)
            );
        }
        if (advisories.deprecated) {
            row.badges.appendChild(make('span', 'sev sev-deprecated', 'deprecated'));
        }
    }

    export function auditFor(id: string): { worst: number; deprecated: boolean } {
        const audit = state.audit;
        if (!audit) {
            return { worst: -1, deprecated: false };
        }

        let worst = -1;
        for (const advisory of audit.vulnerabilities) {
            if (advisory.id.toLowerCase() === id.toLowerCase()) {
                worst = Math.max(worst, advisory.severity);
            }
        }

        return {
            worst,
            deprecated: audit.deprecations.some((d) => d.id.toLowerCase() === id.toLowerCase()),
        };
    }

    export function projectsWith(id: string): string[] {
        return state.installed
            .filter((project) => project.packages.some((p) => p.id.toLowerCase() === id.toLowerCase()))
            .map((project) => project.projectPath);
    }

    /**
     * An empty list has to say which of the several empty-ish things it is. "No results" and
     * "you have not searched yet" look identical otherwise, and only one of them is a problem.
     */
    export function renderEmptyState(list?: HTMLElement): void {
        const target = list ?? el<HTMLUListElement>('list');
        target.querySelector('.empty')?.remove();

        if (rows.length > 0 || state.tab === 'sources') {
            return;
        }

        const states: Record<NuGetMsg.Tab, [string, string]> = {
            browse: state.query
                ? [`Nothing matches “${state.query}”`, 'Try a different term, or another source.']
                : ['Search the configured feeds', 'Start typing a package name.'],
            installed: ['No packages referenced', 'Nothing in this solution references a NuGet package.'],
            updates: ['Everything is up to date', 'No installed package has a newer version available.'],
            consolidate: [
                'No version conflicts',
                'Every package is referenced at the same version across the solution.',
            ],
            sources: ['', ''],
        };

        const [title, detail] = states[state.tab];
        const empty = make('li', 'empty');
        empty.setAttribute('role', 'presentation');
        empty.appendChild(make('span', 'empty-title', title));
        empty.appendChild(document.createTextNode(detail));
        target.appendChild(empty);
    }

    /**
     * Shown from the moment a list request goes out until its reply lands.
     *
     * Without it a request that is slow, or that never answers at all, looks exactly like a panel
     * with nothing in it: the list only ever gets content when a reply arrives, so a pending search
     * painted an empty pane with no indication that anything was on its way.
     */
    export function renderLoading(): void {
        resetList();

        const loading = make('li', 'empty loading');
        loading.setAttribute('role', 'presentation');
        loading.appendChild(make('span', 'empty-title', 'Loading…'));
        el<HTMLUListElement>('list').appendChild(loading);
    }

    /** Paging: the Browse tab asks for the next page when the end of the list comes into view. */
    function addSentinel(list: HTMLElement): void {
        sentinel = make('li', 'sentinel muted', 'Loading more…');
        sentinel.setAttribute('role', 'presentation');
        list.appendChild(sentinel);

        const watcher = new IntersectionObserver(
            (entries) => {
                if (entries.some((e) => e.isIntersecting)) {
                    watcher.disconnect();
                    post({
                        type: 'search',
                        gen: listGen,
                        query: state.query,
                        includePrerelease: el<HTMLInputElement>('prerelease').checked,
                        source: el<HTMLSelectElement>('source').value,
                        skip: rows.length,
                    });
                }
            },
            { root: list, rootMargin: '200px 0px' }
        );
        watcher.observe(sentinel);
    }

    export function wireListKeyboard(): void {
        const list = el<HTMLUListElement>('list');

        list.addEventListener('keydown', (event) => {
            if (rows.length === 0) {
                return;
            }

            const current = focusedRow ? rows.indexOf(focusedRow) : -1;
            let next = current;

            switch (event.key) {
                case 'ArrowDown':
                    next = nextVisible(current, 1);
                    break;
                case 'ArrowUp':
                    next = nextVisible(current, -1);
                    break;
                case 'Home':
                    next = nextVisible(-1, 1);
                    break;
                case 'End':
                    next = nextVisible(rows.length, -1);
                    break;
                case 'PageDown':
                    next = clampVisible(current + 10);
                    break;
                case 'PageUp':
                    next = clampVisible(current - 10);
                    break;
                case ' ':
                    if (focusedRow?.check) {
                        event.preventDefault();
                        focusedRow.check.checked = !focusedRow.check.checked;
                        onSelectionChanged();
                    }
                    return;
                default:
                    return;
            }

            if (next >= 0 && next < rows.length && next !== current) {
                event.preventDefault();
                focusRow(rows[next]);
            }
        });
    }

    function nextVisible(from: number, step: number): number {
        for (let i = from + step; i >= 0 && i < rows.length; i += step) {
            if (!rows[i].li.hidden) {
                return i;
            }
        }
        return from;
    }

    function clampVisible(index: number): number {
        return Math.max(0, Math.min(rows.length - 1, index));
    }
}
