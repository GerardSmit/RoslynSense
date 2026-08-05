/// <reference path="./list.ts" />

/**
 * The Updates tab: pick as many as you like, update them in one pass.
 *
 * The version lock is sent to the server rather than applied here, because it selects a *target
 * version* rather than hiding rows. Locked to the current major, 13.0.1 should be offered 13.0.3 —
 * filtering client-side would offer it nothing and call that a lock.
 */
namespace NG {
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

        if (byPackage.size === 0) {
            // The loop below never runs, so nothing would draw the "everything is up to date"
            // state and the tab would simply look broken.
            appendRows([]);
            onSelectionChanged();
            return;
        }

        for (const group of byPackage.values()) {
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
        }

        setCount('updates', byPackage.size);
        onSelectionChanged();
    }

    export function onSelectionChanged(): void {
        const selected = rows.filter((row) => row.check?.checked);
        const visible = rows.filter((row) => !row.li.hidden && row.check);

        const button = el<HTMLButtonElement>('update-selected');
        button.disabled = selected.length === 0;
        button.textContent =
            selected.length === 0 ? 'Update selected' : `Update selected (${selected.length})`;

        const all = el<HTMLInputElement>('select-all');
        all.checked = visible.length > 0 && selected.length === visible.length;
        all.indeterminate = selected.length > 0 && selected.length < visible.length;

        persist();
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
            const packages = rows
                .filter((row) => row.check?.checked && row.update)
                .map((row) => ({
                    id: row.pkg.id,
                    version: row.update!.latestVersion,
                    projectPaths: row.projectPaths,
                }));

            if (packages.length === 0) {
                return;
            }

            for (const row of rows) {
                if (row.check?.checked) {
                    row.li.classList.add('row-working');
                }
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

        const strip = el<HTMLElement>('summary');
        strip.replaceChildren();

        if (failures.length === 0) {
            strip.hidden = true;
            return;
        }

        // A wall of notification toasts for a fifty-package update helps nobody; one line above
        // the list, with the detail on the rows that failed, does.
        strip.hidden = false;
        strip.appendChild(
            banner(
                'warn',
                `${results.length - failures.length} updated, ${failures.length} failed — ` +
                    failures
                        .slice(0, 3)
                        .map((f) => `${f.id}: ${f.message}`)
                        .join('; ')
            )
        );
    }
}
