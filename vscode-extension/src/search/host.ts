import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { html } from './html';
import { onSolutionReady } from '../solutionReady';
import { tokenizePreview } from './textmate';

/**
 * The extension-host half of the Search Everywhere panel: it owns the LSP requests, the command
 * registry that feeds the Actions tab, and the editor — the webview never opens anything itself.
 */
export function wire(
    context: vscode.ExtensionContext,
    panel: vscode.WebviewPanel,
    getClient: () => LanguageClient | undefined,
    onDispose: () => void
): void {
    panel.webview.html = html(panel.webview, context.extensionUri);

    // One token source per search/preview lane: a new request supersedes the old one, and a
    // request that is already stale must not cancel the one that replaced it.
    let searchInFlight: vscode.CancellationTokenSource | undefined;

    // The query the panel is currently showing an answer to, kept only while that answer was
    // provisional. A search answered from the name index is a promise to ask again: the solution
    // was still loading, so referenced assemblies were invisible to it and a file saved a second
    // ago may not have been in the walk behind it.
    let provisional: Extract<SearchMsg.ToHost, { type: 'search' }> | undefined;

    // The panel's own listeners die with the panel — parking them in context.subscriptions
    // would pile up a wrapper per open/close for the extension's lifetime.
    let disposed = false;
    let previewVersion = 0;

    // Reusing the original request id is what makes this safe to fire at any moment: the webview
    // drops results whose id is not the one it is waiting for, so a rerun that lands after the
    // user has typed again is discarded rather than replacing their newer answer.
    const ready = onSolutionReady(() => {
        if (!disposed && provisional) {
            void search(provisional);
        }
    });

    panel.onDidDispose(() => {
        ready.dispose();
        // Escape while a search runs: the server must stop computing an answer nobody will
        // see, and a reply that still arrives must not touch the disposed webview.
        disposed = true;
        searchInFlight?.cancel();
        searchInFlight?.dispose();
        onDispose();
    });

    const post = (message: SearchMsg.ToView) => {
        if (!disposed) {
            void panel.webview.postMessage(message);
        }
    };

    // A metadata hit is a promise of a file, not a file: the decompilation runs when a row is
    // previewed or opened, and the answer is remembered so open-after-preview costs nothing.
    const metadataTargets = new Map<string, Promise<MetadataTarget | null>>();

    interface MetadataTarget {
        uri: string;
        line: number;
        character: number;
    }

    /** The decompiled file behind a `roslynsense-metadata:` hit — the same target F12 lands on. */
    function resolveMetadata(uri: string): Promise<MetadataTarget | null> {
        const cached = metadataTargets.get(uri);
        if (cached) {
            return cached;
        }

        const client = getClient();
        const resolved: Promise<MetadataTarget | null> = client
            ? client
                  .sendRequest<MetadataTarget | null>('roslynSense/resolveMetadataTarget', { uri })
                  .catch(() => null)
            : Promise.resolve(null);
        metadataTargets.set(uri, resolved);
        // Only success is worth remembering: a failure (server restarting, decompile hiccup)
        // must be retried on the next preview or open, not cached for the panel's lifetime.
        void resolved.then((target) => {
            if (target === null) {
                metadataTargets.delete(uri);
            }
        });
        return resolved;
    }

    /** Metadata hits swap in their decompiled file; everything else passes through. */
    async function toEditorTarget(target: {
        uri: string;
        line: number;
        character: number;
    }): Promise<{ uri: string; line: number; character: number }> {
        if (!target.uri.startsWith('roslynsense-metadata:')) {
            return target;
        }
        return (await resolveMetadata(target.uri)) ?? target;
    }

    panel.webview.onDidReceiveMessage(
        async (message: SearchMsg.ToHost) => {
            switch (message.type) {
                case 'ready':
                    post({ type: 'boot', recent: openEditors() });
                    return;

                case 'search':
                    await search(message);
                    return;

                case 'preview':
                    await preview(message);
                    return;

                case 'open': {
                    const target = await toEditorTarget(message);
                    await open({ ...message, ...target });
                    panel.dispose();
                    return;
                }

                case 'runAction':
                    // Dispose first: a command that opens its own UI (another panel, a picker)
                    // must not land behind this one.
                    panel.dispose();
                    await vscode.commands.executeCommand(message.command);
                    return;

                case 'close':
                    panel.dispose();
                    return;
            }
        }
    );

    async function search(message: Extract<SearchMsg.ToHost, { type: 'search' }>): Promise<void> {
        if (disposed) {
            return;
        }
        searchInFlight?.cancel();
        searchInFlight?.dispose();
        const request = new vscode.CancellationTokenSource();
        searchInFlight = request;
        const token = request.token;
        // A solutionReady notification during this request must not rerun the previous
        // provisional query and cancel the newer query the user is now waiting for.
        provisional = undefined;

        try {
            if (message.tab === 'actions') {
                post({
                    type: 'results',
                    id: message.id,
                    tab: 'actions',
                    items: matchActions(message.query),
                    truncated: false,
                });
                return;
            }

            const client = getClient();
            if (!client) {
                post({ type: 'error', scope: 'search', id: message.id, message: 'The RoslynSense server is not running.' });
                return;
            }

            if (message.tab === 'text') {
                const result = await client.sendRequest<{
                    items: SearchMsg.TextItem[];
                    truncated: boolean;
                    loading?: boolean;
                }>('roslynSense/searchText', { query: message.query, maxResults: 100 }, token);
                if (!token.isCancellationRequested) {
                    provisional = result.loading ? message : undefined;
                    post({ type: 'results', id: message.id, tab: 'text', ...result });
                }
                return;
            }

            const only =
                message.tab === 'classes' ? 'type'
                : message.tab === 'files' ? 'file'
                : message.tab === 'symbols' ? 'member'
                : null;

            const result = await client.sendRequest<{
                items: SearchMsg.SymbolItem[];
                truncated: boolean;
                loading?: boolean;
            }>(
                'roslynSense/searchEverywhere',
                {
                    query: message.query,
                    maxResults: 50,
                    only,
                    includeMetadata: message.includeNonSolution,
                },
                token
            );
            if (!token.isCancellationRequested) {
                provisional = result.loading ? message : undefined;
                post({ type: 'results', id: message.id, tab: message.tab, ...result });
            }
        } catch (error) {
            if (!token.isCancellationRequested) {
                // The server's message names the actual failure (a stale path, a dead
                // connection); the guess is only for errors that carry no message at all.
                const detail = error instanceof Error ? error.message : '';
                post({
                    type: 'error',
                    scope: 'search',
                    id: message.id,
                    message: detail || 'Search failed — is the workspace still loading?',
                });
            }
        } finally {
            request.dispose();
            if (searchInFlight === request) {
                searchInFlight = undefined;
            }
        }
    }

    async function preview(message: Extract<SearchMsg.ToHost, { type: 'preview' }>): Promise<void> {
        const version = ++previewVersion;
        const current = () => !disposed && version === previewVersion;
        try {
            const target = await toEditorTarget({ uri: message.uri, line: message.line, character: 0 });
            if (!current()) {
                return;
            }
            const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(target.uri));
            if (!current()) {
                return;
            }

            let line = Math.min(target.line, document.lineCount - 1);
            if (message.skipPreamble && line === 0) {
                // A file hit previews its first type, not its using block.
                line = firstDeclarationLine(document);
            }

            // Enough above the target to give it a home, enough below to read the body.
            const start = Math.max(0, line - 4);
            const end = Math.min(document.lineCount, line + 36);
            const lines: string[] = [];
            for (let i = start; i < end; i++) {
                lines.push(document.lineAt(i).text);
            }

            const tokens = await tokenizePreview(document, start, end);
            if (!current()) {
                return;
            }
            post({
                type: 'previewText',
                id: message.id,
                startLine: start,
                targetLine: line,
                lines,
                path: vscode.workspace.asRelativePath(vscode.Uri.parse(target.uri), false),
                languageId: document.languageId,
                tokens,
            });
        } catch {
            // Images, archives, anything openTextDocument rejects: the row is still openable
            // (VS Code has viewers for many of these), there is just nothing to preview.
            if (current()) {
                post({ type: 'error', scope: 'preview', id: message.id, message: 'No text preview for this file.' });
            }
        }
    }
}

/**
 * The first type declaration of a C#-family document — where a file preview should start,
 * because a screenful of `using` lines answers nothing about the file.
 */
function firstDeclarationLine(document: vscode.TextDocument): number {
    if (document.languageId !== 'csharp') {
        return 0;
    }

    const declaration =
        /^\s*(?:\[[^\]]*\]\s*)?(?:(?:public|internal|protected|private|abstract|sealed|static|partial|file|readonly|ref|unsafe)\s+)*(?:class|struct|record|interface|enum|delegate)\b/;
    const limit = Math.min(document.lineCount, 500);
    for (let i = 0; i < limit; i++) {
        if (declaration.test(document.lineAt(i).text)) {
            return i;
        }
    }
    return 0;
}

async function open(
    message: Extract<SearchMsg.ToHost, { type: 'open' }> & { line: number; character: number; uri: string }
): Promise<void> {
    const uri = vscode.Uri.parse(message.uri);
    const viewColumn = message.beside ? vscode.ViewColumn.Beside : vscode.ViewColumn.Active;

    // A plain file hit lands at the top — but a file hit with a line ("Customer.cs:851"), a
    // symbol, or a resolved metadata type all land on their line.
    const positioned = !(message.isFile && message.line === 0 && message.character === 0);

    try {
        const document = await vscode.workspace.openTextDocument(uri);
        const editor = await vscode.window.showTextDocument(document, { viewColumn, preview: false });
        if (!positioned) {
            return;
        }

        const position = new vscode.Position(
            Math.min(message.line, document.lineCount - 1),
            message.character
        );
        editor.selection = new vscode.Selection(position, position);
        editor.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenter);
    } catch {
        // Not a text document — an image, an archive. VS Code's own opener picks the viewer.
        await vscode.commands.executeCommand('vscode.open', uri, viewColumn);
    }
}

/** Open editors, shown before anything is typed — the cheapest useful empty-query answer. */
function openEditors(): SearchMsg.RecentItem[] {
    const items: SearchMsg.RecentItem[] = [];
    const seen = new Set<string>();

    for (const group of vscode.window.tabGroups.all) {
        for (const tab of group.tabs) {
            const input = tab.input;
            if (!(input instanceof vscode.TabInputText) || seen.has(input.uri.toString())) {
                continue;
            }

            seen.add(input.uri.toString());
            const relative = vscode.workspace.asRelativePath(input.uri, false);
            items.push({
                name: relative.split(/[\\/]/).pop() ?? relative,
                relativePath: relative,
                uri: input.uri.toString(),
            });
        }
    }

    return items;
}

// ---- Actions tab -------------------------------------------------------------------------

interface ContributedCommand {
    command: string;
    title: string | { value: string };
    category?: string;
}

interface ContributedKeybinding {
    command: string;
    key: string;
    win?: string;
}

let actionCache: SearchMsg.ActionItem[] | undefined;

/**
 * Every command any installed extension contributes, with its title and default keybinding.
 * Built once per session: extensions do not come and go mid-search.
 */
function allActions(): SearchMsg.ActionItem[] {
    if (actionCache) {
        return actionCache;
    }

    const keybindings = new Map<string, string>();
    const actions: SearchMsg.ActionItem[] = [];

    for (const extension of vscode.extensions.all) {
        const contributes = extension.packageJSON?.contributes;

        for (const binding of (contributes?.keybindings ?? []) as ContributedKeybinding[]) {
            const key = process.platform === 'win32' ? (binding.win ?? binding.key) : binding.key;
            if (binding.command && key && !keybindings.has(binding.command)) {
                keybindings.set(binding.command, key);
            }
        }

        for (const command of (contributes?.commands ?? []) as ContributedCommand[]) {
            const title = typeof command.title === 'string' ? command.title : command.title?.value;
            if (!command.command || !title) {
                continue;
            }
            actions.push({
                command: command.command,
                title,
                category: command.category ?? null,
                keybinding: keybindings.get(command.command) ?? null,
            });
        }
    }

    actionCache = actions;
    return actions;
}

/** Substring first, then a subsequence with word-start bonuses — Rider's action matching feel. */
function matchActions(query: string): SearchMsg.ActionItem[] {
    const trimmed = query.trim().toLowerCase();
    if (trimmed.length === 0) {
        return [];
    }

    const scored: { action: SearchMsg.ActionItem; score: number }[] = [];
    for (const action of allActions()) {
        const label = `${action.category ? `${action.category}: ` : ''}${action.title}`;
        const score = fuzzyScore(label.toLowerCase(), trimmed);
        if (score > 0) {
            scored.push({ action, score });
        }
    }

    return scored
        .sort((a, b) => b.score - a.score || a.action.title.length - b.action.title.length)
        .slice(0, 50)
        .map((s) => s.action);
}

function fuzzyScore(candidate: string, query: string): number {
    const direct = candidate.indexOf(query);
    if (direct >= 0) {
        // Earlier and at a word boundary is better; a full-prefix hit best of all.
        return 1000 - direct + (direct === 0 || candidate[direct - 1] === ' ' ? 100 : 0);
    }

    let score = 0;
    let at = 0;
    for (const char of query) {
        if (char === ' ') {
            continue;
        }
        const index = candidate.indexOf(char, at);
        if (index < 0) {
            return 0;
        }
        score += index === 0 || candidate[index - 1] === ' ' ? 10 : 1;
        at = index + 1;
    }
    return score;
}
