import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * Search Everywhere: one box over types, members and files, ranked by the server.
 *
 * Not the built-in symbol picker, for two reasons. It scores and re-sorts whatever the server
 * returns — so the ranking work happens twice and the client wins — and it has no room for files
 * or for a kind filter. Driving our own QuickPick means the list is exactly what the server said,
 * in the order it said it: every item is marked `alwaysShow`, which is the documented way to
 * switch off QuickPick's own filtering.
 */

const METHOD = 'roslynSense/searchEverywhere';

/** Long enough that a fast typist sends one request per word, short enough to feel live. */
const DEBOUNCE_MS = 90;

interface SearchEverywhereItem {
    kind: 'type' | 'member' | 'file';
    name: string;
    container: string | null;
    uri: string;
    path: string;
    line: number;
    character: number;
    symbolKind: number;
}

interface SearchEverywhereResult {
    items: SearchEverywhereItem[];
    truncated: boolean;
}

interface ResultQuickPickItem extends vscode.QuickPickItem {
    hit?: SearchEverywhereItem;
    openTab?: vscode.Uri;
}

export function registerSearchEverywhere(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.searchEverywhere', () => show(getClient))
    );
}

function show(getClient: () => LanguageClient | undefined): void {
    const quickPick = vscode.window.createQuickPick<ResultQuickPickItem>();
    quickPick.placeholder = 'Search everywhere — t: types, m: members, f: files; Namespace.Type.Member narrows';
    quickPick.matchOnDescription = false;
    quickPick.matchOnDetail = false;

    let debounce: NodeJS.Timeout | undefined;
    let inFlight: vscode.CancellationTokenSource | undefined;
    let queryId = 0;

    const openTabs = listOpenTabs();
    quickPick.items = openTabs;

    const search = async (value: string): Promise<void> => {
        const client = getClient();
        if (!client) {
            quickPick.items = [{ label: 'The RoslynSense server is not running.', alwaysShow: true }];
            return;
        }

        // Nothing typed yet: the open editors are the best guess, and they cost no round trip.
        if (value.trim().length === 0) {
            quickPick.busy = false;
            quickPick.items = openTabs;
            return;
        }

        inFlight?.cancel();
        inFlight = new vscode.CancellationTokenSource();
        const token = inFlight.token;
        const id = ++queryId;
        quickPick.busy = true;

        try {
            const result = await client.sendRequest<SearchEverywhereResult>(
                METHOD,
                { query: value, maxResults: 50 },
                token
            );

            // A slower earlier query must not overwrite a newer one's results.
            if (token.isCancellationRequested || id !== queryId) {
                return;
            }

            quickPick.items = result.items.length > 0
                ? result.items.map(toQuickPickItem)
                : [{ label: `No results for "${value}"`, alwaysShow: true }];
        } catch {
            if (id === queryId) {
                quickPick.items = [{ label: 'Search failed — is the workspace still loading?', alwaysShow: true }];
            }
        } finally {
            if (id === queryId) {
                quickPick.busy = false;
            }
        }
    };

    quickPick.onDidChangeValue((value) => {
        if (debounce) {
            clearTimeout(debounce);
        }
        debounce = setTimeout(() => void search(value), DEBOUNCE_MS);
    });

    quickPick.onDidAccept(() => {
        const selected = quickPick.selectedItems[0];
        quickPick.hide();
        if (selected?.hit) {
            void open(selected.hit);
        } else if (selected?.openTab) {
            void vscode.window.showTextDocument(selected.openTab, { preview: false });
        }
    });

    quickPick.onDidHide(() => {
        if (debounce) {
            clearTimeout(debounce);
        }
        inFlight?.cancel();
        quickPick.dispose();
    });

    quickPick.show();
}

function toQuickPickItem(hit: SearchEverywhereItem): ResultQuickPickItem {
    return {
        label: hit.name,
        description: hit.container ?? undefined,
        detail: describeLocation(hit),
        iconPath: new vscode.ThemeIcon(codicon(hit)),
        // Switches off QuickPick's own filtering so the server's ranking is what the user sees.
        alwaysShow: true,
        hit,
    };
}

function describeLocation(hit: SearchEverywhereItem): string {
    const relative = vscode.workspace.asRelativePath(vscode.Uri.file(hit.path), false);
    return hit.kind === 'file' ? relative : `${relative}:${hit.line + 1}`;
}

/** Open editors, shown before anything is typed. */
function listOpenTabs(): ResultQuickPickItem[] {
    const items: ResultQuickPickItem[] = [];
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
                label: relative.split(/[\\/]/).pop() ?? relative,
                detail: relative,
                iconPath: new vscode.ThemeIcon('symbol-file'),
                alwaysShow: true,
                openTab: input.uri,
            });
        }
    }

    return items;
}

async function open(hit: SearchEverywhereItem): Promise<void> {
    const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(hit.uri));
    const editor = await vscode.window.showTextDocument(document, { preview: false });

    if (hit.kind === 'file') {
        return;
    }

    const position = new vscode.Position(hit.line, hit.character);
    editor.selection = new vscode.Selection(position, position);
    editor.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenter);
}

/** LSP SymbolKind → the codicon VS Code uses for that kind elsewhere. */
function codicon(hit: SearchEverywhereItem): string {
    if (hit.kind === 'file') {
        return 'symbol-file';
    }

    switch (hit.symbolKind) {
        case 3: return 'symbol-namespace';
        case 5: return 'symbol-class';
        case 6: return 'symbol-method';
        case 7: return 'symbol-property';
        case 8: return 'symbol-field';
        case 9: return 'symbol-constructor';
        case 10: return 'symbol-enum';
        case 11: return 'symbol-interface';
        case 12: return 'symbol-function';
        case 13: return 'symbol-variable';
        case 14: return 'symbol-constant';
        case 22: return 'symbol-enum-member';
        case 23: return 'symbol-structure';
        case 24: return 'symbol-event';
        case 25: return 'symbol-operator';
        case 26: return 'symbol-parameter';
        default: return 'symbol-misc';
    }
}
