/// <reference path="./details.ts" />
/// <reference path="./updates.ts" />
/// <reference path="./sources.ts" />
/// <reference path="./splitter.ts" />
/// <reference path="./nav.ts" />

/** Boot, tab switching and the message loop. */
namespace NG {
    export function start(): void {
        // The message loop goes on first and each step is isolated. Wiring a control that is not
        // in the document throws, and when that happened inside one long start() it took out
        // everything after it — including the listener that receives boot, so the panel came up
        // completely empty with no error anywhere.
        wireMessages();

        const steps = [wireHeader, wireListKeyboard, wireUpdates, wireSources, wireSplitter, wireNavigation];
        for (const step of steps) {
            try {
                step();
            } catch (error) {
                console.error('[nuget] wiring failed', error);
            }
        }

        post({ type: 'ready', state: savedState() });
    }

    function wireHeader(): void {
        const query = el<HTMLInputElement>('query');
        let debounce: number | undefined;

        query.addEventListener('input', () => {
            window.clearTimeout(debounce);
            debounce = window.setTimeout(() => applyQuery(query.value), 300);
        });

        el<HTMLInputElement>('prerelease').addEventListener('change', () => refresh());
        // Every tab, not only Browse: a feed selector that silently means "Browse only" is worse
        // than no feed selector, because Installed and Updates go on listing packages that feed
        // has never heard of.
        el<HTMLSelectElement>('source').addEventListener('change', () => {
            state.sourceMap = null;
            refresh();
        });

        el<HTMLButtonElement>('scope').addEventListener('click', () => post({ type: 'pickScope' }));

        for (const button of document.querySelectorAll<HTMLButtonElement>('nav button')) {
            button.addEventListener('click', () => switchTab(button.dataset.tab as NuGetMsg.Tab));
        }

        for (const chip of document.querySelectorAll<HTMLButtonElement>('#installed-toolbar .filter')) {
            chip.addEventListener('click', () =>
                setInstalledFilter(chip.dataset.filter as InstalledFilter)
            );
        }

        document.addEventListener('keydown', (event) => {
            if (event.key === '/' && document.activeElement !== query) {
                event.preventDefault();
                query.focus();
            } else if (event.key === 'Escape' && document.activeElement === query) {
                query.value = '';
                applyQuery('');
            }
        });
    }

    /**
     * The search box searches whatever tab you are on.
     *
     * It used to jump to Browse on the first keystroke, which meant there was no way to find a
     * package among four hundred installed ones — the tab you were looking at vanished as soon as
     * you typed its name. Browse still asks the feed, because that is the only list the panel does
     * not already hold; the rest narrow in place.
     */
    function applyQuery(value: string): void {
        state.query = value;

        if (state.tab === 'browse') {
            refresh();
        } else if (state.tab !== 'sources') {
            applyRowFilter();
        }
        persist();
    }

    export function switchTab(tab: NuGetMsg.Tab): void {
        // A restored state can carry a tab this version no longer has (the Consolidate tab
        // folded into Installed); an unknown value must not strand the panel on a blank pane.
        if (!['browse', 'installed', 'updates', 'sources'].includes(tab)) {
            tab = 'browse';
        }
        state.tab = tab;
        pushNav({ tab, packageId: null });

        for (const button of document.querySelectorAll<HTMLButtonElement>('nav button')) {
            button.setAttribute('aria-selected', String(button.dataset.tab === tab));
        }

        el<HTMLElement>('updates-toolbar').hidden = tab !== 'updates';
        el<HTMLElement>('installed-toolbar').hidden = tab !== 'installed';
        el<HTMLElement>('summary').hidden = true;

        // Feeds are not packages: the box has nothing to search here, and a control that silently
        // does nothing is worse than one that says so.
        const query = el<HTMLInputElement>('query');
        query.disabled = tab === 'sources';
        query.placeholder =
            tab === 'browse' ? 'Search packages…'
            : tab === 'sources' ? 'Search does not apply to feeds'
            : `Filter ${tab}…`;

        // Feeds are a different shape from packages, so they get their own pane rather than
        // pretending a feed is a row in the same listbox.
        el<HTMLElement>('pane-packages').hidden = tab === 'sources';
        el<HTMLElement>('pane-sources').hidden = tab !== 'sources';

        refresh();
        persist();
    }

    export function refresh(): void {
        const gen = nextListGen();
        const prerelease = el<HTMLInputElement>('prerelease').checked;

        // Sources has its own pane, and every other tab is about to replace the list wholesale.
        if (state.tab !== 'sources') {
            renderLoading();
        }

        switch (state.tab) {
            case 'browse':
                post({
                    type: 'search',
                    gen,
                    query: state.query,
                    includePrerelease: prerelease,
                    source: el<HTMLSelectElement>('source').value,
                    skip: 0,
                });
                break;
            case 'installed':
                // The same generation for all three: the updates reply decorates the rows the
                // projects reply builds, and a mismatched gen would orphan one of them.
                post({ type: 'installed', gen });
                post({ type: 'audit', gen, refresh: false });
                requestUpdates(gen);
                break;
            case 'updates':
                requestUpdates();
                post({ type: 'audit', gen: listGen, refresh: false });
                break;
            case 'sources':
                post({ type: 'sources' });
                break;
        }
    }

    function wireMessages(): void {
        window.addEventListener('message', (event: MessageEvent<NuGetMsg.ToView>) => {
            const message = event.data;

            // A throw in here used to be invisible. `boot` is the one that hurt: it wires the
            // scope and the feed list before it asks for the first page, so failing partway
            // through left a panel that looked finished and had issued no request at all.
            try {
                switch (message.type) {
                    case 'boot':
                        state.projects = message.projects;
                        state.sources = message.sources;
                        state.settings = message.settings;
                        applyTokenColors(message.settings.codeTokenColors);
                        setScope(message.scope);
                        fillSources(message.sources);
                        // Without this a first-ever open sits on an empty list having issued no
                        // request at all, with no hint that a search is what it wants.
                        if (message.state) {
                            restore(message.state);
                        } else {
                            switchTab(state.tab);
                        }
                        break;

                    case 'settings':
                        state.settings = message.settings;
                        applyTokenColors(message.settings.codeTokenColors);
                        break;

                    case 'results':
                        if (message.gen !== listGen) {
                            return;
                        }
                        state.hasMore = message.hasMore;
                        showFeeds(message.feeds);
                        // skip > 0 is the paging case; anything else replaces the list, so a new
                        // query cannot append itself onto the previous one's results.
                        if (message.skip > 0) {
                            appendRows(message.results);
                        } else {
                            setRows(message.results);
                        }
                        break;

                    case 'projects':
                        state.installed = message.projects;
                        state.projects = message.projects.map((p) => ({
                            projectPath: p.projectPath,
                            projectName: p.projectName,
                            targetFrameworks: p.targetFrameworks,
                        }));
                        if (state.tab === 'installed' && message.gen === listGen) {
                            const merged = mergeInstalled(message.projects);
                            setRows(merged);
                            requestPackageSources(merged.map((pkg) => pkg.id));
                        }
                        break;

                    case 'packageSources':
                        // A reply for a feed the user has since moved off would filter the list by
                        // the wrong feed entirely — worse than not filtering it at all.
                        if (message.gen !== listGen || message.source !== selectedSource()) {
                            return;
                        }
                        state.sourceMap = Object.fromEntries(
                            Object.entries(message.map).map(([id, sources]) => [id.toLowerCase(), sources])
                        );
                        applyRowFilter();
                        break;

                    case 'updates':
                        if (message.gen !== listGen) {
                            return;
                        }
                        state.updates = message.updates;
                        if (state.tab === 'updates') {
                            showFeeds(message.feeds);
                            showUpdates(message.updates);
                        } else if (state.tab === 'installed') {
                            // The Installed tab asked, to decorate its rows with "8.0.1 → 9.0.4"
                            // hints. Rows built after this reply pick the data up in buildRow;
                            // rows already built are re-decorated here — same shape as audit.
                            setCount('updates', groupedUpdateCount(message.updates));
                            for (const row of rows) {
                                decorateRow(row);
                            }
                            applyRowFilter();
                        }
                        break;

                    case 'updatePlan':
                        if (message.gen !== planGen || state.tab !== 'updates') {
                            return;
                        }
                        showPlan(message.induced);
                        break;

                    case 'audit':
                        state.audit = message.audit;
                        for (const row of rows) {
                            decorateRow(row);
                        }
                        break;

                    case 'versions':
                        state.versions[message.id] = message.versions;
                        if (focusedRow?.pkg.id === message.id) {
                            renderDetails(focusedRow, state.metadata[metadataKey(focusedRow.pkg)] ?? null);
                        }
                        break;

                    case 'metadata':
                        if (message.gen !== detailsGen) {
                            return;
                        }
                        state.metadata[`${message.id}/${message.version}`] = message.metadata;
                        if (focusedRow?.pkg.id === message.id) {
                            renderDetails(focusedRow, message.metadata);
                        }
                        break;

                    case 'icon':
                        onIcon(message.key, message.dataUri);
                        break;

                    case 'transitive':
                        if (message.gen !== detailsGen || !focusedRow) {
                            return;
                        }
                        setTransitive(focusedRow.pkg.id, message.packages);
                        renderDetails(focusedRow, state.metadata[metadataKey(focusedRow.pkg)] ?? null);
                        break;

                    case 'goToTab':
                        switchTab(message.tab);
                        break;

                    case 'scope':
                        setScope(message.projectPaths);
                        state.pendingSelect = message.selectPackage ?? state.pendingSelect;
                        if (message.selectPackage) {
                            switchTab('installed');
                        } else if (focusedRow) {
                            renderDetails(focusedRow, state.metadata[metadataKey(focusedRow.pkg)] ?? null);
                        }
                        break;

                    case 'sources':
                        fillSources(message.sources);
                        break;

                    case 'sourceEditResult': {
                        fillSources(message.sources);
                        const strip = el<HTMLElement>('summary');
                        strip.hidden = false;
                        strip.replaceChildren(
                            banner(message.success ? 'info' : 'error', message.message)
                        );
                        // A feed change can add or remove results, so whatever tab is showing is stale.
                        if (state.tab !== 'sources') {
                            refresh();
                        }
                        break;
                    }

                    case 'busy':
                        el<HTMLElement>('progress').classList.toggle('active', message.busy);
                        break;

                    case 'opResult':
                        showUpdateOutcomes(message.results);
                        break;

                    case 'refresh':
                        clearPendingIcons();
                        refresh();
                        break;

                    case 'error':
                        clearPendingIcons();
                        if (message.scope === 'details') {
                            el<HTMLElement>('details').prepend(banner('error', message.message));
                        } else {
                            const strip = el<HTMLElement>('summary');
                            strip.hidden = false;
                            strip.replaceChildren(banner('error', message.message));
                        }
                        break;
                }
            } catch (error) {
                console.error('[nuget] message failed', message.type, error);
                const strip = el<HTMLElement>('summary');
                strip.hidden = false;
                strip.replaceChildren(
                    banner('error', error instanceof Error ? error.message : String(error))
                );
            }
        });
    }

    /**
     * Asks which feeds carry each installed package, but only when a feed is actually selected.
     *
     * Unlike Updates, the Installed list is read from project files and knows nothing about feeds,
     * so this is the one place the filter costs a round trip. It shares the update check's cache
     * server-side, and "All sources" — the normal case — never asks at all.
     */
    function requestPackageSources(ids: string[]): void {
        const source = selectedSource();
        if (source.length === 0 || ids.length === 0) {
            state.sourceMap = null;
            return;
        }
        post({ type: 'packageSources', gen: listGen, ids, source });
    }

    /** One row per package id, with the versions every project resolved to. */
    function mergeInstalled(projects: NuGetMsg.ProjectPackages[]): NuGetMsg.PackageSummary[] {
        const byId = new Map<string, NuGetMsg.PackageSummary>();

        for (const project of projects) {
            for (const pkg of project.packages) {
                const key = pkg.id.toLowerCase();
                const existing = byId.get(key);
                if (!existing) {
                    byId.set(key, { ...pkg, installedVersions: [...new Set(pkg.installedVersions)] });
                    continue;
                }
                existing.installedVersions = [
                    ...new Set([...existing.installedVersions, ...pkg.installedVersions]),
                ];
            }
        }

        return [...byId.values()].sort((a, b) => a.id.localeCompare(b.id));
    }

    /** The Updates tab badge counts one entry per (package, latest), the way its rows group. */
    function groupedUpdateCount(updates: NuGetMsg.PackageUpdate[]): number {
        return new Set(updates.map((u) => `${u.id}|${u.latestVersion}`.toLowerCase())).size;
    }

    function showFeeds(feeds: NuGetMsg.FeedOutcome[]): void {
        const strip = el<HTMLElement>('feeds');
        strip.replaceChildren();

        const failed = feeds.filter((feed) => !feed.ok);
        if (failed.length === 0) {
            strip.hidden = true;
            return;
        }

        strip.hidden = false;
        strip.appendChild(
            make('span', undefined, `${failed.length} of ${feeds.length} feeds did not answer — `)
        );

        failed.forEach((feed, index) => {
            if (index > 0) {
                strip.appendChild(document.createTextNode(' · '));
            }
            strip.appendChild(make('strong', undefined, feed.name));
            strip.appendChild(
                document.createTextNode(feed.unauthorized ? ': sign-in required ' : `: ${feed.error ?? 'failed'} `)
            );
            if (feed.unauthorized) {
                const signIn = make('button', 'linklike', 'Sign in');
                signIn.addEventListener('click', () =>
                    post({ type: 'signIn', feedName: feed.name, feedUrl: feed.source })
                );
                strip.appendChild(signIn);
            }
        });
    }

    function fillSources(sources: NuGetMsg.PackageSource[]): void {
        state.sources = sources;
        renderSources();

        const select = el<HTMLSelectElement>('source');
        const previous = select.value;
        select.replaceChildren();

        const all = make('option', undefined, 'All sources') as HTMLOptionElement;
        all.value = '';
        select.appendChild(all);

        for (const source of sources) {
            const option = make(
                'option',
                undefined,
                source.isEnabled ? source.name : `${source.name} (disabled)`
            ) as HTMLOptionElement;
            option.value = source.name;
            option.disabled = !source.isEnabled;
            option.title = source.configFilePath
                ? `${source.source}\n${source.configFilePath}`
                : source.source;
            select.appendChild(option);
        }

        select.value = previous;
    }

    /**
     * The scope chip: a count, never a list.
     *
     * An empty scope is a valid, useful state now rather than something to be fixed before the
     * panel works — it means "wherever this package already is" for update and uninstall. The
     * chosen projects are on the tooltip; thirty of them spelled out next to the chip is how the
     * header turned into a paragraph.
     */
    /**
     * Paints the highlighter with the editor's own token colours.
     *
     * Custom properties rather than classes, because the stylesheet already names a fallback for
     * each one: a theme the host could not read simply leaves `--tok-kw` unset and the rule behind
     * it keeps working. The values are colours from a theme file, and they reach CSS through
     * `setProperty`, so nothing here can inject a declaration — an unparseable value is dropped.
     */
    function applyTokenColors(colors: Record<string, string>): void {
        for (const token of ['com', 'str', 'kw', 'typ', 'num', 'tag', 'attr']) {
            const value = colors[token];
            if (value) {
                document.documentElement.style.setProperty(`--tok-${token}`, value);
            } else {
                document.documentElement.style.removeProperty(`--tok-${token}`);
            }
        }
    }

    function setScope(scope: string[]): void {
        state.scope = scope;

        const button = el<HTMLButtonElement>('scope');
        button.textContent = scope.length === 0 ? 'All projects ▾' : `${describeProjects(scope)} ▾`;
        button.title =
            scope.length === 0
                ? 'Every project. Choose projects to narrow what the panel acts on.'
                : scope.map((path) => fileName(path)).join('\n');
        button.setAttribute(
            'aria-label',
            scope.length === 0 ? 'All projects' : `Projects: ${scope.map(fileName).join(', ')}`
        );

        if (focusedRow) {
            renderDetails(focusedRow, state.metadata[metadataKey(focusedRow.pkg)] ?? null);
        }
    }

    function restore(saved: NuGetMsg.SavedState): void {
        state.query = saved.query;
        state.pendingSelect = saved.selectedId;
        if (saved.splitPercent) {
            applySplit(saved.splitPercent);
        }
        el<HTMLInputElement>('query').value = saved.query;
        el<HTMLInputElement>('prerelease').checked = saved.prerelease;
        el<HTMLSelectElement>('version-lock').value = saved.versionLock;
        el<HTMLSelectElement>('source').value = saved.source;
        switchTab(saved.tab);
    }
}

NG.start();
