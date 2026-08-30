import * as vscode from 'vscode';
import { State } from 'vscode-languageclient/node';
import type { LanguageClient } from 'vscode-languageclient/node';
import { onClientReady } from './clientReady';
import { extensionDebug } from './extensionDebug';
import { onSolutionReady } from './solutionReady';
import { iconFor } from './treeIcons';
import type { SolutionTreeNode, TreeNavigation } from './treeIcons';

/**
 * Discovery: what the solution *runs and exposes*, as opposed to what it contains.
 *
 * The Solution Explorer answers "where does this file live", and there are facts about a solution
 * that no arrangement of folders holds. What runs on a schedule is one — the registrations are
 * ordinary calls in a startup file, so nothing in a file tree stands for them and the job methods
 * look uncalled. What the app serves over HTTP and what it exposes over gRPC are two more.
 *
 * Every section here is contributed by a language pack on the server; this file knows about none
 * of them. It lists whatever the server returns and routes clicks back by node id, so a new
 * section is a server change alone.
 */

const VIEW_ID = 'roslynSense.discovery';

/** What the server answers when asked what implements the thing a row names. */
interface ImplementationsResult {
    locations: Array<{
        uri: string;
        range: { start: { line: number; character: number } };
    }>;

    /**
     * Why there are none, when there are none. The server sends this because it is the only side
     * that can tell "you have not built this yet" apart from "nothing implements it" — see
     * `DiscoveryImplementationsResult` on the server for the four cases.
     */
    reason?: string | null;
}

export function registerDiscoveryExplorer(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const changeEmitter = new vscode.EventEmitter<SolutionTreeNode | undefined>();
    const log = extensionDebug();

    /**
     * Waits for a client that is still starting.
     *
     * The view is built at activation and asks for its roots straight away, which is before the
     * client has connected — and a request sent to a starting client is rejected outright.
     * Answering empty instead leaves the view blank for good, because a `TreeDataProvider` is
     * never asked again unless it says something changed. The same wait the Solution Explorer
     * does, for the same reason.
     */
    function whenRunning(client: LanguageClient): Promise<boolean> {
        if (client.state === State.Running) {
            return Promise.resolve(true);
        }

        return new Promise<boolean>((resolve) => {
            const subscription = client.onDidChangeState((e) => {
                if (e.newState === State.Running) {
                    subscription.dispose();
                    clearTimeout(timer);
                    resolve(true);
                }
            });

            const timer = setTimeout(() => {
                subscription.dispose();
                resolve(false);
            }, 60_000);
        });
    }

    async function fetchChildren(nodeId: string | null): Promise<SolutionTreeNode[]> {
        const label = nodeId ?? '<roots>';
        const client = getClient();

        if (!client) {
            log.appendLine(`discoveryTree(${label}): no client`);
            return [];
        }

        if (!(await whenRunning(client))) {
            log.appendLine(`discoveryTree(${label}): client never reached Running`);
            return [];
        }

        try {
            return await client.sendRequest<SolutionTreeNode[]>('roslynSense/discoveryTree', {
                nodeId,
            });
        } catch (error) {
            // Returning [] silently would make a failed request look like a section with nothing
            // in it — "this solution schedules nothing", which is an answer, and a wrong one.
            log.appendLine(
                `discoveryTree(${label}) failed: ${
                    error instanceof Error ? error.message : String(error)
                }`
            );
            return [];
        }
    }

    const provider: vscode.TreeDataProvider<SolutionTreeNode> = {
        onDidChangeTreeData: changeEmitter.event,

        getTreeItem(node) {
            const item = new vscode.TreeItem(
                node.label,
                node.hasChildren
                    ? vscode.TreeItemCollapsibleState.Collapsed
                    : vscode.TreeItemCollapsibleState.None
            );
            item.id = node.id;
            item.description = node.description ?? undefined;
            item.contextValue = node.contextValue;

            // The server's wording when it sent any, because it is the half that knows what the
            // row left out — the method serving a route, and the line it is written on. Left
            // unset, VS Code falls back to the resource path, which is right for a file row.
            if (node.tooltip) {
                item.tooltip = node.tooltip;
            }

            if (node.resourceUri) {
                item.resourceUri = vscode.Uri.parse(node.resourceUri);
            }

            // Every row gets an icon, always. A row without one draws its label where the icon
            // would have been, and VS Code reacts to an expandable row with no icon by collapsing
            // the twistie column on its leaf siblings — so one icon-less kind shifts its whole
            // branch out of line with the rest of the tree.
            item.iconPath = iconFor(node, context.extensionUri, false);

            // Clicking a leaf goes where it points. Preview, so that reading down a list of rpcs
            // reuses one tab rather than leaving a dozen open.
            //
            // A row with children keeps VS Code's own click, which opens it. A branch that both
            // navigates and expands makes every step down the tree open a file nobody asked for
            // — and the tree is read by walking it, so that is most clicks. The Definition button
            // and the context menu are how a branch row navigates, and they are on every row that
            // has somewhere to go.
            if (node.goTo && !node.hasChildren) {
                item.command = {
                    command: 'vscode.open',
                    title: 'Go to Definition',
                    arguments: [vscode.Uri.parse(node.goTo.uri), selectionAt(node.goTo)],
                };
            }

            return item;
        },

        getChildren: (node) => fetchChildren(node?.id ?? null),
    };

    const view = vscode.window.createTreeView(VIEW_ID, {
        treeDataProvider: provider,
        showCollapseAll: true,
    });
    context.subscriptions.push(view, changeEmitter);

    const refresh = (node?: SolutionTreeNode) => changeEmitter.fire(node);

    /** The row a command should act on — the clicked one, or the selection for a keybinding. */
    const targetOf = (node?: SolutionTreeNode): SolutionTreeNode | undefined =>
        node ?? view.selection[0];

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.discovery.refresh', () => refresh()),

        vscode.commands.registerCommand(
            'roslynSense.discovery.goToDefinition',
            async (clicked?: SolutionTreeNode) => {
                const target = targetOf(clicked)?.goTo;
                if (target) {
                    await open(target);
                }
            }
        ),

        vscode.commands.registerCommand(
            'roslynSense.discovery.goToImplementation',
            async (clicked?: SolutionTreeNode) => {
                const node = targetOf(clicked);
                if (node) {
                    await goToImplementation(node);
                }
            }
        )
    );

    /**
     * Where the thing this row names is implemented.
     *
     * Two ways of answering, and which one applies is a property of the section rather than of
     * this command. A scheduled job and a route already know their method — the server resolved it
     * when it drew the row, so the button is a jump. An rpc does not: crossing from a `.proto` to
     * the C# honouring it is a solution-wide symbol search, far too expensive to run for every row
     * on expand, and it can have more than one answer. That one is asked for here, once, because
     * somebody pressed the button.
     */
    async function goToImplementation(node: SolutionTreeNode): Promise<void> {
        if (node.goToSecondary) {
            await open(node.goToSecondary);
            return;
        }

        const client = getClient();
        if (!node.goTo || !client) {
            return;
        }

        // The same wait the listing does. A row can be on screen while the client is restarting —
        // the tree keeps what it drew — and a request sent to a starting client is rejected, which
        // as a button press reads as nothing happening at all.
        if (!(await whenRunning(client))) {
            void vscode.window.showWarningMessage(
                'RoslynSense is not running yet, so there is nothing to search.'
            );
            return;
        }

        const from = node.goTo;
        let result: ImplementationsResult | undefined;

        try {
            result = await vscode.window.withProgress(
                {
                    // A notification rather than the status bar, which is not a presentation
                    // choice: VS Code draws a cancel button only on this location, so `cancellable`
                    // on a Window progress is inert and the token below could never fire.
                    location: vscode.ProgressLocation.Notification,
                    title: `Finding what implements ${node.label}…`,
                    cancellable: true,
                },
                // The token is handed to the request rather than merely awaited around it. The
                // search waits for every project consuming the contract to finish loading, which
                // for a low-level one is the whole solution, and that token is the only bound on
                // the wait — dropping it would leave the server working on an answer nobody is
                // going to read.
                (_progress, token) =>
                    client.sendRequest<ImplementationsResult>(
                        'roslynSense/discoveryImplementations',
                        {
                            textDocument: { uri: from.uri },
                            position: from.range.start,
                        },
                        token
                    )
            );
        } catch (error) {
            // Cancelling rejects the request, and a cancelled search is not a failure worth a
            // message — the user is the one who stopped it.
            log.appendLine(
                `discoveryImplementations(${node.label}) ended: ${
                    error instanceof Error ? error.message : String(error)
                }`
            );
            return;
        }

        const locations = result?.locations ?? [];

        if (locations.length === 0) {
            void vscode.window.showInformationMessage(
                result?.reason ?? `No implementation found for ${node.label}.`
            );
            return;
        }

        if (locations.length === 1) {
            await openLocation(locations[0]);
            return;
        }

        // More than one is normal rather than exceptional — a real server and a test double
        // implement the same rpc — so the choice is offered instead of the first being guessed at.
        const picked = await vscode.window.showQuickPick(
            locations.map((location) => ({
                label: `$(symbol-class) ${basename(location.uri)}`,
                description: `line ${location.range.start.line + 1}`,
                detail: vscode.workspace.asRelativePath(vscode.Uri.parse(location.uri), false),
                location,
            })),
            { title: `Implementations of ${node.label}`, matchOnDetail: true }
        );

        if (picked) {
            await openLocation(picked.location);
        }
    }

    // Built before the client exists, so the first listing has nothing to ask. Refreshing on the
    // transitions is what fills the view in: once when the client first connects, again whenever
    // it re-enters Running (a restart, or binding to another solution), and once more when the
    // solution has finished loading — which is when a section that answered from a project-file
    // scan can answer from a parse instead.
    context.subscriptions.push(onSolutionReady(() => refresh()));

    // Replaced rather than added to. `onClientReady` fires again on every restart, and a
    // subscription made inside it would outlive the client it was made on — after three restarts a
    // single transition refreshes the view three times, each one a round trip per visible section.
    let stateChanges: vscode.Disposable | undefined;
    context.subscriptions.push({ dispose: () => stateChanges?.dispose() });

    onClientReady(context, getClient, (client) => {
        refresh();

        stateChanges?.dispose();
        stateChanges = client.onDidChangeState((e) => {
            if (e.newState === State.Running) {
                refresh();
            }
        });
    });
}

/** The selection argument `vscode.open` takes, from a range the server sent. */
function selectionAt(target: TreeNavigation): vscode.TextDocumentShowOptions {
    return {
        selection: new vscode.Range(
            target.range.start.line,
            target.range.start.character,
            target.range.end.line,
            target.range.end.character
        ),
        preview: true,
    };
}

async function open(target: TreeNavigation): Promise<void> {
    await vscode.commands.executeCommand(
        'vscode.open',
        vscode.Uri.parse(target.uri),
        selectionAt(target)
    );
}

/**
 * Opens one search result. Through the shared command rather than `vscode.open`, because that one
 * also reveals the line — a jump into the middle of a long file is no use with the target scrolled
 * off the bottom.
 */
async function openLocation(location: {
    uri: string;
    range: { start: { line: number; character: number } };
}): Promise<void> {
    await vscode.commands.executeCommand(
        'roslynSense.openLocation',
        location.uri,
        location.range.start.line,
        location.range.start.character
    );
}

function basename(uri: string): string {
    const path = vscode.Uri.parse(uri).path;
    return path.slice(path.lastIndexOf('/') + 1);
}
