import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { forgetCredential } from './credentials';
import { html } from './html';
import { pickScope, rememberScope, savedScope } from './scope';

/**
 * The extension-host half of the panel: it owns the LSP calls, anything modal, and every decision
 * the webview is not allowed to make on its own — opening a URL, accepting a licence, choosing
 * which projects an install writes to.
 */
export function wire(
    context: vscode.ExtensionContext,
    panel: vscode.WebviewPanel,
    getClient: () => LanguageClient | undefined,
    onDispose: () => void,
    /** Replayed after boot: posting it before the webview attaches its listener drops it. */
    pendingScope?: { projectPaths: string[]; selectPackage: string | null },
    pendingTab?: NuGetMsg.Tab
): void {
    panel.webview.html = html(panel.webview, context.extensionUri);
    panel.onDidDispose(onDispose, null, context.subscriptions);

    let projects: NuGetMsg.ProjectRef[] = [];
    let scope: string[] = [];

    const post = (message: NuGetMsg.ToView) => void panel.webview.postMessage(message);

    // A count, not a flag: the handler is async, so two requests overlap and the first to finish
    // would otherwise stop the progress bar while the second is still running.
    let inFlight = 0;
    const busy = (delta: number) => {
        inFlight = Math.max(0, inFlight + delta);
        post({ type: 'busy', busy: inFlight > 0 });
    };

    /** Balances the count for one handler, whether it returned or threw. */
    const settle = (started: boolean) => {
        if (started) {
            busy(-1);
        }
    };

    const fail = (error: unknown, target: 'list' | 'details' = 'list') =>
        post({
            type: 'error',
            message: error instanceof Error ? error.message : String(error),
            scope: target,
        });

    panel.webview.onDidReceiveMessage(
        async (message: NuGetMsg.ToHost) => {
            const client = getClient();
            if (!client) {
                fail(new Error('RoslynSense is not running.'));
                // The webview marks an icon request pending before posting, so a dropped reply
                // would leave that row on its fallback glyph forever.
                if (message.type === 'icon') {
                    post({ type: 'icon', id: message.id, key: message.iconUrl ?? `embedded:${message.id}`, dataUri: null });
                }
                return;
            }

            let started = false;
            const start = () => {
                started = true;
                busy(+1);
            };

            try {
                switch (message.type) {
                    case 'ready': {
                        // Boot is two round trips against a workspace that may still be loading,
                        // and it was the one request that ran without the progress bar.
                        start();
                        const installed = await client.sendRequest<NuGetMsg.ProjectPackages[]>(
                            'roslynSense/nuget/installed',
                            {}
                        );
                        projects = installed.map((p) => ({
                            projectPath: p.projectPath,
                            projectName: p.projectName,
                            targetFrameworks: p.targetFrameworks,
                        }));
                        scope = savedScope(context, projects);

                        const sources = await client
                            .sendRequest<NuGetMsg.PackageSource[]>('roslynSense/nuget/sources', {})
                            .catch(() => [] as NuGetMsg.PackageSource[]);

                        // Opening from a project node beats both the remembered scope and the
                        // saved state — it is the more specific instruction, and it arrived first.
                        if (pendingScope) {
                            scope = pendingScope.projectPaths;
                            rememberScope(context, scope);
                        }

                        post({
                            type: 'boot',
                            scope,
                            projects,
                            sources,
                            settings: settings(),
                            state: pendingScope || pendingTab ? null : message.state,
                        });
                        post({ type: 'projects', gen: 0, projects: installed });

                        if (pendingScope) {
                            post({
                                type: 'scope',
                                projectPaths: pendingScope.projectPaths,
                                selectPackage: pendingScope.selectPackage,
                            });
                            pendingScope = undefined;
                        }
                        if (pendingTab) {
                            post({ type: 'goToTab', tab: pendingTab });
                            pendingTab = undefined;
                        }
                        settle(started);
                        break;
                    }

                    case 'installed': {
                        start();
                        const installed = await client.sendRequest<NuGetMsg.ProjectPackages[]>(
                            'roslynSense/nuget/installed',
                            {}
                        );
                        projects = installed.map((p) => ({
                            projectPath: p.projectPath,
                            projectName: p.projectName,
                            targetFrameworks: p.targetFrameworks,
                        }));
                        post({ type: 'projects', gen: message.gen, projects: installed });
                        settle(started);
                        break;
                    }

                    case 'search': {
                        start();
                        const take = settings().pageSize;
                        const result = await client.sendRequest<{
                            packages: NuGetMsg.PackageSummary[];
                            feeds: NuGetMsg.FeedOutcome[];
                        }>('roslynSense/nuget/search', {
                            query: message.query,
                            includePrerelease: message.includePrerelease,
                            skip: message.skip,
                            take,
                            source: message.source || null,
                        });
                        post({
                            type: 'results',
                            gen: message.gen,
                            tab: 'browse',
                            skip: message.skip,
                            results: result.packages,
                            // A short page means the feed had nothing more to give.
                            hasMore: result.packages.length >= take,
                            feeds: result.feeds,
                        });
                        settle(started);
                        break;
                    }

                    case 'updates': {
                        start();
                        const result = await client.sendRequest<{
                            updates: NuGetMsg.PackageUpdate[];
                            feeds: NuGetMsg.FeedOutcome[];
                        }>('roslynSense/nuget/updates', {
                            includePrerelease: message.includePrerelease,
                            versionLock: message.versionLock,
                            projectPaths: message.projectPaths.length > 0 ? message.projectPaths : null,
                            alignPlatform: alignPlatform(),
                        });
                        post({
                            type: 'updates',
                            gen: message.gen,
                            updates: result.updates,
                            feeds: result.feeds,
                        });
                        settle(started);
                        break;
                    }

                    case 'audit': {
                        const audit = await client.sendRequest<NuGetMsg.Audit>(
                            'roslynSense/nuget/audit',
                            { refresh: message.refresh }
                        );
                        post({ type: 'audit', gen: message.gen, audit });
                        break;
                    }

                    case 'versions': {
                        const result = await client.sendRequest<{ versions: string[] }>(
                            'roslynSense/nuget/versions',
                            { id: message.id, includePrerelease: message.includePrerelease }
                        );
                        post({ type: 'versions', id: message.id, versions: result.versions });
                        break;
                    }

                    case 'metadata': {
                        const metadata = await client.sendRequest<NuGetMsg.PackageMetadata | null>(
                            'roslynSense/nuget/metadata',
                            {
                                id: message.id,
                                version: message.version,
                                includeReadme: settings().readme !== 'off',
                            }
                        );
                        post({
                            type: 'metadata',
                            gen: message.gen,
                            id: message.id,
                            version: message.version ?? '',
                            metadata,
                        });
                        break;
                    }

                    case 'icon': {
                        const key = message.iconUrl ?? `embedded:${message.id}`;
                        const result = await client
                            .sendRequest<{ dataUri: string | null }>('roslynSense/nuget/icon', {
                                id: message.id,
                                version: message.version,
                                iconUrl: message.iconUrl,
                                allowDownload: message.allowDownload,
                            })
                            // A missing icon is not an error condition; it is a fallback glyph.
                            .catch(() => ({ dataUri: null }));
                        post({ type: 'icon', id: message.id, key, dataUri: result.dataUri });
                        break;
                    }

                    case 'transitive': {
                        const result = await client.sendRequest<{
                            packages: NuGetMsg.TransitivePackage[];
                        }>('roslynSense/nuget/transitive', {
                            projectPath: message.projectPath,
                            packageId: message.packageId,
                        });
                        post({
                            type: 'transitive',
                            gen: message.gen,
                            projectPath: message.projectPath,
                            packages: result.packages,
                        });
                        break;
                    }

                    case 'sources': {
                        post({
                            type: 'sources',
                            sources: await client.sendRequest<NuGetMsg.PackageSource[]>(
                                'roslynSense/nuget/sources',
                                {}
                            ),
                        });
                        break;
                    }

                    case 'sourceEdit': {
                        const params = await sourceEditParams(client, message);
                        if (!params) {
                            break;
                        }
                        const result = await client.sendRequest<{
                            success: boolean;
                            message: string;
                            sources: NuGetMsg.PackageSource[];
                        }>('roslynSense/nuget/sources/edit', params);

                        post({
                            type: 'sourceEditResult',
                            success: result.success,
                            message: result.message,
                            sources: result.sources,
                        });
                        break;
                    }

                    case 'pickScope': {
                        const picked = await pickScope(projects, scope);
                        if (picked) {
                            scope = picked;
                            rememberScope(context, scope);
                            post({ type: 'scope', projectPaths: scope });
                        }
                        break;
                    }

                    case 'install': {
                        if (!(await acceptLicense(message))) {
                            break;
                        }
                        if (!(await confirmFrameworks(client, message))) {
                            break;
                        }
                        await run(client, panel, `Installing ${message.id}…`, 'install', {
                            id: message.id,
                            version: message.version,
                            projectPaths: message.projectPaths,
                        });
                        break;
                    }

                    case 'uninstall': {
                        await run(client, panel, `Removing ${message.id}…`, 'uninstall', {
                            id: message.id,
                            projectPaths: message.projectPaths,
                        });
                        break;
                    }

                    case 'consolidate': {
                        await run(client, panel, `Consolidating ${message.id}…`, 'consolidate', {
                            id: message.id,
                            version: message.version,
                        });
                        break;
                    }

                    case 'updatePlan': {
                        // Advice for the panel to render inline; a planning failure costs a hint,
                        // never an update.
                        const plan = await client
                            .sendRequest<{ induced: NuGetMsg.InducedUpdate[] }>(
                                'roslynSense/nuget/updatePlan',
                                planParams(message.packages, message.versionLock, message.includePrerelease)
                            )
                            .catch(() => ({ induced: [] as NuGetMsg.InducedUpdate[] }));
                        post({ type: 'updatePlan', gen: message.gen, induced: plan.induced });
                        break;
                    }

                    case 'updateAll': {
                        // The plan is re-computed at execution time rather than trusted from the
                        // panel: the inline preview may be minutes old. The webview has already
                        // shown the induced set, so no modal interrupts here — the outcome list
                        // reports everything that moved.
                        const plan = await client
                            .sendRequest<{ induced: NuGetMsg.InducedUpdate[] }>(
                                'roslynSense/nuget/updatePlan',
                                planParams(message.packages, message.versionLock, message.includePrerelease)
                            )
                            .catch(() => ({ induced: [] as NuGetMsg.InducedUpdate[] }));
                        const packages = merge(message.packages, plan.induced);

                        const result = await vscode.window.withProgress(
                            {
                                location: vscode.ProgressLocation.Notification,
                                title: `Updating ${packages.length} package(s)…`,
                            },
                            () =>
                                client.sendRequest<{ results: NuGetMsg.UpdateOutcome[] }>(
                                    'roslynSense/nuget/updateAll',
                                    { packages, restore: true }
                                )
                        );
                        post({ type: 'opResult', results: result.results });
                        // Only refresh on a clean run: rebuilding the list would wipe the per-row
                        // failure markers seconds after painting them, leaving the user a summary
                        // line and no way to see which project each failure was in.
                        if (result.results.every((outcome) => outcome.success)) {
                            post({ type: 'refresh' });
                        }
                        break;
                    }

                    case 'openExternal': {
                        await openExternal(panel, message.url);
                        break;
                    }

                    case 'openFile': {
                        const document = await vscode.workspace.openTextDocument(
                            vscode.Uri.file(message.path)
                        );
                        await vscode.window.showTextDocument(document);
                        break;
                    }

                    case 'signIn': {
                        await forgetCredential(context, message.feedUrl);
                        post({ type: 'refresh' });
                        break;
                    }

                    case 'persist': {
                        // Handled entirely by the webview's own setState; nothing to do here.
                        break;
                    }
                }
            } catch (error) {
                settle(started);
                fail(error);
            }
        },
        null,
        context.subscriptions
    );

}

type UpdateItem = { id: string; version: string; projectPaths: string[] };

/**
 * The parameters for a dependency plan: what else has to move for these updates to restore.
 *
 * Mode is always "minimal" — the lowest version that satisfies the requirement. A direct reference
 * the updated package outgrew is not a warning; it wins the resolution, so restore fails with
 * NU1605 after every project file has already been written.
 */
function planParams(
    packages: UpdateItem[],
    versionLock: NuGetMsg.Lock,
    includePrerelease: boolean
): Record<string, unknown> {
    return {
        packages,
        mode: 'minimal',
        versionLock,
        includePrerelease,
        alignPlatform: alignPlatform(),
    };
}

/**
 * One entry per (package, version), carrying every project that lands on it.
 *
 * The planner resolves per project, so the same package can be induced to different versions in
 * different projects — collapsing on id alone would silently pick one of them for all of them.
 */
function merge(selected: UpdateItem[], induced: NuGetMsg.InducedUpdate[]): UpdateItem[] {
    const versions = new Map<string, { id: string; version: string; projectPath: string }>();

    for (const item of selected) {
        for (const projectPath of item.projectPaths) {
            versions.set(`${item.id.toLowerCase()}|${projectPath.toLowerCase()}`, {
                id: item.id,
                version: item.version,
                projectPath,
            });
        }
    }

    // Induced wins: the planner never proposes a version below what the selection already asked
    // for, so a collision here is a requirement the selected version did not satisfy.
    for (const item of induced) {
        versions.set(`${item.id.toLowerCase()}|${item.projectPath.toLowerCase()}`, {
            id: item.id,
            version: item.version,
            projectPath: item.projectPath,
        });
    }

    const byVersion = new Map<string, UpdateItem>();
    for (const entry of versions.values()) {
        const key = `${entry.id.toLowerCase()}|${entry.version}`;
        const existing = byVersion.get(key);
        if (existing) {
            existing.projectPaths.push(entry.projectPath);
        } else {
            byVersion.set(key, { id: entry.id, version: entry.version, projectPaths: [entry.projectPath] });
        }
    }

    return [...byVersion.values()];
}

/**
 * Opens a URL that came from package-authored content.
 *
 * `Uri.parse` is non-strict by default and never throws, and `openExternal` on a `command:` URI is
 * arbitrary command execution with the extension's privileges. The CSP does not help here: the
 * exploit path is this message, not the DOM.
 */
async function openExternal(panel: vscode.WebviewPanel, raw: unknown): Promise<void> {
    if (typeof raw !== 'string' || raw.length === 0 || raw.length > 2048) {
        return;
    }

    let uri: vscode.Uri;
    try {
        uri = vscode.Uri.parse(raw, true);
    } catch {
        return;
    }

    if (uri.scheme !== 'http' && uri.scheme !== 'https') {
        void panel.webview.postMessage({
            type: 'error',
            message: `Refused to open a ${uri.scheme}: link from package content.`,
            scope: 'details',
        } satisfies NuGetMsg.ToView);
        return;
    }

    await vscode.env.openExternal(uri);
}

/**
 * Turns a feed edit into request parameters, prompting for anything the webview deliberately does
 * not own.
 *
 * A feed's name and URL end up in a NuGet.config the whole team shares, and removing one changes
 * where every package in the solution resolves from — so text entry and the confirmation both
 * happen here, where VS Code's own input validation and modal dialog are available.
 *
 * Returns undefined when the user backs out at any step.
 */
async function sourceEditParams(
    client: LanguageClient,
    message: Extract<NuGetMsg.ToHost, { type: 'sourceEdit' }>
): Promise<Record<string, unknown> | undefined> {
    if (message.action === 'enable' || message.action === 'disable') {
        return { action: message.action, name: message.name };
    }

    if (message.action === 'reorder') {
        return { action: 'reorder', order: message.order };
    }

    const sources = await client.sendRequest<NuGetMsg.PackageSource[]>(
        'roslynSense/nuget/sources',
        {}
    );
    const existing = sources.find((source) => source.name === message.name);

    if (message.action === 'remove') {
        const confirm = await vscode.window.showWarningMessage(
            `Remove the feed “${message.name}”?`,
            {
                modal: true,
                detail: existing
                    ? `${existing.source}\n\nPackages that only exist on this feed will stop resolving.`
                    : undefined,
            },
            'Remove'
        );
        return confirm === 'Remove' ? { action: 'remove', name: message.name } : undefined;
    }

    const taken = new Set(
        sources
            .filter((source) => source.name !== message.name)
            .map((source) => source.name.toLowerCase())
    );

    const name = await vscode.window.showInputBox({
        title: message.action === 'add' ? 'Add a package feed' : `Edit “${message.name}”`,
        prompt: 'Feed name',
        value: existing?.name ?? '',
        ignoreFocusOut: true,
        validateInput: (value) => {
            const trimmed = value.trim();
            if (trimmed.length === 0) {
                return 'A feed needs a name.';
            }
            return taken.has(trimmed.toLowerCase()) ? 'A feed with that name already exists.' : null;
        },
    });
    if (name === undefined) {
        return undefined;
    }

    const source = await vscode.window.showInputBox({
        title: message.action === 'add' ? 'Add a package feed' : `Edit “${message.name}”`,
        prompt: 'Feed URL, or a path to a folder of .nupkg files',
        value: existing?.source ?? 'https://',
        ignoreFocusOut: true,
        validateInput: (value) => {
            const trimmed = value.trim();
            if (/^https?:\/\/\S+$/i.test(trimmed)) {
                return null;
            }
            // Anything else has to be a folder, and the server checks that it exists — this only
            // catches the obvious typo before a round trip.
            return trimmed.length === 0 || trimmed === 'https://'
                ? 'Enter an http(s) URL or a folder path.'
                : null;
        },
    });
    if (source === undefined) {
        return undefined;
    }

    return message.action === 'add'
        ? { action: 'add', name: name.trim(), source: source.trim() }
        : { action: 'update', name: message.name, newName: name.trim(), source: source.trim() };
}

export async function openSourcesConfig(sources: NuGetMsg.PackageSource[]): Promise<void> {
    const files = [...new Set(sources.map((s) => s.configFilePath).filter((p): p is string => !!p))];

    if (files.length === 0) {
        void vscode.window.showInformationMessage('No NuGet.config file was found for this solution.');
        return;
    }

    const picked =
        files.length === 1
            ? files[0]
            : await vscode.window.showQuickPick(files, { title: 'Open NuGet.config' });

    if (picked) {
        const document = await vscode.workspace.openTextDocument(vscode.Uri.file(picked));
        await vscode.window.showTextDocument(document);
    }
}

/**
 * A licence that requires acceptance is a real consent decision, so it is asked here rather than
 * inside the webview.
 */
async function acceptLicense(message: {
    id: string;
    requireLicenseAcceptance: boolean;
    license: string | null;
}): Promise<boolean> {
    if (!message.requireLicenseAcceptance) {
        return true;
    }

    const answer = await vscode.window.showWarningMessage(
        `${message.id} requires you to accept its licence${message.license ? ` (${message.license})` : ''}.`,
        { modal: true },
        'Accept'
    );
    return answer === 'Accept';
}

/** Warns before an install that restore would reject, without refusing it. */
async function confirmFrameworks(
    client: LanguageClient,
    message: { id: string; version: string; projectPaths: string[] }
): Promise<boolean> {
    const check = await client
        .sendRequest<NuGetMsg.FrameworkCheck>('roslynSense/nuget/checkFramework', {
            id: message.id,
            version: message.version,
            projectPaths: message.projectPaths,
        })
        .catch(() => null);

    if (!check || check.compatible || !check.warning) {
        return true;
    }

    const answer = await vscode.window.showWarningMessage(
        check.warning,
        { modal: true },
        'Install anyway'
    );
    return answer === 'Install anyway';
}

async function run(
    client: LanguageClient,
    panel: vscode.WebviewPanel,
    title: string,
    method: 'install' | 'uninstall' | 'consolidate',
    params: Record<string, unknown>
): Promise<void> {
    const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title },
        () =>
            client.sendRequest<{ success: boolean; message: string }>(
                `roslynSense/nuget/${method}`,
                params
            )
    );

    if (!result.success) {
        void vscode.window.showErrorMessage(result.message);
    }
    void panel.webview.postMessage({ type: 'refresh' } satisfies NuGetMsg.ToView);
}

function settings(): NuGetMsg.Settings {
    const config = vscode.workspace.getConfiguration('roslynSense');
    return {
        pageSize: config.get<number>('nuget.pageSize', 30),
        readme: config.get<'rendered' | 'plain' | 'off'>('nuget.readme', 'rendered'),
        showTransitive: config.get<boolean>('nuget.showTransitive', true),
    };
}

/**
 * Whether platform-tracking packages stay on the .NET major the project targets. Host-side only:
 * the webview never sees it, the server applies it.
 */
function alignPlatform(): boolean {
    return vscode.workspace
        .getConfiguration('roslynSense')
        .get<boolean>('nuget.alignPlatformPackages', true);
}
