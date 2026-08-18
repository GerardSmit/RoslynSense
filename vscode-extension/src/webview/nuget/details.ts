/// <reference path="./markdown.ts" />
/// <reference path="./list.ts" />

/**
 * The details pane: everything known about the selected package, and the buttons that act on it.
 *
 * Deprecation and vulnerability banners are the reason this pane was rebuilt. The old panel had
 * the markup for both, but nothing ever set the fields they read, so the two warnings that matter
 * most could never appear.
 */
namespace NG {
    export function requestDetails(row: Row): void {
        renderDetails(row, state.metadata[metadataKey(row.pkg)] ?? null);

        post({ type: 'versions', id: row.pkg.id, includePrerelease: el<HTMLInputElement>('prerelease').checked });
        post({
            type: 'metadata',
            gen: nextDetailsGen(),
            id: row.pkg.id,
            version: row.pkg.installedVersion ?? row.pkg.version ?? null,
            // Read from the feed the panel is scoped to, so the source and version list shown are
            // the ones the rest of the panel is talking about.
            source: selectedSource(),
        });

        if (state.settings.showTransitive && row.projectPaths.length > 0) {
            post({
                type: 'transitive',
                gen: detailsGen,
                projectPath: row.projectPaths[0],
                packageId: row.pkg.id,
            });
        }
    }

    export function metadataKey(pkg: NuGetMsg.PackageSummary): string {
        return `${pkg.id}/${pkg.installedVersion ?? pkg.version ?? ''}`;
    }

    export function renderDetails(row: Row, metadata: NuGetMsg.PackageMetadata | null): void {
        const details = el<HTMLElement>('details');
        details.replaceChildren();

        renderBanners(details, row, metadata);
        renderHeader(details, row, metadata);
        renderActions(details, row, metadata);

        if (metadata?.readmeMarkdown && state.settings.readme !== 'off') {
            const section = make('section', 'd-readme');
            section.appendChild(make('h3', undefined, 'Readme'));
            section.appendChild(
                renderMarkdown(
                    metadata.readmeMarkdown,
                    state.settings.readme === 'plain',
                    // Relative links and images in a README are written to be read inside the
                    // repository; the project URL is what makes them resolvable here.
                    metadata.projectUrl
                )
            );
            details.appendChild(section);
        } else if (row.pkg.description || metadata?.description) {
            details.appendChild(make('p', 'd-description', metadata?.description ?? row.pkg.description ?? ''));
        }

        renderDependencies(details, row, metadata);
        renderTransitive(details, row);
        renderFacts(details, row, metadata);
    }

    /** Advisory URLs, deduped — several projects hitting the same CVE is one advisory, not four. */
    function appendAdvisoryLinks(node: HTMLElement, urls: (string | null)[]): void {
        for (const url of new Set(urls.filter((u): u is string => !!u))) {
            node.appendChild(document.createTextNode(' '));
            node.appendChild(link('advisory', url));
        }
    }

    function renderBanners(
        details: HTMLElement,
        row: Row,
        metadata: NuGetMsg.PackageMetadata | null
    ): void {
        const advisories = (state.audit?.vulnerabilities ?? []).filter(
            (v) => v.id.toLowerCase() === row.pkg.id.toLowerCase()
        );
        const fromMetadata = metadata?.vulnerabilities ?? [];

        // The two sources are about two different versions, and saying so is the whole point. The
        // audit is the restore's verdict on what is installed; the metadata's advisories belong to
        // whichever version the dropdown is showing, which is usually the one you came here to
        // upgrade *to*. Merged into one line they read as "this package is unsafe either way".
        if (advisories.length > 0) {
            const installed = [...new Set(advisories.map((a) => a.version))].sort();
            const worst = Math.max(...advisories.map((a) => a.severity));
            const subject =
                installed.length === 1
                    ? `The installed version, ${installed[0]},`
                    : `The installed versions (${installed.join(', ')})`;

            const node = banner('error', '');
            node.appendChild(
                document.createTextNode(
                    `${subject} ${installed.length === 1 ? 'has' : 'have'} known vulnerabilities — ` +
                    `highest severity ${severityName(worst)}.`
                )
            );
            appendAdvisoryLinks(node, advisories.map((a) => a.advisoryUrl));

            if (advisories.some((a) => a.isTransitive)) {
                node.appendChild(
                    make('div', 'muted', 'Reached through a transitive dependency, not a direct reference.')
                );
            }
            details.appendChild(node);
        }

        if (fromMetadata.length > 0 && metadata) {
            const worst = Math.max(...fromMetadata.map((v) => v.severity));
            const node = banner('error', '');
            node.appendChild(
                document.createTextNode(
                    `Version ${metadata.version} has known vulnerabilities — ` +
                    `highest severity ${severityName(worst)}.`
                )
            );
            appendAdvisoryLinks(node, fromMetadata.map((v) => v.advisoryUrl));
            details.appendChild(node);
        }

        const deprecation =
            metadata?.deprecation ??
            deprecationFromAudit(row.pkg.id);

        if (deprecation) {
            const reasons = deprecation.reasons.length > 0 ? ` (${deprecation.reasons.join(', ')})` : '';
            const node = banner('warn', `This package is deprecated${reasons}.`);
            if (deprecation.message) {
                // These messages end in a URL more often than not — the .NET deprecation campaign
                // appends "you can learn more about it from <url>" to every one of them — and a
                // URL you cannot click is the one part of a warning that had a job to do.
                node.appendChild(make('div')).appendChild(linkify(deprecation.message));
            }
            if (deprecation.alternatePackageId) {
                const alternate = make('div', undefined, 'Use ');
                alternate.appendChild(packageLink(deprecation.alternatePackageId));
                alternate.appendChild(document.createTextNode(' '));
                alternate.appendChild(make('span', 'muted', deprecation.alternateVersionRange ?? ''));
                node.appendChild(alternate);
            }
            details.appendChild(node);
        }
    }

    function deprecationFromAudit(id: string): NuGetMsg.Deprecation | null {
        const entry = state.audit?.deprecations.find((d) => d.id.toLowerCase() === id.toLowerCase());
        return entry
            ? {
                  reasons: entry.reasons,
                  message: null,
                  alternatePackageId: entry.alternatePackageId,
                  alternateVersionRange: entry.alternateVersionRange,
              }
            : null;
    }

    function renderHeader(
        details: HTMLElement,
        row: Row,
        metadata: NuGetMsg.PackageMetadata | null
    ): void {
        const header = make('header', 'd-head');

        const icon = make('span', 'icon icon-lg');
        icon.setAttribute('aria-hidden', 'true');
        icon.appendChild(make('span', 'icon-fallback', (row.pkg.id[0] ?? '?').toUpperCase()));
        const img = make('img', 'icon-img') as HTMLImageElement;
        img.id = 'detail-icon';
        img.alt = '';
        img.dataset.key = iconKey(row.pkg);
        const cached = cachedIcon(row.pkg);
        if (cached) {
            img.src = cached;
        } else {
            img.hidden = true;
            requestIconFor(row.pkg);
        }
        icon.appendChild(img);
        header.appendChild(icon);

        const titles = make('div');
        titles.appendChild(make('h2', 'd-id', metadata?.title || row.pkg.id));
        if (metadata?.title && metadata.title !== row.pkg.id) {
            titles.appendChild(make('div', 'muted', row.pkg.id));
        }
        titles.appendChild(
            make(
                'div',
                'muted',
                [
                    metadata?.authors ?? row.pkg.authors,
                    formatCount(metadata?.downloads ?? row.pkg.downloads),
                    formatDate(metadata?.published ?? null),
                    metadata?.prefixReserved ? 'prefix reserved' : null,
                ]
                    .filter(Boolean)
                    .join(' · ')
            )
        );
        header.appendChild(titles);
        details.appendChild(header);
    }

    function renderActions(
        details: HTMLElement,
        row: Row,
        metadata: NuGetMsg.PackageMetadata | null
    ): void {
        const actions = make('div', 'd-actions');

        const versions = make('select') as HTMLSelectElement;
        versions.id = 'd-version';
        versions.setAttribute('aria-label', 'Version');
        // An empty stored array is not nullish, so `??` alone would leave the dropdown with no
        // options and Install would post an empty version.
        const known = pick(state.versions[row.pkg.id], metadata?.allVersions, [row.pkg.version]);
        for (const version of known) {
            const option = make('option', undefined, version) as HTMLOptionElement;
            option.value = version;
            versions.appendChild(option);
        }
        if (state.selectedVersion && known.includes(state.selectedVersion)) {
            versions.value = state.selectedVersion;
        }
        versions.addEventListener('change', () => {
            state.selectedVersion = versions.value;
        });
        actions.appendChild(versions);

        const targets = targetsFor(row);

        const primary = make('button', 'action') as HTMLButtonElement;
        primary.disabled = row.pkg.isGlobalPackageReference || known.length === 0;

        if (row.pkg.installedVersion) {
            // An installed package moves version; it is never re-added. `install` shells
            // `dotnet add package`, which would write a reference into every targeted project
            // whether or not it had one — the same reason the Installed tab's row button goes
            // through updateAll (see buildRowUpdate).
            primary.textContent =
                targets.paths.length === 0
                    ? 'Update — not referenced by the selected projects'
                    : `Update in ${describeProjects(targets.paths)}`;
            primary.disabled = primary.disabled || targets.paths.length === 0;
            primary.addEventListener('click', () =>
                post({
                    type: 'updateAll',
                    packages: [{ id: row.pkg.id, version: versions.value, projectPaths: targets.paths }],
                    versionLock: el<HTMLSelectElement>('version-lock').value as NuGetMsg.Lock,
                    includePrerelease: el<HTMLInputElement>('prerelease').checked,
                })
            );
        } else {
            // Install is the one action with nothing to infer from: there is no existing set of
            // projects, and "every project in the solution" is how a repository ends up with a
            // reference in places nobody meant to put one.
            primary.textContent =
                state.scope.length === 0
                    ? 'Install — choose a project first'
                    : `Install into ${describeProjects(state.scope)}`;
            primary.disabled = primary.disabled || state.scope.length === 0;
            primary.addEventListener('click', () =>
                post({
                    type: 'install',
                    id: row.pkg.id,
                    version: versions.value,
                    projectPaths: state.scope,
                    requireLicenseAcceptance: metadata?.requireLicenseAcceptance ?? false,
                    license: metadata?.licenseExpression ?? null,
                })
            );
        }
        actions.appendChild(primary);

        if (row.pkg.installedVersion) {
            const remove = make('button', 'action secondary') as HTMLButtonElement;
            remove.textContent = `Uninstall from ${describeProjects(targets.paths)}`;
            remove.title = targets.inferred
                ? 'Every project that references this package. Choose projects above to narrow it.'
                : targets.paths.join('\n');
            remove.disabled = targets.paths.length === 0 || row.pkg.isGlobalPackageReference;
            remove.addEventListener('click', () =>
                post({
                    type: 'uninstall',
                    id: row.pkg.id,
                    projectPaths: targets.paths,
                    // Nothing was chosen, so nothing was named: the host asks before editing
                    // several project files the user never pointed at.
                    confirmAll: targets.inferred,
                })
            );
            actions.appendChild(remove);
        }

        if (row.pkg.installedVersions.length > 1) {
            // What the Consolidate tab used to do, next to the package that needs it: every
            // project that references this package moves to the chosen version, solution-wide.
            const consolidate = make('button', 'action secondary') as HTMLButtonElement;
            consolidate.textContent = `Consolidate to ${versions.value}`;
            consolidate.title =
                'Projects reference this package at different versions ' +
                `(${row.pkg.installedVersions.join(', ')}). ` +
                'Set every one of them to the selected version.';
            consolidate.disabled = row.pkg.isGlobalPackageReference;
            consolidate.addEventListener('click', () =>
                post({ type: 'consolidate', id: row.pkg.id, version: versions.value })
            );
            versions.addEventListener('change', () => {
                consolidate.textContent = `Consolidate to ${versions.value}`;
            });
            actions.appendChild(consolidate);
        }

        details.appendChild(actions);

        if (row.pkg.isGlobalPackageReference) {
            details.appendChild(
                make(
                    'p',
                    'muted',
                    'This is a GlobalPackageReference: it applies to every project in the repository, ' +
                        'so it cannot be installed or removed per project.'
                )
            );
        }

        if (row.pkg.isCentrallyManaged && row.pkg.versionSource) {
            const note = make('p', 'muted', 'Version managed centrally in ');
            const file = make('span', 'md-link', fileName(row.pkg.versionSource));
            file.setAttribute('role', 'link');
            file.tabIndex = 0;
            file.title = row.pkg.versionSource;
            file.addEventListener('click', () =>
                post({ type: 'openFile', path: row.pkg.versionSource! })
            );
            note.appendChild(file);
            details.appendChild(note);
        }
    }

    function renderDependencies(
        details: HTMLElement,
        row: Row,
        metadata: NuGetMsg.PackageMetadata | null
    ): void {
        if (!metadata || metadata.dependencyGroups.length === 0) {
            return;
        }

        const section = make('section', 'd-deps');
        section.appendChild(make('h3', undefined, 'Dependencies'));

        // The projects being acted on, which with an empty scope are the ones already referencing
        // the package rather than none of them — otherwise no group would open in the common case.
        const targets = targetsFor(row).paths;
        const projectFrameworks = new Set(
            state.projects
                .filter((p) => targets.some((t) => t.toLowerCase() === p.projectPath.toLowerCase()))
                .flatMap((p) => p.targetFrameworks.map((f) => f.toLowerCase()))
        );

        for (const group of metadata.dependencyGroups) {
            const node = make('details', 'd-group') as HTMLDetailsElement;
            // The group matching what the selected projects target is the one being asked about.
            node.open = projectFrameworks.has(group.targetFramework.toLowerCase());

            const summary = make('summary', undefined,
                `${group.targetFramework || 'any framework'} — ${group.dependencies.length} ` +
                `${group.dependencies.length === 1 ? 'dependency' : 'dependencies'}`);
            node.appendChild(summary);

            const list = make('ul', 'd-dep-list');
            for (const dependency of group.dependencies) {
                const item = make('li');
                item.appendChild(packageLink(dependency.id));
                item.appendChild(make('span', 'muted', ` ${dependency.versionRange}`));
                list.appendChild(item);
            }
            node.appendChild(list);
            section.appendChild(node);
        }

        details.appendChild(section);
    }

    export function renderTransitive(details: HTMLElement, row: Row): void {
        const packages = transitiveFor(row.pkg.id);
        if (packages.length === 0) {
            return;
        }

        const section = make('section', 'd-transitive');
        section.appendChild(make('h3', undefined, 'Brings in'));

        const list = make('ul', 'd-dep-list');
        for (const package_ of packages) {
            const item = make('li');
            item.appendChild(packageLink(package_.id));
            item.appendChild(make('span', 'muted', ` ${package_.version}`));
            list.appendChild(item);
        }
        section.appendChild(list);
        details.appendChild(section);
    }

    const transitive: Record<string, NuGetMsg.TransitivePackage[]> = {};

    export function setTransitive(id: string, packages: NuGetMsg.TransitivePackage[]): void {
        transitive[id.toLowerCase()] = packages;
    }

    function transitiveFor(id: string): NuGetMsg.TransitivePackage[] {
        return transitive[id.toLowerCase()] ?? [];
    }

    function renderFacts(
        details: HTMLElement,
        row: Row,
        metadata: NuGetMsg.PackageMetadata | null
    ): void {
        const section = make('section', 'd-meta');
        section.appendChild(make('h3', undefined, 'Details'));

        const list = make('dl');
        const add = (label: string, value: Node | string | null) => {
            if (!value) {
                return;
            }
            list.appendChild(make('dt', undefined, label));
            const dd = make('dd');
            if (typeof value === 'string') {
                dd.textContent = value;
            } else {
                dd.appendChild(value);
            }
            list.appendChild(dd);
        };

        const license =
            metadata?.licenseExpression ??
            (metadata?.licenseFileText ? 'in the package' : null) ??
            (metadata?.licenseUrl ? 'see link' : null);

        add('License', license);
        if (metadata?.licenseUrl) {
            add('License link', link(metadata.licenseUrl, metadata.licenseUrl));
        }
        add('Owners', metadata?.owners ?? null);
        add('Tags', metadata?.tags ?? null);
        add('Source feed', metadata?.sourceName ?? row.pkg.sourceName);
        if (metadata?.projectUrl) {
            add('Project', link(metadata.projectUrl, metadata.projectUrl));
        }
        if (metadata?.packageDetailsUrl) {
            add('On the feed', link(metadata.packageDetailsUrl, metadata.packageDetailsUrl));
        }
        if (metadata?.reportAbuseUrl) {
            add('Report abuse', link(metadata.reportAbuseUrl, metadata.reportAbuseUrl));
        }

        if (metadata?.licenseFileText) {
            const licenseDetails = make('details', 'd-license') as HTMLDetailsElement;
            licenseDetails.appendChild(make('summary', undefined, 'License text'));
            licenseDetails.appendChild(make('pre', 'md-raw', metadata.licenseFileText));
            section.appendChild(licenseDetails);
        }

        if (list.childElementCount > 0) {
            section.insertBefore(list, section.children[1] ?? null);
        }
        details.appendChild(section);
    }

    /**
     * The projects an action applies to.
     *
     * With projects chosen, the selection wins, narrowed to the ones that actually reference the
     * package. With nothing chosen, update and uninstall mean "wherever this package already is" —
     * which is the set they were always going to operate on, and which used to have to be
     * hand-picked one project at a time.
     */
    export function targetsFor(row: Row): { paths: string[]; inferred: boolean } {
        if (state.scope.length === 0) {
            return { paths: row.projectPaths, inferred: true };
        }

        return {
            paths: state.scope.filter((path) =>
                row.projectPaths.some((p) => p.toLowerCase() === path.toLowerCase())
            ),
            inferred: false,
        };
    }

    /**
     * A count, never a list. Spelling out thirty project names is how the header used to become a
     * paragraph; the names live on the tooltip of whatever this labels.
     */
    export function describeProjects(paths: string[]): string {
        if (paths.length === 0) {
            return 'no project';
        }
        if (paths.length === 1) {
            return fileName(paths[0]).replace(/\.[^.]+$/, '');
        }
        return `${paths.length} projects`;
    }

    export function fileName(path: string): string {
        const parts = path.split(/[\\/]/);
        return parts[parts.length - 1] || path;
    }

    /** The first list that actually has something in it. */
    function pick(...candidates: (string[] | undefined)[]): string[] {
        return candidates.find((c): c is string[] => !!c && c.length > 0) ?? [];
    }
}
