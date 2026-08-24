import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { html } from './html';
import {
    listReferenceNames,
    previewConnection,
    splitProvider,
} from './connectionPreview';
import { onProjectSetChanged } from '../projectSet';
import {
    ConfigScope,
    SCOPE_LABELS,
    configFilePath,
    loadLayers,
    writeSetting,
} from '../roslynsenseConfig';

/**
 * The extension-host half of the settings panel: it owns the files.
 *
 * The webview renders a form from the schema and reports what the person changed; every read and
 * every write happens here. Nothing in the panel edits VS Code's own settings — `roslynSense.*`
 * keys stay in the Settings editor where people already look for them, and this is only about
 * `roslynsense.json`, which is a different file with a different audience.
 */
export function wire(
    context: vscode.ExtensionContext,
    panel: vscode.WebviewPanel,
    onDispose: () => void,
    getClient: () => LanguageClient | undefined = () => undefined
): void {
    panel.webview.html = html(panel.webview, context.extensionUri);

    let scope: ConfigScope = 'repo';
    let disposed = false;

    /**
     * The edits made since the last save, in the order they were made.
     *
     * Held here rather than only in the page because closing the tab is what asks whether to keep
     * them, and the webview is gone by the time that question can be answered. Keyed by scope and
     * path, so changing one setting twice before saving writes it once.
     */
    const pending = new Map<string, { scope: ConfigScope; path: readonly string[]; value: unknown }>();

    panel.onDidDispose(() => {
        disposed = true;
        onDispose();
        void offerToKeep();
    });

    /**
     * The tab closed with edits nobody wrote. VS Code has no way to ask before a webview panel
     * goes — there is no veto and no "are you sure" — so the question is asked after, where it can
     * still save what the person typed rather than let it vanish silently.
     */
    async function offerToKeep(): Promise<void> {
        if (pending.size === 0) {
            return;
        }

        const keep = 'Save';
        const answer = await vscode.window.showWarningMessage(
            `RoslynSense settings closed with ${count(pending.size, 'unsaved change')}.`,
            { modal: true },
            keep
        );

        if (answer === keep) {
            await saveAll();
        }
        pending.clear();
    }

    /** Writes every pending edit, weakest scope first, and reports the first failure. */
    async function saveAll(): Promise<string | undefined> {
        const edits = [...pending.values()];
        pending.clear();

        let written: string | undefined;
        for (const edit of edits) {
            // `null` over the wire is the panel's "unset"; `undefined` does not survive JSON, and
            // removing the key is what puts the setting back to inherited.
            written = await writeSetting(
                edit.scope,
                workingDirectory(),
                edit.path as string[],
                edit.value === null ? undefined : edit.value
            );
        }

        return written;
    }

    const post = (message: SettingsMsg.ToView) => {
        if (!disposed) {
            void panel.webview.postMessage(message);
        }
    };

    /**
     * The panel is one window's view of files several things write — the person's own editor, the
     * server, another window, a git checkout. Re-sending the whole state on any change to any
     * layer is cheap (four small files) and is the only version of this that cannot go stale.
     */
    const watcher = vscode.workspace.createFileSystemWatcher('**/roslynsense*.json');
    const refresh = () => post(buildState(scope));
    watcher.onDidChange(refresh);
    watcher.onDidCreate(refresh);
    watcher.onDidDelete(refresh);
    panel.onDidDispose(() => watcher.dispose());

    // What a control resolves against is the loaded solution, and a panel opened during load is
    // told "nothing yet" by every one of them. Nothing asked again, so the answer stood until the
    // panel was closed and reopened.
    const resolvable = onProjectSetChanged(() => post({ type: 'resolvable' }));
    panel.onDidDispose(() => resolvable.dispose());

    panel.webview.onDidReceiveMessage(async (message: SettingsMsg.ToHost) => {
        switch (message.type) {
            case 'ready':
                post(buildState(scope));
                return;

            case 'selectScope':
                scope = message.scope;
                post(buildState(scope));
                return;

            case 'openFile': {
                // Created empty rather than refused: "open the file I would be writing to" is a
                // reasonable thing to ask of a scope nobody has written to yet.
                if (!fs.existsSync(message.filePath)) {
                    await fs.promises.mkdir(dirOf(message.filePath), { recursive: true });
                    await fs.promises.writeFile(message.filePath, '{\n}\n', 'utf8');
                }
                const document = await vscode.workspace.openTextDocument(message.filePath);
                await vscode.window.showTextDocument(document, { preview: false });
                return;
            }

            case 'completeConnection': {
                const items = await connectionCompletions(message.value);
                post({ type: 'connectionCompletions', value: message.value, items });
                return;
            }

            case 'resolveConnections': {
                const results: Record<string, SettingsMsg.ConnectionPreview> = {};
                for (const value of message.values) {
                    results[value] = previewConnection(value, workingDirectory()) ?? {};
                }
                post({ type: 'connectionsResolved', results });
                return;
            }

            case 'askChoices': {
                // Answered against the merge the page is showing rather than the config the
                // server booted with: a convention someone just added has to be offerable as a
                // fallback before the file is saved and reloaded.
                const items = await ask<{ items: SettingsMsg.Choice[] }>(
                    'roslynSense/settingChoices',
                    { path: message.path, config: loadLayers(workingDirectory()).merged }
                );
                post({ type: 'settingChoices', token: message.token, items: items?.items ?? [] });
                return;
            }

            case 'askMemberShape': {
                const shape = await ask<Omit<SettingsMsg.MemberShape, 'type' | 'token'>>(
                    'roslynSense/memberShape',
                    {
                        containingType: message.containingType,
                        memberName: message.memberName,
                        parameterTypes: message.parameterTypes,
                        // Which kinds of member the setting can name; the server offers every kind
                        // when the page does not narrow it.
                        kinds: message.kinds,
                    }
                );

                post({
                    type: 'memberShape',
                    token: message.token,
                    typeSuggestions: shape?.typeSuggestions ?? [],
                    memberSuggestions: shape?.memberSuggestions ?? [],
                    matches: shape?.matches ?? [],
                    resolvedType: shape?.resolvedType,
                    problem: shape
                        ? shape.problem
                        : 'The language server is not running, so nothing can be resolved yet.',
                });
                return;
            }

            case 'edit':
                pending.set(`${message.scope}\u0000${message.path.join('.')}`, {
                    scope: message.scope,
                    path: message.path,
                    value: message.value,
                });
                return;

            case 'discard':
                pending.clear();
                post({ type: 'saved' });
                post(buildState(scope));
                return;

            case 'save': {
                if (pending.size === 0) {
                    return;
                }

                const changed = pending.size;
                try {
                    const written = await saveAll();
                    post({ type: 'saved' });
                    post(buildState(scope, `${count(changed, 'change')} written to ${written}`));
                } catch (error) {
                    // Whatever did land is on disk; the state that follows is what actually is.
                    post({ type: 'saved' });
                    post(buildState(scope, `Could not save: ${describe(error)}`));
                }
                return;
            }
        }
    });

    /**
     * One request to the server, or undefined when there is nobody to ask. A settings page is
     * usable without a loaded solution — every control except the two that resolve symbols — so a
     * missing client is a quieter answer rather than an error.
     */
    async function ask<T>(method: string, parameters: unknown): Promise<T | undefined> {
        const client = getClient();
        if (!client) {
            return undefined;
        }

        try {
            return await client.sendRequest<T>(method, parameters);
        } catch {
            return undefined;
        }
    }

    function buildState(current: ConfigScope, notice?: string): SettingsMsg.State {
        const directory = workingDirectory();
        const { layers, merged } = loadLayers(directory);

        // The four the scope selector offers, by the path they resolve to for this directory.
        // Every other layer — a `roslynsense.json` in some ancestor — is still shown as an origin
        // but is edited where it lives, because "which parent did this come from" is a question
        // the chip answers better than a fifth tab would.
        const editablePaths = new Set(
            (['global', 'repo', 'repoLocal', 'personal'] as const).map((s) =>
                normalize(configFilePath(s, directory))
            )
        );

        return {
            type: 'state',
            schema: readSchema(context),
            workingDirectory: directory,
            scope: current,
            notice,
            effective: merged,
            layers: layers.map((layer) => ({
                scope: layer.scope,
                label: SCOPE_LABELS[layer.scope],
                filePath: layer.filePath,
                exists: layer.json !== undefined || layer.parseError !== undefined,
                json: layer.json,
                parseError: layer.parseError,
                editable: editablePaths.has(normalize(layer.filePath)),
            })),
        };
    }
}

/**
 * Suggestions for a connection value, staged by what has been typed so far: provider → reference
 * kind → config file → name inside the file. Every item is the full value, ready to accept.
 */
async function connectionCompletions(value: string): Promise<string[]> {
    const { head, ref } = splitProvider(value);

    // Still typing the provider prefix.
    if (head === '') {
        return ['mssql:', 'psql:', 'sqlite:'].filter((p) =>
            p.startsWith(value.toLowerCase())
        );
    }

    const kindMatch = /^(json|xml):/i.exec(ref);
    if (!kindMatch) {
        // Could become a raw connection string — offer the reference forms while it still might
        // be one, and stop suggesting once it clearly is not.
        const forms = [`${head}json:`, `${head}xml:`];
        return forms.filter((form) => form.toLowerCase().startsWith(value.toLowerCase()));
    }

    const kind = kindMatch[1].toLowerCase() as 'json' | 'xml';
    const body = ref.slice(kind.length + 1);
    const hash = body.indexOf('#');

    if (hash < 0) {
        // Complete the file path from the config files the workspace actually has.
        const pattern = kind === 'json' ? '**/appsettings*.json' : '**/*.config';
        const files = await vscode.workspace.findFiles(
            pattern,
            '**/{node_modules,bin,obj}/**',
            50
        );
        return files
            .map((uri) => `${head}${kind}:${vscode.workspace.asRelativePath(uri)}#`)
            .sort();
    }

    // Complete the name from inside the chosen file.
    const filePart = body.slice(0, hash);
    const filePath = path.isAbsolute(filePart)
        ? filePart
        : path.resolve(workingDirectory(), filePart);
    return listReferenceNames(filePath, kind).map(
        (name) => `${head}${kind}:${filePart}#${name}`
    );
}

/**
 * The directory the layers resolve for — the same one the server is launched in, so that what the
 * panel shows is what the server sees.
 */
function workingDirectory(): string {
    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
}

/** The schema the form is built from, shipped beside the extension. */
function readSchema(context: vscode.ExtensionContext): unknown {
    const schemaPath = vscode.Uri.joinPath(
        context.extensionUri,
        'schemas',
        'roslynsense.schema.json'
    ).fsPath;

    try {
        return JSON.parse(fs.readFileSync(schemaPath, 'utf8'));
    } catch {
        // A packaged extension always has it; a broken install gets an empty form rather than an
        // exception on the way to one.
        return { properties: {} };
    }
}

function dirOf(filePath: string): string {
    const index = Math.max(filePath.lastIndexOf('/'), filePath.lastIndexOf('\\'));
    return index > 0 ? filePath.slice(0, index) : filePath;
}

function normalize(filePath: string): string {
    return filePath.replace(/\\/g, '/').toLowerCase();
}

function describe(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

function count(n: number, noun: string): string {
    return `${n} ${noun}${n === 1 ? '' : 's'}`;
}
