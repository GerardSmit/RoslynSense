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
     * select-all and the tab count must not see them. */
    let inducedRows: HTMLLIElement[] = [];
    let plannedInduced: NuGetMsg.InducedUpdate[] = [];
    let planTimer: number | undefined;
    /** The list generation the outstanding plan request was made against. A slow plan reply must
     * not decorate a list that has been rebuilt since. */
    let planListGen = -1;
    /** What the last Update click actually asked for, to tell selected from induced in the outcome. */
    let lastRequested = new Set<string>();

    export function showUpdates(updates: NuGetMsg.PackageUpdate[]): void {
        // One row per package; the projects it affects ride along, because the same package can be
        // behind by different amounts in different projects.
        const byPackage = new Map<string, NuGetMsg.PackageUpdate[]>();
        for (const update of updates) {
            const key = `${update.id}|${update.latestVersion}`;
            const existing = byPackage.get(key);
            if (existing) {
                existing.push(update);
            } else {
                byPackage.set(key, [update]);
            }
        }

        resetList();
        clearPlan();

        if (byPackage.size === 0) {
            // The loop below never runs, so nothing would draw the "everything is up to date"
            // state and the tab would simply look broken.
            appendRows([]);
            onSelectionChanged();
            return;
        }

        for (const [key, group] of byPackage) {
            const first = group[0];
            const summary: NuGetMsg.PackageSummary = {
                id: first.id,
                version: first.latestVersion,
                authors: null,
                description: `${first.currentVersion} → ${first.latestVersion} in ${group
                    .map((u) => u.projectName)
                    .join(', ')}`,
                downloads: null,
                iconUrl: null,
                deprecated: false,
                vulnerable: false,
                installedVersion: first.currentVersion,
                installedVersions: [...new Set(group.map((u) => u.currentVersion))],
                isCentrallyManaged: first.isCentrallyManaged,
                isGlobalPackageReference: first.isGlobalPackageReference,
                versionSource: first.versionSource,
                sourceName: null,
            };

            appendRows([summary]);
            const row = rows[rows.length - 1];
            row.severity = first.severity;
            row.update = first;
            row.projectPaths = group.map((u) => u.projectPath);
            decorateRow(row);

            if (row.check) {
                // Patch and minor moves are pre-checked; a major is a decision, so it waits for
                // one — its badge says why it starts unticked.
                row.check.checked = precheck(first.severity) && !unchecked.has(key.toLowerCase());
            }
        }

        setCount('updates', byPackage.size);
        onSelectionChanged();
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

        for (const li of inducedRows) {
            li.remove();
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

            const text = make('span', 'row-text');
            const title = make('span', 'row-title');
            title.appendChild(make('span', 'id', first.id));
            const badges = make('span', 'badges');
            badges.appendChild(make('span', 'badge', `${first.currentVersion} → ${first.version}`));
            title.appendChild(badges);
            text.appendChild(title);
            text.appendChild(
                make(
                    'span',
                    'muted row-meta',
                    `required by ${first.requiredBy} ${first.requiredByVersion} — ` +
                        [...new Set(group.map((item) => item.projectName))].join(', ')
                )
            );
            li.appendChild(text);

            // Under the row that asked for it, so cause and effect read top to bottom.
            const anchor = rows.find(
                (row) => row.pkg.id.toLowerCase() === first.requiredBy.toLowerCase()
            );
            if (anchor) {
                anchor.li.after(li);
            } else {
                el<HTMLUListElement>('list').appendChild(li);
            }
            inducedRows.push(li);
        }

        const note = el<HTMLElement>('plan-note');
        note.textContent =
            grouped.size === 0
                ? ''
                : `+ ${grouped.size} ${grouped.size === 1 ? 'dependency' : 'dependencies'} will move too`;

        updateButtonLabel();
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
            for (const li of inducedRows) {
                li.classList.add('row-working');
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
            // out is the kind of surprise the chip exists to prevent.
            projectPaths: state.scope,
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

        for (const li of inducedRows) {
            li.classList.remove('row-working');
            const failed = failures.filter((f) => f.id.toLowerCase() === li.dataset.pkgId);
            if (failed.length > 0) {
                li.classList.add('row-failed');
                li.title = failed.map((f) => `${fileName(f.projectPath)}: ${f.message}`).join('\n');
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
