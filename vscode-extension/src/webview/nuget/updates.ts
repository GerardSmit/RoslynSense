/// <reference path="./list.ts" />

/**
 * The Updates tab: everything safe is pre-checked, one button applies the lot.
 *
 * The version lock is sent to the server rather than applied here, because it selects a *target
 * version* rather than hiding rows. Locked to the current major, 13.0.1 should be offered 13.0.3 —
 * filtering client-side would offer it nothing and call that a lock.
 *
 * Every selection change re-plans the dependency closure, and the packages the selection drags
 * along render inline as indented rows. That preview replaces the old confirmation modal: what a
 * click will do is on screen before the click, not behind it.
 */
namespace NG {
    /**
     * Rows the user unticked, keyed `id|latestVersion`, kept for the webview's lifetime so a
     * refresh does not silently re-arm an update they opted out of. Keying on the version means a
     * genuinely new latest release re-checks on purpose.
     */
    const unchecked = new Set<string>();

    /** The induced-update rows currently in the DOM. Never part of `rows` — keyboard navigation,
     * select-all and the tab count must not see them. They do go into `secondaryRows`, which is
     * only about icons. */
    let inducedRows: Row[] = [];
    let plannedInduced: NuGetMsg.InducedUpdate[] = [];
    let planTimer: number | undefined;
    /** The list generation the outstanding plan request was made against. A slow plan reply must
     * not decorate a list that has been rebuilt since. */
    let planListGen = -1;
    /** What the last Update click actually asked for, to tell selected from induced in the outcome. */
    let lastRequested = new Set<string>();

    /**
     * The grouped updates behind the rows, keyed `id|latestVersion` lowercase.
     *
     * Populated before the rows are built rather than patched onto them afterwards, so that
     * `buildRow` can finish an Updates row in one pass. Building them one at a time meant one
     * `appendRows` call — and so one full filter and empty-state pass — per package.
     */
    const groups = new Map<string, NuGetMsg.PackageUpdate[]>();

    export function updateGroupFor(pkg: NuGetMsg.PackageSummary): NuGetMsg.PackageUpdate[] {
        return groups.get(`${pkg.id}|${pkg.version}`.toLowerCase()) ?? [];
    }

    /** Whether this row starts ticked: patch and minor are safe, a major is a decision. */
    export function preselected(group: NuGetMsg.PackageUpdate[]): boolean {
        const key = `${group[0].id}|${group[0].latestVersion}`.toLowerCase();
        return precheck(group[0].severity) && !unchecked.has(key);
    }

    export function showUpdates(updates: NuGetMsg.PackageUpdate[]): void {
        // One row per package; the projects it affects ride along, because the same package can be
        // behind by different amounts in different projects.
        groups.clear();
        for (const update of updates) {
            const key = `${update.id}|${update.latestVersion}`.toLowerCase();
            const existing = groups.get(key);
            if (existing) {
                existing.push(update);
            } else {
                groups.set(key, [update]);
            }
        }

        resetList();
        clearPlan();

        // Empty is a real state with its own message, so it goes through appendRows like any other.
        // The selection is settled from there once the last chunk lands, not here: with more than
        // a hundred updates the later rows do not exist yet at this point.
        appendRows([...groups.values()].map(summaryOf));
        setCount('updates', groups.size);
    }

    /**
     * The row text for one grouped update: which versions move, and where to.
     *
     * Only the versions. The project names used to be spelled out here, which on a solution-wide
     * update meant every row ended in a list of thirty names — the part of the line that overflows
     * first and helps least. They are on the row's tooltip instead.
     */
    function summaryOf(group: NuGetMsg.PackageUpdate[]): NuGetMsg.PackageSummary {
        const first = group[0];
        const currents = [...new Set(group.map((u) => u.currentVersion))];

        return {
            id: first.id,
            version: first.latestVersion,
            authors: null,
            description: `${describeVersions(currents)} → ${first.latestVersion}`,
            downloads: null,
            iconUrl: null,
            deprecated: false,
            vulnerable: false,
            installedVersion: first.currentVersion,
            installedVersions: currents,
            isCentrallyManaged: first.isCentrallyManaged,
            isGlobalPackageReference: first.isGlobalPackageReference,
            versionSource: first.versionSource,
            sourceName: null,
        };
    }

    /** One version reads as itself; several read as the set they are. */
    export function describeVersions(versions: string[]): string {
        return versions.length === 1 ? versions[0] : `[${versions.join(', ')}]`;
    }

    function precheck(severity: NuGetMsg.Severity): boolean {
        return severity === 'patch' || severity === 'minor' || severity === 'none';
    }

    export function onSelectionChanged(): void {
        // Remember opt-outs by content, not by row: the rows are rebuilt wholesale on every
        // refresh, and only this set carries the user's unticks across that.
        for (const row of rows) {
            if (row.check && row.update) {
                const key = `${row.pkg.id}|${row.update.latestVersion}`.toLowerCase();
                if (row.check.checked) {
                    unchecked.delete(key);
                } else {
                    unchecked.add(key);
                }
            }
        }

        const selected = rows.filter((row) => row.check?.checked);
        const visible = rows.filter((row) => !row.li.hidden && row.check);

        const all = el<HTMLInputElement>('select-all');
        all.checked = visible.length > 0 && selected.length === visible.length;
        all.indeterminate = selected.length > 0 && selected.length < visible.length;

        schedulePlan();
        updateButtonLabel();
        persist();
    }

    /**
     * Re-plans the dependency closure for the current selection, debounced so ticking five boxes
     * costs one request. The reply is dropped unless both the plan generation and the list
     * generation still match — a stale plan repainting a fresh list is worse than no plan.
     */
    function schedulePlan(): void {
        window.clearTimeout(planTimer);
        planTimer = window.setTimeout(() => {
            const packages = selectedItems();
            if (packages.length === 0) {
                showPlan([]);
                return;
            }

            planListGen = listGen;
            post({
                type: 'updatePlan',
                gen: nextPlanGen(),
                packages,
                versionLock: el<HTMLSelectElement>('version-lock').value as NuGetMsg.Lock,
                includePrerelease: el<HTMLInputElement>('prerelease').checked,
            });
        }, 400);
    }

    function selectedItems(): { id: string; version: string; projectPaths: string[] }[] {
        return rows
            .filter((row) => row.check?.checked && row.update)
            .map((row) => ({
                id: row.pkg.id,
                version: row.update!.latestVersion,
                projectPaths: row.projectPaths,
            }));
    }

    /** Renders the induced updates inline, each under the row that drags it along. */
    export function showPlan(induced: NuGetMsg.InducedUpdate[]): void {
        if (induced.length > 0 && planListGen !== listGen) {
            return;
        }

        for (const row of inducedRows) {
            row.li.remove();
            const at = secondaryRows.indexOf(row);
            if (at >= 0) {
                secondaryRows.splice(at, 1);
            }
        }
        inducedRows = [];
        plannedInduced = induced;

        // One row per (package, version); the same package can be induced in several projects.
        const grouped = new Map<string, NuGetMsg.InducedUpdate[]>();
        for (const item of induced) {
            const key = `${item.id}|${item.version}`.toLowerCase();
            const existing = grouped.get(key);
            if (existing) {
                existing.push(item);
            } else {
                grouped.set(key, [item]);
            }
        }

        for (const group of grouped.values()) {
            const first = group[0];
            const li = make('li', 'row row-induced');
            li.setAttribute('role', 'presentation');
            li.dataset.pkgId = first.id.toLowerCase();
            li.title = [...new Set(group.map((item) => item.projectName))].join(', ');

            // An induced row is a package like any other: it gets the same icon slot, so the list
            // does not go ragged where the dependencies are, and the same click-through, because
            // "what is this thing you are about to move?" is exactly the question it raises.
            const icon = make('span', 'icon');
            icon.setAttribute('aria-hidden', 'true');
            const fallback = make('span', 'icon-fallback', (first.id[0] ?? '?').toUpperCase());
            const img = make('img', 'icon-img') as HTMLImageElement;
            img.alt = '';
            img.hidden = true;
            img.decoding = 'async';
            icon.appendChild(fallback);
            icon.appendChild(img);
            li.appendChild(icon);

            const text = make('span', 'row-text');
            const title = make('span', 'row-title');
            title.appendChild(packageLink(first.id));
            const badges = make('span', 'badges');
            badges.appendChild(make('span', 'badge', `${first.currentVersion} → ${first.version}`));
            title.appendChild(badges);
            text.appendChild(title);
            text.appendChild(
                make(
                    'span',
                    'muted row-meta',
                    `required by ${first.requiredBy} ${first.requiredByVersion}`
                )
            );
            li.appendChild(text);
            li.addEventListener('click', () => openPackage(first.id));

            // Under the row that asked for it, so cause and effect read top to bottom.
            const anchor = rows.find(
                (row) => row.pkg.id.toLowerCase() === first.requiredBy.toLowerCase()
            );
            if (anchor) {
                anchor.li.after(li);
            } else {
                el<HTMLUListElement>('list').appendChild(li);
            }

            const row: Row = {
                pkg: inducedSummary(first),
                li,
                iconImg: img,
                iconFallback: fallback,
                badges,
                projectPaths: group.map((item) => item.projectPath),
            };
            inducedRows.push(row);
            secondaryRows.push(row);
            attachIcon(row);
        }

        const note = el<HTMLElement>('plan-note');
        note.textContent =
            grouped.size === 0
                ? ''
                : `+ ${grouped.size} ${grouped.size === 1 ? 'dependency' : 'dependencies'} will move too`;

        updateButtonLabel();
    }

    /** Just enough of a summary for the icon plumbing, which keys on id and iconUrl. */
    function inducedSummary(item: NuGetMsg.InducedUpdate): NuGetMsg.PackageSummary {
        return {
            id: item.id,
            version: item.version,
            authors: null,
            description: null,
            downloads: null,
            iconUrl: null,
            deprecated: false,
            vulnerable: false,
            installedVersion: item.currentVersion,
            installedVersions: [item.currentVersion],
            isCentrallyManaged: false,
            isGlobalPackageReference: false,
            versionSource: null,
            sourceName: null,
        };
    }

    function clearPlan(): void {
        window.clearTimeout(planTimer);
        inducedRows = [];
        plannedInduced = [];
        planListGen = -1;
        el<HTMLElement>('plan-note').textContent = '';
    }

    function updateButtonLabel(): void {
        const selected = rows.filter((row) => row.check?.checked).length;
        const induced = new Set(
            plannedInduced.map((item) => `${item.id}|${item.version}`.toLowerCase())
        ).size;

        const button = el<HTMLButtonElement>('update-selected');
        button.disabled = selected === 0;
        button.textContent =
            selected === 0
                ? 'Update'
                : induced > 0
                  ? `Update ${selected} (+${induced} ${induced === 1 ? 'dependency' : 'dependencies'})`
                  : `Update ${selected}`;
    }

    export function wireUpdates(): void {
        el<HTMLInputElement>('select-all').addEventListener('change', (event) => {
            const checked = (event.target as HTMLInputElement).checked;
            for (const row of rows) {
                if (row.check && !row.li.hidden) {
                    row.check.checked = checked;
                }
            }
            onSelectionChanged();
        });

        el<HTMLSelectElement>('version-lock').addEventListener('change', () => {
            if (state.tab === 'updates') {
                requestUpdates();
            }
        });

        el<HTMLButtonElement>('update-selected').addEventListener('click', () => {
            const packages = selectedItems();
            if (packages.length === 0) {
                return;
            }

            lastRequested = new Set(packages.map((item) => item.id.toLowerCase()));

            for (const row of rows) {
                if (row.check?.checked) {
                    row.li.classList.add('row-working');
                }
            }
            for (const row of inducedRows) {
                row.li.classList.add('row-working');
            }

            el<HTMLButtonElement>('update-selected').disabled = true;
            post({
                type: 'updateAll',
                packages,
                // The lock travels with the request: it decides how far an induced bump may go,
                // and the host has no view of this control.
                versionLock: el<HTMLSelectElement>('version-lock').value as NuGetMsg.Lock,
                includePrerelease: el<HTMLInputElement>('prerelease').checked,
            });
        });
    }

    export function requestUpdates(gen?: number): void {
        post({
            type: 'updates',
            gen: gen ?? nextListGen(),
            includePrerelease: el<HTMLInputElement>('prerelease').checked,
            versionLock: el<HTMLSelectElement>('version-lock').value as NuGetMsg.Lock,
            // The scope chip narrows this tab like every other: updating a project you filtered
            // out is the kind of surprise the chip exists to prevent. An empty scope is every
            // project, which is what the chip now says when nothing is chosen.
            projectPaths: state.scope,
            // The server answers as though this were the only feed configured, so a package the
            // feed does not carry is absent rather than offered a version from somewhere else.
            source: selectedSource(),
        });
    }

    export function showUpdateOutcomes(results: NuGetMsg.UpdateOutcome[]): void {
        const failures = results.filter((r) => !r.success);

        for (const row of rows) {
            row.li.classList.remove('row-working');

            const failed = failures.filter((f) => f.id.toLowerCase() === row.pkg.id.toLowerCase());
            if (failed.length > 0) {
                row.li.classList.add('row-failed');
                row.li.title = failed.map((f) => `${fileName(f.projectPath)}: ${f.message}`).join('\n');
            }
        }

        for (const row of inducedRows) {
            row.li.classList.remove('row-working');
            const failed = failures.filter((f) => f.id.toLowerCase() === row.li.dataset.pkgId);
            if (failed.length > 0) {
                row.li.classList.add('row-failed');
                row.li.title = failed
                    .map((f) => `${fileName(f.projectPath)}: ${f.message}`)
                    .join('\n');
            }
        }

        const strip = el<HTMLElement>('summary');
        strip.replaceChildren();
        strip.hidden = false;

        const updatedIds = new Set(
            results.filter((r) => r.success).map((r) => r.id.toLowerCase())
        );
        const pulledIn = [...updatedIds].filter((id) => !lastRequested.has(id)).length;
        const suffix = pulledIn > 0 ? ` (${pulledIn} pulled in by dependencies)` : '';

        if (failures.length === 0) {
            // No modal confirmed this run, so the receipt does: what moved, and how much of it
            // was the dependency closure rather than the selection.
            strip.appendChild(
                banner(
                    'info',
                    `${updatedIds.size} package${updatedIds.size === 1 ? '' : 's'} updated${suffix}.`
                )
            );
            return;
        }

        // A wall of notification toasts for a fifty-package update helps nobody; one line above
        // the list, with the detail on the rows that failed, does.
        strip.appendChild(
            banner(
                'warn',
                `${updatedIds.size} updated${suffix}, ${failures.length} failed — ` +
                    failures
                        .slice(0, 3)
                        .map((f) => `${f.id}: ${f.message}`)
                        .join('; ')
            )
        );
    }
}
