/// <reference path="./details.ts" />
/// <reference path="./updates.ts" />
/// <reference path="./sources.ts" />
/// <reference path="./splitter.ts" />

/** Boot, tab switching and the message loop. */
namespace NG {
    export function start(): void {
        // The message loop goes on first and each step is isolated. Wiring a control that is not
        // in the document throws, and when that happened inside one long start() it took out
        // everything after it — including the listener that receives boot, so the panel came up
        // completely empty with no error anywhere.
        wireMessages();

        for (const step of [wireHeader, wireListKeyboard, wireUpdates, wireSources, wireSplitter]) {
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
            debounce = window.setTimeout(() => {
                state.query = query.value;
                switchTab('browse');
            }, 300);
        });

        el<HTMLInputElement>('prerelease').addEventListener('change', () => refresh());
        el<HTMLSelectElement>('source').addEventListener('change', () => {
            if (state.tab === 'browse') {
                refresh();
            }
        });

        el<HTMLButtonElement>('scope').addEventListener('click', () => post({ type: 'pickScope' }));

        for (const button of document.querySelectorAll<HTMLButtonElement>('nav button')) {
            button.addEventListener('click', () => switchTab(button.dataset.tab as NuGetMsg.Tab));
        }

        document.addEventListener('keydown', (event) => {
            if (event.key === '/' && document.activeElement !== query) {
                event.preventDefault();
                query.focus();
            } else if (event.key === 'Escape' && document.activeElement === query) {
                query.value = '';
                state.query = '';
                switchTab('browse');
            }
        });
    }

    export function switchTab(tab: NuGetMsg.Tab): void {
        state.tab = tab;

        for (const button of document.querySelectorAll<HTMLButtonElement>('nav button')) {
            button.setAttribute('aria-selected', String(button.dataset.tab === tab));
        }

        el<HTMLElement>('updates-toolbar').hidden = tab !== 'updates';
        el<HTMLElement>('summary').hidden = true;

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
                post({ type: 'installed', gen });
                post({ type: 'audit', gen, refresh: false });
                break;
            case 'updates':
                requestUpdates();
                post({ type: 'audit', gen: listGen, refresh: false });
                break;
            case 'consolidate':
                post({ type: 'consolidations', gen });
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
                            setRows(mergeInstalled(message.projects));
                        }
                        break;

                    case 'updates':
                        if (message.gen !== listGen) {
                            return;
                        }
                        showFeeds(message.feeds);
                        showUpdates(message.updates);
                        break;

                    case 'consolidations':
                        if (message.gen !== listGen) {
                            return;
                        }
                        showConsolidations(message.results);
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

    function showConsolidations(consolidations: NuGetMsg.Consolidation[]): void {
        setRows(
            consolidations.map((consolidation) => ({
                id: consolidation.id,
                version: consolidation.versions[0]?.version ?? '',
                authors: null,
                description: consolidation.versions
                    .map((v) => `${v.projectName}: ${v.version}`)
                    .join(' · '),
                downloads: null,
                iconUrl: null,
                deprecated: false,
                vulnerable: false,
                installedVersion: consolidation.versions[0]?.version ?? null,
                installedVersions: [...new Set(consolidation.versions.map((v) => v.version))],
                isCentrallyManaged: false,
                isGlobalPackageReference: false,
                versionSource: null,
                sourceName: null,
            }))
        );
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

    function setScope(scope: string[]): void {
        state.scope = scope;

        const button = el<HTMLButtonElement>('scope');
        button.textContent = scope.length === 0 ? 'Choose projects…' : `${describeScope(scope)} ▾`;
        button.classList.toggle('warn', scope.length > 3);

        const summary = el<HTMLElement>('scope-summary');
        summary.textContent =
            scope.length === 0
                ? 'Choose at least one project to install into.'
                : scope.map((path) => fileName(path)).join(', ');

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
        el<HTMLSelectElement>('dependency-mode').value = saved.dependencyMode ?? 'selectedOnly';
        el<HTMLSelectElement>('source').value = saved.source;
        switchTab(saved.tab);
    }
}

NG.start();
