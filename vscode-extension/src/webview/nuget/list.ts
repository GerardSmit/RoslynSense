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

    /**
     * Rows that are on screen but not in the list: the Updates tab's induced-dependency rows, and
     * whatever virtual row the details pane is currently showing. They want icons like anything
     * else, but keyboard navigation, select-all and the tab counts must not see them.
     */
    export const secondaryRows: Row[] = [];

    export let focusedRow: Row | undefined;

    let rowSeq = 0;
    let sentinel: HTMLElement | undefined;

    export function resetList(): void {
        const list = el<HTMLUListElement>('list');
        list.replaceChildren();
        rows.length = 0;
        secondaryRows.length = 0;
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
            settled();
            applyPendingSelection();
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

            setCount(state.tab, rows.length);
            if (state.tab === 'browse' && state.hasMore) {
                addSentinel(list);
            }
            settled();
            applyPendingSelection();
        };

        flush(0);
    }

    /**
     * Everything that has to see the whole list, once the last chunk is in.
     *
     * The chunking is why this is a step of its own: rows arrive across several frames, so anything
     * that counts or plans against `rows` has to wait. The Updates tab is the one that shows it —
     * the select-all state and the dependency plan are derived from the ticked rows, and running
     * them after the first hundred would plan an update for the first hundred.
     */
    function settled(): void {
        // Late chunks arrive after the filter last ran; they must not surface rows the query,
        // the chip or the source filter hides.
        applyRowFilter();

        if (state.tab === 'updates') {
            onSelectionChanged();
        }
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
        pushNav({ tab: state.tab, packageId: row.pkg.id });

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

    /**
     * Shows a package's details, whether or not it is in the current list.
     *
     * A clicked dependency usually is not — it may not be installed, and Browse is showing search
     * results for something else entirely. Refusing to open those would make dependencies links
     * that mostly do nothing.
     */
    export function openPackage(id: string): void {
        if (selectById(id)) {
            return;
        }

        focusedRow?.li.setAttribute('aria-selected', 'false');
        focusedRow = virtualRow(id);
        pushNav({ tab: state.tab, packageId: id });
        state.selectedVersion = null;
        requestDetails(focusedRow);
        persist();
    }

    export function clearDetails(): void {
        focusedRow?.li.setAttribute('aria-selected', 'false');
        focusedRow = undefined;
        const details = el<HTMLElement>('details');
        details.replaceChildren(make('p', 'placeholder', 'Select a package to see its details.'));
        persist();
    }

    /**
     * A details target for a package that has no row — a clicked dependency, or a history entry
     * whose tab no longer lists it.
     *
     * The `<li>` is built and never appended: `renderDetails` works from `pkg` and `projectPaths`,
     * but the icon plumbing and the selection helpers all expect a `Row`, and a detached element is
     * cheaper than making half of `Row` optional everywhere. Installed facts are merged in from the
     * projects reply so Update, Uninstall and Consolidate behave exactly as they would from the
     * Installed tab rather than treating a referenced package as brand new.
     */
    export function virtualRow(id: string): Row {
        const installed = state.installed
            .flatMap((project) => project.packages)
            .filter((pkg) => pkg.id.toLowerCase() === id.toLowerCase());

        const versions = [...new Set(installed.flatMap((pkg) => pkg.installedVersions))];
        const known = rows.find((row) => row.pkg.id.toLowerCase() === id.toLowerCase())?.pkg;

        const pkg: NuGetMsg.PackageSummary = {
            id: installed[0]?.id ?? known?.id ?? id,
            version: known?.version ?? installed[0]?.version ?? '',
            authors: null,
            description: null,
            downloads: null,
            iconUrl: known?.iconUrl ?? installed[0]?.iconUrl ?? null,
            deprecated: false,
            vulnerable: false,
            installedVersion: installed[0]?.installedVersion ?? null,
            installedVersions: versions,
            isCentrallyManaged: installed.some((p) => p.isCentrallyManaged),
            isGlobalPackageReference: installed.some((p) => p.isGlobalPackageReference),
            versionSource: installed.find((p) => p.versionSource)?.versionSource ?? null,
            sourceName: null,
        };

        const li = make('li', 'row');
        li.id = `nr-${++rowSeq}`;
        const fallback = make('span', 'icon-fallback', (pkg.id[0] ?? '?').toUpperCase());
        const img = make('img', 'icon-img') as HTMLImageElement;
        img.hidden = true;

        return {
            pkg,
            li,
            iconImg: img,
            iconFallback: fallback,
            badges: make('span', 'badges'),
            projectPaths: projectsWith(pkg.id),
        };
    }

    function applyPendingSelection(): void {
        if (!state.pendingSelect) {
            return;
        }
        // Not in this tab's list — a package can be a dependency of something installed without
        // being installed itself. Dropping the request would make Back silently do nothing.
        const id = state.pendingSelect;
        state.pendingSelect = null;
        openPackage(id);
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

        // Only the Installed tab has per-row actions; elsewhere the details pane is the actor.
        let actions: HTMLElement | undefined;
        if (state.tab === 'installed') {
            actions = make('span', 'row-actions');
            li.appendChild(actions);
        }

        const row: Row = {
            pkg,
            li,
            iconImg: img,
            iconFallback: fallback,
            badges,
            actions,
            check,
            projectPaths: projectsWith(pkg.id),
        };

        // The Updates tab's rows carry what they are going to do. Reading it here rather than
        // patching it on afterwards is what lets the whole tab be built in one appendRows call.
        if (grid) {
            const group = updateGroupFor(pkg);
            if (group.length > 0) {
                row.update = group[0];
                row.severity = group[0].severity;
                row.projectPaths = group.map((u) => u.projectPath);
                li.title = [...new Set(group.map((u) => u.projectName))].join(', ');
            }
            if (check) {
                check.setAttribute('aria-label', `Update ${pkg.id}`);
                check.checked = group.length > 0 && preselected(group);
            }
        }

        li.addEventListener('click', () => focusRow(row));
        decorateRow(row);
        return row;
    }

    /** Installed / update / vulnerable / deprecated / centrally-managed markers, rebuilt on demand. */
    export function decorateRow(row: Row): void {
        row.badges.replaceChildren();
        row.actions?.replaceChildren();

        const pending = state.tab === 'installed' ? updatesFor(row.pkg.id) : [];

        if (row.pkg.installedVersion) {
            const installed = row.pkg.installedVersions.join(', ');
            const latest = [...new Set(pending.map((u) => u.latestVersion))].join(', ');
            const label = latest ? `${installed} → ${latest}` : `installed ${installed}`;
            row.badges.appendChild(make('span', latest ? 'badge upd' : 'badge', label));
        }

        if (state.tab === 'installed' && row.pkg.installedVersions.length > 1) {
            const mixed = make('span', 'sev sev-minor', 'mixed versions');
            mixed.title =
                'Projects reference this package at different versions. ' +
                'Use Consolidate in the details pane to align them.';
            row.badges.appendChild(mixed);
        }

        if (pending.length > 0 && row.actions) {
            row.actions.appendChild(buildRowUpdate(row, pending));
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

        const uncapped = row.update?.latestUncapped ?? pending.find((u) => u.latestUncapped)?.latestUncapped;
        if (uncapped) {
            // Band alignment held the offer back; disclose what exists so the cap never reads as
            // "up to date" to someone who knows the newer band shipped.
            const capped = make('span', 'badge upd-capped', `${uncapped} available`);
            capped.title =
                `${uncapped} tracks a newer .NET than this project targets. ` +
                'Turn off roslynSense.nuget.alignPlatformPackages to update across bands.';
            row.badges.appendChild(capped);
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

    export function updatesFor(id: string): NuGetMsg.PackageUpdate[] {
        return state.updates.filter((u) => u.id.toLowerCase() === id.toLowerCase());
    }

    /**
     * The Installed tab's one-click Update. It goes through updateAll rather than install:
     * install shells `dotnet add package`, which would also add the reference to selected
     * projects that do not have it — updateAll edits versions only, writes
     * Directory.Packages.props under CPM, and the host's silent plan drags induced references
     * along so the restore cannot NU1605.
     */
    function buildRowUpdate(row: Row, pending: NuGetMsg.PackageUpdate[]): HTMLButtonElement {
        // Grouped by target version: with different TFMs the same package can resolve to
        // different latests in different projects, and each project must get its own.
        const byVersion = new Map<string, string[]>();
        for (const update of pending) {
            const paths = byVersion.get(update.latestVersion);
            if (paths) {
                paths.push(update.projectPath);
            } else {
                byVersion.set(update.latestVersion, [update.projectPath]);
            }
        }

        const button = make('button', 'linklike row-update', 'Update') as HTMLButtonElement;
        button.title = `Update to ${[...byVersion.keys()].join(', ')} in ${
            new Set(pending.map((u) => u.projectName)).size
        } project(s)`;
        button.setAttribute('aria-label', `Update ${row.pkg.id}`);
        button.addEventListener('click', (event) => {
            // The row click handler focuses the row and loads details; the button is an action,
            // not a selection.
            event.stopPropagation();
            row.li.classList.add('row-working');
            post({
                type: 'updateAll',
                packages: [...byVersion].map(([version, projectPaths]) => ({
                    id: row.pkg.id,
                    version,
                    projectPaths,
                })),
                versionLock: el<HTMLSelectElement>('version-lock').value as NuGetMsg.Lock,
                includePrerelease: el<HTMLInputElement>('prerelease').checked,
            });
        });
        return button;
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

    export type InstalledFilter = 'all' | 'updates' | 'mixed';
    let installedFilter: InstalledFilter = 'all';

    export function setInstalledFilter(filter: InstalledFilter): void {
        installedFilter = filter;
        applyRowFilter();
    }

    /**
     * Everything that narrows the list, applied in one pass.
     *
     * Three filters can be active at once — the search box, the Installed tab's chips, and the feed
     * selector — and they compose by AND. Keeping them in one place is what makes the chip counts
     * agree with what is on screen; when each filter owned its own pass, the last one to run
     * decided, and the counts described a list nobody was looking at.
     *
     * Hidden rows stay built: keyboard navigation already skips them (`nextVisible`), and toggling
     * `hidden` is far cheaper than rebuilding a thousand rows per keystroke.
     */
    export function applyRowFilter(): void {
        const query = state.query.trim().toLowerCase();
        const source = selectedSource();

        let updates = 0;
        let mixed = 0;

        for (const row of rows) {
            const hasUpdate = updatesFor(row.pkg.id).length > 0;
            const isMixed = row.pkg.installedVersions.length > 1;
            updates += hasUpdate ? 1 : 0;
            mixed += isMixed ? 1 : 0;

            // Browse already asked the feed for the query, so filtering its results again would
            // hide packages the feed itself considered a match.
            const matchesQuery =
                state.tab === 'browse' || query.length === 0 || row.pkg.id.toLowerCase().includes(query);

            const matchesChip =
                state.tab !== 'installed' ||
                (installedFilter === 'updates' ? hasUpdate :
                 installedFilter === 'mixed' ? isMixed : true);

            row.li.hidden = !(matchesQuery && matchesChip && onSelectedSource(row.pkg.id, source));
        }

        if (state.tab === 'installed') {
            const counts: Record<InstalledFilter, string> = {
                all: `All (${rows.length})`,
                updates: `Updates (${updates})`,
                mixed: `Mixed versions (${mixed})`,
            };
            for (const chip of document.querySelectorAll<HTMLButtonElement>('#installed-toolbar .filter')) {
                const filter = chip.dataset.filter as InstalledFilter;
                chip.textContent = counts[filter];
                chip.setAttribute('aria-pressed', String(filter === installedFilter));
            }
        }

        renderEmptyState();
    }

    /**
     * Whether a feed carries this package.
     *
     * The Updates tab is filtered by the server, which resolves versions from the chosen feed and
     * so knows the answer already. Only Installed needs this, and only once the map has arrived —
     * until then nothing is hidden, so the list is briefly wider than it will be rather than
     * briefly wrong.
     */
    function onSelectedSource(id: string, source: string): boolean {
        if (source.length === 0 || state.sourceMap === null) {
            return true;
        }
        return (state.sourceMap[id.toLowerCase()] ?? []).some(
            (name) => name.toLowerCase() === source.toLowerCase()
        );
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

        if (state.tab === 'sources' || rows.some((row) => !row.li.hidden)) {
            return;
        }

        // A list can be empty three different ways, and only one of them is a problem: nothing
        // installed, nothing left after the filters, or nothing searched for yet. Saying "no
        // packages" when a feed filter is hiding forty of them sends people looking for a bug, so
        // the message names whichever filter is doing the hiding.
        const source = selectedSource();
        if (rows.length > 0) {
            const reason =
                state.query ? `Nothing here matches “${state.query}”`
                : source ? `Nothing here comes from ${source}`
                : 'Nothing matches this filter';
            const filtered = make('li', 'empty');
            filtered.setAttribute('role', 'presentation');
            filtered.appendChild(make('span', 'empty-title', reason));
            filtered.appendChild(
                document.createTextNode(
                    `${rows.length} package${rows.length === 1 ? ' is' : 's are'} hidden by the current filters.`
                )
            );
            target.appendChild(filtered);
            return;
        }

        const states: Record<NuGetMsg.Tab, [string, string]> = {
            browse: state.query
                ? [`Nothing matches “${state.query}”`, 'Try a different term, or another source.']
                : ['Search the configured feeds', 'Start typing a package name.'],
            installed: ['No packages referenced', 'Nothing in this solution references a NuGet package.'],
            updates: source
                ? [`Nothing to update from ${source}`, 'No package on this feed has a newer version available.']
                : ['Everything is up to date', 'No installed package has a newer version available.'],
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
