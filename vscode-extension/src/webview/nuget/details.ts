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
            section.appendChild(renderMarkdown(metadata.readmeMarkdown, state.settings.readme === 'plain'));
            details.appendChild(section);
        } else if (row.pkg.description || metadata?.description) {
            details.appendChild(make('p', 'd-description', metadata?.description ?? row.pkg.description ?? ''));
        }

        renderDependencies(details, row, metadata);
        renderTransitive(details, row);
        renderFacts(details, row, metadata);
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

        if (advisories.length > 0 || fromMetadata.length > 0) {
            const worst = Math.max(
                ...advisories.map((a) => a.severity),
                ...fromMetadata.map((v) => v.severity),
                0
            );
            const node = banner('error', `Known vulnerabilities — highest severity ${severityName(worst)}.`);

            const urls = new Set(
                [...advisories.map((a) => a.advisoryUrl), ...fromMetadata.map((v) => v.advisoryUrl)].filter(
                    (u): u is string => !!u
                )
            );
            for (const url of urls) {
                node.appendChild(document.createTextNode(' '));
                node.appendChild(link('advisory', url));
            }

            if (advisories.some((a) => a.isTransitive)) {
                node.appendChild(
                    make('div', 'muted', 'Reached through a transitive dependency, not a direct reference.')
                );
            }
            details.appendChild(node);
        }

        const deprecation =
            metadata?.deprecation ??
            deprecationFromAudit(row.pkg.id);

        if (deprecation) {
            const reasons = deprecation.reasons.length > 0 ? ` (${deprecation.reasons.join(', ')})` : '';
            const node = banner('warn', `This package is deprecated${reasons}.`);
            if (deprecation.message) {
                node.appendChild(make('div', undefined, deprecation.message));
            }
            if (deprecation.alternatePackageId) {
                const alternate = make('div', undefined, `Use ${deprecation.alternatePackageId} `);
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

        const install = make('button', 'action') as HTMLButtonElement;
        install.textContent = installLabel(row);
        install.disabled =
            state.scope.length === 0 || row.pkg.isGlobalPackageReference || known.length === 0;
        install.addEventListener('click', () =>
            post({
                type: 'install',
                id: row.pkg.id,
                version: versions.value,
                projectPaths: state.scope,
                requireLicenseAcceptance: metadata?.requireLicenseAcceptance ?? false,
                license: metadata?.licenseExpression ?? null,
            })
        );
        actions.appendChild(install);

        if (row.pkg.installedVersion) {
            const targets = state.scope.filter((path) =>
                row.projectPaths.some((p) => p.toLowerCase() === path.toLowerCase())
            );

            const remove = make('button', 'action secondary') as HTMLButtonElement;
            remove.textContent =
                targets.length === state.scope.length
                    ? `Uninstall from ${describeScope(targets)}`
                    : `Uninstall from ${targets.length} of ${state.scope.length} selected`;
            remove.disabled = targets.length === 0 || row.pkg.isGlobalPackageReference;
            remove.addEventListener('click', () =>
                post({ type: 'uninstall', id: row.pkg.id, projectPaths: targets })
            );
            actions.appendChild(remove);
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

        const projectFrameworks = new Set(
            state.projects
                .filter((p) => state.scope.some((s) => s.toLowerCase() === p.projectPath.toLowerCase()))
                .flatMap((p) => p.targetFrameworks.map((f) => f.toLowerCase()))
        );

        for (const group of metadata.dependencyGroups) {
            const node = make('details', 'd-group') as HTMLDetailsElement;
            // The group matching what the selected projects target is the one being asked about.
            node.open = projectFrameworks.has(group.targetFramework.toLowerCase());

            const summary = make('summary', undefined,
                `${group.targetFramework || 'any framework'} — ${group.dependencies.length} dependencies`);
            node.appendChild(summary);

            const list = make('ul', 'd-dep-list');
            for (const dependency of group.dependencies) {
                list.appendChild(make('li', undefined, `${dependency.id} ${dependency.versionRange}`));
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
            list.appendChild(make('li', undefined, `${package_.id} ${package_.version}`));
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

    export function installLabel(row: Row): string {
        const verb = row.pkg.installedVersion ? 'Update' : 'Install';
        return state.scope.length === 0
            ? `${verb} — choose a project first`
            : `${verb} into ${describeScope(state.scope)}`;
    }

    export function describeScope(paths: string[]): string {
        if (paths.length === 0) {
            return 'no project';
        }
        const names = paths.map((path) => fileName(path).replace(/\.[^.]+$/, ''));
        return names.length <= 3 ? names.join(', ') : `${names.length} projects`;
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
