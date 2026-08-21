import * as Path from 'path';
import * as vscode from 'vscode';
import { State } from 'vscode-languageclient/node';
import type { LanguageClient } from 'vscode-languageclient/node';
import { onClientReady } from './clientReady';
import { isUnder, normalisePath } from './paths';
import { composite, restore, snapshot, UndoStack } from './solutionUndo';
import type { Snapshot, UndoStep } from './solutionUndo';
import { onProjectSetChanged } from './projectSet';

/**
 * Solution Explorer: the solution's *logical* structure, the way Visual Studio and Rider show
 * it — solution folders, a Dependencies subtree per project, and files nested under the file
 * they belong to.
 *
 * Everything is lazy. The root listing costs a .sln parse; a project is only evaluated when
 * someone expands it, which is what keeps a large solution from stalling on open.
 */

interface SolutionTreeNode {
    id: string;
    kind: string;
    label: string;
    description: string | null;
    resourceUri: string | null;
    hasChildren: boolean;
    contextValue: string;
    dimmed: boolean;
    highlights: [number, number][] | null;
}

const VIEW_ID = 'roslynSense.solutionExplorer';

/** Private drag payload: the dragged nodes, as JSON. */
const DROP_MIME = 'application/vnd.code.tree.roslynsense.solutionexplorer';

/** What a drag from outside the tree carries — the OS file explorer, or VS Code's own. */
const URI_LIST_MIME = 'text/uri-list';

/**
 * A dragged node, reduced to what a drop needs.
 *
 * The id matters as much as the URI: a project and a file dropped on the same target mean
 * completely different edits, and a payload of bare URIs cannot tell them apart.
 */
interface DragItem {
    id: string;
    resourceUri: string | null;
}

interface TreeEditResult {
    ok: boolean;
    message: string;
    uri: string | null;
    edit: unknown;
}

/** Toggle state, persisted per workspace so the view opens the way it was left. */
interface ViewState {
    showAllFiles: boolean;
    showIgnored: boolean;
    revealActiveFile: boolean;
    fileNesting: boolean;
}

/** What `Ctrl+C` / `Ctrl+X` put down, until a paste picks it up. */
interface Clipboard {
    items: DragItem[];
    cut: boolean;
}

export function registerSolutionExplorer(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const state: ViewState = {
        showAllFiles: context.workspaceState.get('roslynSense.showAllFiles', false),
        showIgnored: context.workspaceState.get('roslynSense.showIgnored', false),
        revealActiveFile: context.workspaceState.get('roslynSense.revealActiveFile', false),
        fileNesting: context.workspaceState.get(
            'roslynSense.fileNesting',
            vscode.workspace.getConfiguration('roslynSense').get('solutionExplorer.fileNesting', true)
        ),
    };

    let filter: string | undefined;
    let clipboard: Clipboard | undefined;

    /// Whether files are drawn by the user's file icon theme instead of by this extension.
    const readFileIconSource = () =>
        vscode.workspace
            .getConfiguration('roslynSense')
            .get<string>('solutionExplorer.fileIcons', 'roslynSense') === 'theme';
    let fileIconsFromTheme = readFileIconSource();

    /// Unloading is a per-window view choice rather than a property of the solution, so it lives
    /// here and never reaches the solution file.
    let unloaded: string[] = context.workspaceState.get('roslynSense.unloadedProjects', []);
    let startupProject: string | undefined =
        context.workspaceState.get('roslynSense.startupProject', undefined);

    const changeEmitter = new vscode.EventEmitter<SolutionTreeNode | undefined>();
    const nodesById = new Map<string, SolutionTreeNode>();
    const log = vscode.window.createOutputChannel('RoslynSense Solution Explorer');
    context.subscriptions.push(log);

    /// Who listed each node, which is the only record of the parent for ids that do not encode it.
    const parentById = new Map<string, string>();

    /** The solution the tree is showing, learned from its root node. */
    const solutionUriOf = (): string | undefined => {
        for (const id of nodesById.keys()) {
            if (id.startsWith('solution:')) {
                return vscode.Uri.file(id.slice('solution:'.length)).toString();
            }
        }
        return undefined;
    };

    /**
     * Waits for a client that is still starting.
     *
     * The tree is built at activation and asks for its roots straight away, which is before the
     * client has connected — and a request sent to a starting client is rejected outright.
     * Answering empty instead is what left the view blank, because a `TreeDataProvider` is never
     * asked again unless it says something changed, and refreshing on the state transition is a
     * race against the transition having already happened. Waiting has neither problem.
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

            // A client that never starts must not leave the view spinning forever.
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
            log.appendLine(`solutionTree(${label}): no client`);
            return [];
        }

        if (!(await whenRunning(client))) {
            log.appendLine(`solutionTree(${label}): client never reached Running`);
            return [];
        }
        try {
            const children = await client.sendRequest<SolutionTreeNode[]>(
                'roslynSense/solutionTree',
                {
                    nodeId,
                    showAllFiles: state.showAllFiles,
                    showIgnored: state.showIgnored,
                    filter: filter ?? null,
                    fileNesting: state.fileNesting,
                    unloadedProjects: unloaded,
                }
            );
            children.forEach((child) => {
                nodesById.set(child.id, child);
                if (nodeId) {
                    parentById.set(child.id, nodeId);
                }
            });
            return children;
        } catch (error) {
            // Silently returning [] made a failed request indistinguishable from an empty node,
            // which is how the whole view could vanish on a collapse-and-reopen with nothing
            // anywhere to say why. VS Code shows nothing for a rejected getChildren either, so
            // the log is the only place this can be said.
            log.appendLine(
                `solutionTree(${nodeId ?? '<roots>'}) failed: ${
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
                node.highlights
                    ? { label: node.label, highlights: node.highlights }
                    : node.label,
                node.hasChildren
                    ? vscode.TreeItemCollapsibleState.Collapsed
                    : vscode.TreeItemCollapsibleState.None
            );
            item.id = node.id;
            item.description = node.description ?? undefined;
            item.contextValue = node.contextValue;

            if (node.resourceUri) {
                item.resourceUri = vscode.Uri.parse(node.resourceUri);
            }
            // Every row gets an icon, always. A row without one draws its label where the icon
            // would have been, so a single icon-less kind shifts its whole branch out of line
            // with the rest of the tree.
            item.iconPath = iconFor(node, context.extensionUri, fileIconsFromTheme);
            if (node.kind === 'file' || node.kind === 'solutionItem' || node.kind === 'import') {
                item.command = {
                    command: 'vscode.open',
                    title: 'Open',
                    arguments: [item.resourceUri],
                };
            }
            // A reference row is a pointer to a project that is already in the tree, so clicking
            // it goes there instead of opening the .csproj or expanding a second copy of it.
            if (node.kind === 'projectRef' && node.resourceUri) {
                item.command = {
                    command: 'roslynSense.solutionExplorer.goToProject',
                    title: 'Go to Project',
                    arguments: [node],
                };
            }
            if (node.kind === 'generatedFile' && node.resourceUri) {
                // Generated output has no file to open; it is fetched from the compilation.
                item.command = {
                    command: 'roslynSense.openVirtualDocument',
                    title: 'Open',
                    arguments: [node.resourceUri],
                };
            }
            if (node.dimmed) {
                item.description = node.description ?? 'not in project';
            }
            // Visual Studio and Rider both mark the project F5 will start; without it the setting
            // is invisible and the only way to learn it is to press F5 and see what happens.
            if (startupProject && projectPathOf(node) && samePath(projectPathOf(node)!, startupProject)
                && node.id.startsWith('project:')) {
                item.description = item.description ? `${item.description} · startup` : 'startup';
            }
            return item;
        },

        getChildren: (node) => fetchChildren(node?.id ?? null),

        getParent(node) {
            // Reveal walks this chain to the root and gives up at the first row that cannot name
            // its parent — and a file id is nothing but its path, so parsing the id alone
            // answered undefined for every file in the tree. That is why revealing a file did
            // nothing at all: not a failed lookup, a parent chain one step long. The listing that
            // produced the node is the record for every id that does not encode its own parent.
            const parentId = parentById.get(node.id) ?? parentIdOf(node.id);
            return parentId ? nodesById.get(parentId) : undefined;
        },
    };

    const view = vscode.window.createTreeView(VIEW_ID, {
        treeDataProvider: provider,
        canSelectMany: true,
        showCollapseAll: true,
        dragAndDropController: {
            dropMimeTypes: [DROP_MIME, URI_LIST_MIME],
            dragMimeTypes: [DROP_MIME],

            handleDrag(source, data) {
                // Only things with somewhere to go: a file or folder has a path, a project has a
                // place in the solution. A Dependencies node has neither.
                const movable = draggableOf(source);
                if (movable.length > 0) {
                    data.set(DROP_MIME, new vscode.DataTransferItem(JSON.stringify(movable)));
                }
            },

            async handleDrop(target, data) {
                if (!target) {
                    return;
                }

                const own = data.get(DROP_MIME);
                if (own) {
                    await dropNodes(target, JSON.parse(String(await own.asString())) as DragItem[]);
                    return;
                }

                const external = data.get(URI_LIST_MIME);
                if (external) {
                    await dropFiles(target, parseUriList(String(await external.asString())));
                }
            },
        },
    });
    context.subscriptions.push(view, changeEmitter);

    /// Refreshing one node re-fetches only its branch, and collapses whatever was open inside it —
    /// which is also the only way to collapse a subtree, since VS Code exposes no collapse API.
    const refresh = (node?: SolutionTreeNode) => changeEmitter.fire(node);

    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration('roslynSense.solutionExplorer.fileIcons')) {
                fileIconsFromTheme = readFileIconSource();
                refresh();
            }
        })
    );

    // The view is built at activation and asked for its roots straight away, which is before the
    // client exists — so the first fetch answered empty, and a TreeDataProvider is never asked
    // again unless it says something changed. That is the whole reason the Solution Explorer came
    // up blank and stayed blank: not slowness, no answer. The roots themselves cost a .sln parse
    // on the server and no MSBuild at all, so this is one cheap re-ask as soon as there is
    // somebody to ask.
    onClientReady(context, getClient, (client) => {
        refresh();

        // Which projects are loaded is drawn on the rows, and the server loads them in the
        // background — so without this the tree keeps saying "loading…" over a solution that
        // finished loading a minute ago, until somebody presses refresh. Through the shared
        // signal rather than the notification, because the settings panel needs it too and
        // `onNotification` would hand it to whichever of them subscribed last.
        context.subscriptions.push(onProjectSetChanged(() => refresh()));

        // And again on every transition into Running. Binding to another solution, or a restart,
        // puts the client through Starting — during which fetchChildren has nothing to ask and
        // answers empty. Without this the view stays empty until the user touches it.
        context.subscriptions.push(
            client.onDidChangeState((e) => {
                if (e.newState === State.Running) {
                    refresh();
                }
            })
        );
    });

    /**
     * The nodes a command should act on.
     *
     * A context menu invokes a command with the clicked node, and with the whole selection when
     * there is more than one. A *keybinding* invokes it with no arguments at all — VS Code has no
     * way to pass the tree's selection through a keystroke — so every shortcut in this view used
     * to throw on `node.id` and do nothing. Reading the selection off the view is what makes the
     * keyboard and the mouse reach the same code.
     */
    const targetsOf = (
        node?: SolutionTreeNode,
        selected?: SolutionTreeNode[]
    ): SolutionTreeNode[] =>
        selected?.length ? selected : node ? [node] : [...view.selection];

    const targetOf = (node?: SolutionTreeNode): SolutionTreeNode | undefined =>
        node ?? view.selection[0];

    /** Registers a command that acts on whatever the tree points at. */
    const onNode = (
        command: string,
        run: (node: SolutionTreeNode, selected: SolutionTreeNode[]) => unknown
    ): vscode.Disposable =>
        vscode.commands.registerCommand(
            command,
            (node?: SolutionTreeNode, selected?: SolutionTreeNode[]) => {
                const targets = targetsOf(node, selected);
                return targets.length > 0 ? run(targets[0], targets) : undefined;
            }
        );

    /// Runs one tree edit and applies whatever namespace fixups it implies. The edits come back
    /// rather than being written server-side so an open, unsaved file is changed in its buffer.
    ///
    /// Also where undo is recorded, rather than at each of the fifteen commands that call it: the
    /// inverse of an edit is a property of the edit, and a command that has to remember to record
    /// its own is a command that will one day forget. `record: false` is for the replays undo and
    /// redo perform, which must not push history of their own.
    async function edit(
        params: Record<string, unknown>,
        options: { record?: boolean; quiet?: boolean } = {}
    ): Promise<TreeEditResult | undefined> {
        const client = getClient();
        if (!client) {
            return undefined;
        }

        // Before the edit runs: what "delete" undoes to is gone by the time it returns.
        const plan = options.record === false ? undefined : await planUndo(params);

        const result = await client.sendRequest<TreeEditResult>(
            'roslynSense/solutionTreeEdit', params);

        if (!result.ok) {
            if (!options.quiet) {
                void vscode.window.showErrorMessage(result.message);
            }
            return result;
        }

        if (result.edit) {
            await vscode.workspace.applyEdit(
                await client.protocol2CodeConverter.asWorkspaceEdit(result.edit));
        }

        const step = plan?.(result);
        if (step) {
            remember(step);
        }
        return result;
    }

    /// One NuGet operation, with the progress notification the NuGet view shows for the same
    /// work. Returns whether it succeeded, because an undo step must not be recorded for a
    /// removal that did not happen.
    async function nuget(
        method: 'install' | 'uninstall',
        entry: { id: string; version?: string; project: string },
        title: string
    ): Promise<boolean> {
        const client = getClient();
        if (!client) {
            return false;
        }

        const result = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title },
            () =>
                client.sendRequest<{ success: boolean; message: string }>(
                    `roslynSense/nuget/${method}`,
                    { id: entry.id, version: entry.version, projectPaths: [entry.project] }
                )
        );

        if (!result?.success) {
            void vscode.window.showErrorMessage(result?.message ?? `Could not ${method} ${entry.id}.`);
            return false;
        }
        return true;
    }

    /// An edit replayed by undo or redo: no history of its own, and a failure raised rather than
    /// shown, so the undo command reports "could not undo" instead of claiming it undid something
    /// it did not. Silent at the edit layer for the same reason — one failure, one message.
    async function replay(params: Record<string, unknown>): Promise<void> {
        const result = await edit(params, { record: false, quiet: true });
        if (!result?.ok) {
            throw new Error(result?.message ?? 'the server did not apply the change');
        }
    }

    /// A replay whose failure is not a failure of the undo. Putting a restored file back in its
    /// project is the case: under an SDK-style glob the item is already there, and the edit
    /// declining is the correct outcome rather than a reason to tell the user undo broke.
    async function replayIfPossible(params: Record<string, unknown>): Promise<void> {
        await edit(params, { record: false, quiet: true });
    }

    // ---- Undo ------------------------------------------------------------------------------

    const history = new UndoStack();

    /// Steps recorded while a batch is open, so a multi-select delete is one Ctrl+Z rather than
    /// four. Null when nothing is batching, which is the case for a single command.
    let batch: UndoStep[] | null = null;

    function remember(step: UndoStep): void {
        if (batch) {
            batch.push(step);
        } else {
            history.push(step);
        }
    }

    /// Everything the callback edits becomes one undo step. A nested batch joins the outer one, so
    /// a command calling another command's helper cannot split its own step in two.
    async function asOneStep<T>(label: string, run: () => Promise<T>): Promise<T> {
        if (batch) {
            return run();
        }

        const collected: UndoStep[] = [];
        batch = collected;
        try {
            return await run();
        } finally {
            batch = null;
            if (collected.length === 1) {
                history.push(collected[0]);
            } else if (collected.length > 1) {
                history.push(composite(label, collected));
            }
        }
    }

    /**
     * The inverse of an edit, decided before it runs.
     *
     * Returns a function rather than a step because half of these need the result — an added file
     * is undone by deleting the path the server chose for it, which nothing knows until it
     * answers. Returning undefined means "not undoable", and that is a deliberate answer for every
     * action whose inverse would be approximate: undoing an edit into something *close to* the
     * previous state is worse than not offering to undo it at all, because the difference does not
     * show up until much later.
     */
    async function planUndo(
        params: Record<string, unknown>
    ): Promise<((result: TreeEditResult) => UndoStep | undefined) | undefined> {
        const action = params.action as string;
        const uri = params.targetUri as string | undefined;

        const opposites: Record<string, string> = {
            addProjectReference: 'removeProjectReference',
            removeProjectReference: 'addProjectReference',
            excludeFile: 'includeExistingFile',
            includeExistingFile: 'excludeFile',
        };

        // Symmetric pairs: each is exactly the other's inverse, arguments and all.
        if (opposites[action]) {
            const label = describeEdit(action, params);
            return () => ({
                label,
                undo: async () => {
                    await replay({ ...params, action: opposites[action] });
                },
                redo: async () => {
                    await replay(params);
                },
            });
        }

        switch (action) {
            // Something new on disk. Undoing it captures what it holds first — a file created an
            // hour ago and typed into since is not the empty file the template wrote.
            case 'addFile':
            case 'addFolder':
            case 'copy':
                return (result) =>
                    result.uri
                        ? removalStep(describeEdit(action, params), vscode.Uri.parse(result.uri))
                        : undefined;

            // A rename replayed backwards is a rename, which matters: it re-runs the type and
            // namespace fixups in reverse rather than leaving the file called one thing and the
            // class inside it called another.
            case 'rename':
                return (result) =>
                    uri && result.uri
                        ? {
                              label: `Rename ${basenameOfUri(uri)}`,
                              undo: async () => {
                                  await replay({
                                      action: 'rename',
                                      targetUri: result.uri,
                                      name: basenameOfUri(uri),
                                  });
                              },
                              redo: async () => {
                                  await replay(params);
                              },
                          }
                        : undefined;

            case 'move':
                return (result) =>
                    uri && result.uri
                        ? {
                              label: `Move ${basenameOfUri(uri)}`,
                              undo: async () => {
                                  await replay({
                                      action: 'move',
                                      targetUri: result.uri,
                                      destinationUri: parentUriOf(uri),
                                  });
                              },
                              redo: async () => {
                                  await replay(params);
                              },
                          }
                        : undefined;

            case 'delete': {
                if (!uri) {
                    return undefined;
                }
                const captured = await snapshot(vscode.Uri.parse(uri));
                if (!captured) {
                    // Missing, or larger than the snapshot budget. No step at all rather than one
                    // that would restore an empty file over whatever used to be there.
                    return undefined;
                }
                return () => ({
                    label: `Delete ${basenameOfUri(uri)}`,
                    undo: async () => {
                        await restore(captured);
                        // Back on disk is not back in the project: an .aspx or a .resx is listed
                        // explicitly, and a project that lost its item keeps compiling without it.
                        await replayIfPossible({ action: 'includeExistingFile', targetUri: uri });
                    },
                    redo: async () => {
                        await replay(params);
                    },
                });
            }

            default:
                return undefined;
        }
    }

    /// Undo for something that was created: capture it, delete it, and put the capture back on
    /// redo. The capture is taken at undo time rather than at creation time, so whatever was typed
    /// into the file in between survives the round trip.
    function removalStep(label: string, target: vscode.Uri): UndoStep {
        let captured: Snapshot | undefined;
        return {
            label,
            undo: async () => {
                captured = await snapshot(target);
                await replay({ action: 'delete', targetUri: target.toString() });
            },
            redo: async () => {
                if (captured) {
                    await restore(captured);
                    await replayIfPossible({
                        action: 'includeExistingFile',
                        targetUri: target.toString(),
                    });
                }
            },
        };
    }


    /**
     * What a drop from inside the tree means, decided by what was picked up and what it landed on.
     *
     * A project dropped on a solution folder is an edit to the solution file and nothing moves on
     * disk; a file dropped on a project folder is the exact opposite. Routing both through one
     * `move` action is what used to make dragging a project try to relocate its `.csproj`.
     */
    async function dropNodes(
        target: SolutionTreeNode,
        items: DragItem[],
        intent?: 'move' | 'copy'
    ): Promise<void> {
        const folderId = solutionFolderIdOf(target);
        const solutionUri = solutionUriOf();

        for (const item of items) {
            if (item.id.startsWith('project:')) {
                // A project only ever moves between solution folders, and there is no such thing
                // as copying one into a second folder — the solution lists it once.
                if (folderId === undefined || !solutionUri || intent === 'copy') {
                    continue;
                }
                await edit({
                    action: 'moveProject',
                    targetUri: vscode.Uri.file(item.id.slice('project:'.length)).toString(),
                    destinationUri: solutionUri,
                    projectPath: folderId,
                    name: target.label,
                });
                continue;
            }

            if (!item.resourceUri) {
                continue;
            }

            // A solution folder holds references to files, not the files themselves — and not
            // directories, which the solution format has no way to carry.
            if (folderId !== undefined) {
                if (folderId && solutionUri && !item.id.startsWith('folder:')) {
                    await edit({
                        action: 'addSolutionItem',
                        targetUri: item.resourceUri,
                        destinationUri: solutionUri,
                        projectPath: folderId,
                    });

                    // Dragging an item from one solution folder to another moves it, the way
                    // dragging anything else does. Without this it would be listed twice.
                    const from = solutionItemFolderOf(item.id);
                    if (from !== undefined && from !== folderId && intent !== 'copy') {
                        await edit({
                            action: 'removeSolutionItem',
                            targetUri: item.resourceUri,
                            destinationUri: solutionUri,
                            projectPath: from,
                        });
                    }
                }
                continue;
            }

            const destination = containerUriOf(target);
            if (!destination) {
                continue;
            }

            const action = intent ?? (await moveOrCopy(item.resourceUri, target));
            if (action) {
                await edit({ action, targetUri: item.resourceUri, destinationUri: destination });
            }
        }

        refresh();
    }

    /**
     * Whether a cross-project drop should move or copy.
     *
     * Visual Studio and Rider decide this from whether Ctrl is held, and VS Code's tree drag API
     * reports no modifier state at all — so the question gets asked instead, and only when the
     * answer is not obvious. Within one project a drag is a move, the way it is everywhere else.
     */
    async function moveOrCopy(
        sourceUri: string,
        target: SolutionTreeNode
    ): Promise<'move' | 'copy' | undefined> {
        const project = projectPathOf(target);
        const source = vscode.Uri.parse(sourceUri).fsPath;
        if (!project || isUnder(source, Path.dirname(project))) {
            return 'move';
        }

        const picked = await vscode.window.showQuickPick(
            [
                { label: 'Move', detail: 'Take it out of its current project', action: 'move' as const },
                { label: 'Copy', detail: 'Leave the original where it is', action: 'copy' as const },
            ],
            { title: `${Path.basename(source)} → ${Path.basename(project, Path.extname(project))}` }
        );
        return picked?.action;
    }

    /**
     * Files dragged in from outside the tree.
     *
     * Onto a solution folder they are referenced where they lie, which is what a solution item is.
     * Onto a project they are copied in, because a project compiles what is inside it — unless the
     * file is already inside it, which is the "Add Existing Item" case for a file that is on disk
     * but excluded. Copying that one would only produce "X copy.cs" beside the original.
     */
    async function dropFiles(target: SolutionTreeNode, uris: vscode.Uri[]): Promise<void> {
        const folderId = solutionFolderIdOf(target);
        const solutionUri = solutionUriOf();
        const destination = containerUriOf(target);

        for (const uri of uris) {
            if (folderId !== undefined) {
                if (folderId && solutionUri) {
                    await edit({
                        action: 'addSolutionItem',
                        targetUri: uri.toString(),
                        destinationUri: solutionUri,
                        projectPath: folderId,
                    });
                }
                continue;
            }
            if (!destination) {
                continue;
            }

            const owner = projectPathOf(target) ?? projectPathOf(parentOf(target));
            const inside = owner && isUnder(uri.fsPath, Path.dirname(owner));
            await edit(
                inside
                    ? { action: 'includeExistingFile', targetUri: uri.toString() }
                    : { action: 'copy', targetUri: uri.toString(), destinationUri: destination }
            );
        }

        refresh();
    }

    /// Expands each ancestor the server names, then selects the file. Without this, revealing
    /// only works for a file whose branch the user already happened to expand.
    async function revealUri(uri: vscode.Uri): Promise<boolean> {
        const chain = await revealChain(uri);
        return chain.length > 0 && walkAndReveal(chain);
    }

    /// The ids from the solution root down to what the uri names, or empty when the server
    /// cannot place it. Separate from the walk so a caller can cut the chain short — going to a
    /// project wants the rows above it expanded and nothing below it.
    async function revealChain(uri: vscode.Uri): Promise<string[]> {
        const client = getClient();
        if (!client) {
            return [];
        }

        try {
            const result = await client.sendRequest<{ path: string[] }>(
                'roslynSense/solutionTreeReveal',
                { uri: uri.toString(), fileNesting: state.fileNesting });
            return result?.path ?? [];
        } catch {
            return [];
        }
    }

    async function walkAndReveal(chain: string[]): Promise<boolean> {
        // Walk the chain listing each level, so `parentById` knows who listed every node on the
        // way down. A cached node whose parent is unrecorded — or recorded as somebody else, which
        // happens for a project reference listed under two projects — is re-fetched rather than
        // trusted, because `getParent` is about to be asked the same question.
        //
        // A level the tree does not draw stops the walk but not the reveal: the file may be hidden
        // by "Show All Files" being off, or nested under a row the current settings put elsewhere.
        // Landing on the folder that holds it is the useful answer there, and infinitely better
        // than telling the user the file is not in their solution.
        let parent: SolutionTreeNode | undefined;
        for (const id of chain) {
            const cached = nodesById.get(id);
            const node =
                cached && (!parent || parentById.get(id) === parent.id)
                    ? cached
                    : (await fetchChildren(parent?.id ?? null)).find((child) => child.id === id);
            if (!node) {
                break;
            }
            parent = node;
        }

        if (!parent) {
            return false;
        }

        // Revealing can still fail — a row VS Code has since dropped from its own model, most of
        // all — and an unhandled rejection here would take the command's fallback message with it.
        try {
            await view.reveal(parent, { select: true, focus: false, expand: true });
        } catch (error) {
            log.appendLine(
                `reveal(${parent.id}) failed: ${
                    error instanceof Error ? error.message : String(error)
                }`
            );
            return false;
        }
        return true;
    }

    /**
     * Puts the tree on a project's own row — where "go to" from a reference lands.
     *
     * Built on the same server-supplied chain as revealUri and then cut short at the project,
     * rather than asking for a second kind of chain: the walk below the project is what expands
     * folders, and a project row wants none of that. The .csproj is what the chain is asked
     * about because the reveal request answers about files, and a project file is the one file
     * that is always inside exactly the project it belongs to.
     */
    async function revealProject(projectPath: string): Promise<boolean> {
        const chain = await revealChain(vscode.Uri.file(projectPath));
        const stop = chain.findIndex((id) => id.startsWith('project:'));
        return stop < 0 ? false : walkAndReveal(chain.slice(0, stop + 1));
    }

    /** Puts the tree on the file being edited, if the solution has anywhere to put it. */
    async function revealActiveEditor(): Promise<boolean> {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.uri.scheme !== 'file') {
            return false;
        }
        return revealUri(editor.document.uri);
    }

    /**
     * Starts a project, with or without the debugger attached.
     *
     * Both go through the same debug configuration: `noDebug` is what VS Code's own
     * "Run Without Debugging" sets, so the launch target, build step and launch profile are
     * resolved identically either way rather than by a second, parallel code path.
     */
    async function launch(node: SolutionTreeNode, options: { debug: boolean }): Promise<void> {
        const projectPath = projectPathOf(node);
        if (!projectPath) {
            return;
        }

        // Running a project makes it the startup project, so the badge has to follow — otherwise
        // it keeps pointing at whatever was started before, which is worse than not showing it.
        startupProject = projectPath;
        await context.workspaceState.update('roslynSense.startupProject', projectPath);
        refresh();

        await vscode.debug.startDebugging(
            undefined,
            {
                type: 'roslynsense',
                request: 'launch',
                name: `C#: ${Path.basename(projectPath, Path.extname(projectPath))}`,
                projectPath,
            },
            { noDebug: !options.debug }
        );
    }

    const setUnloaded = async (paths: string[]) => {
        // Deduplicated on the way in: unloading an already-unloaded project is a no-op, and a
        // list with it twice would need removing twice.
        unloaded = paths.filter(
            (path, index) => paths.findIndex((other) => samePath(other, path)) === index);
        await context.workspaceState.update('roslynSense.unloadedProjects', unloaded);
        refresh();
    };

    async function buildSolution(
        node: SolutionTreeNode,
        target: 'build' | 'rebuild' | 'clean'
    ): Promise<void> {
        const client = getClient();
        const solutionUri = solutionUriOf();
        if (!client || !solutionUri) {
            return;
        }

        const titles = { build: 'Building', rebuild: 'Rebuilding', clean: 'Cleaning' };
        await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Window, title: `${titles[target]} ${node.label}…` },
            () =>
                client.sendRequest('workspace/executeCommand', {
                    command: 'roslynSense.build',
                    arguments: [vscode.Uri.parse(solutionUri).fsPath, 'Debug', target],
                })
        );
    }

    /**
     * The node one level up.
     *
     * Some ids carry their parent and some do not — a project reference is `project:<path>` no
     * matter which project references it — so this is remembered from the listing that produced
     * the node rather than parsed out of its id.
     */
    function parentOf(node: SolutionTreeNode): SolutionTreeNode | undefined {
        const parentId = parentById.get(node.id) ?? parentIdOf(node.id);
        return parentId ? nodesById.get(parentId) : undefined;
    }

    /** Which solution folder a solution item hangs off. */
    function solutionFolderOf(node: SolutionTreeNode): string | undefined {
        const own = solutionItemFolderOf(node.id);
        if (own !== undefined) {
            return own;
        }
        const parent = parentById.get(node.id);
        return parent?.startsWith('slnfolder:') ? parent.slice('slnfolder:'.length) : undefined;
    }

    const setToggle = async (key: keyof ViewState, value: boolean) => {
        state[key] = value;
        await context.workspaceState.update(`roslynSense.${key}`, value);
        await vscode.commands.executeCommand('setContext', `roslynSense.${key}`, value);
        refresh();
    };

    // Seed the context keys so the title-bar toggles render in the right state on activation.
    void vscode.commands.executeCommand('setContext', 'roslynSense.showAllFiles', state.showAllFiles);
    void vscode.commands.executeCommand('setContext', 'roslynSense.showIgnored', state.showIgnored);
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.revealActiveFile', state.revealActiveFile);
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.fileNesting', state.fileNesting);

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.solutionExplorer.refresh', refresh),

        // Bound to Ctrl+Z / Ctrl+Y only while this view has focus — see the keybindings in
        // package.json. Scoped that way because the editor's own undo means something else
        // entirely, and stealing the chord from a focused editor would be indefensible.
        vscode.commands.registerCommand('roslynSense.solutionExplorer.undo', async () => {
            if (!history.canUndo) {
                void vscode.window.showInformationMessage('Nothing to undo in the Solution Explorer.');
                return;
            }
            try {
                const label = await history.undo();
                refresh();
                void vscode.window.setStatusBarMessage(`Undid: ${label}`, 3000);
            } catch (error) {
                void vscode.window.showErrorMessage(
                    `Could not undo: ${error instanceof Error ? error.message : String(error)}`
                );
                refresh();
            }
        }),

        vscode.commands.registerCommand('roslynSense.solutionExplorer.redo', async () => {
            if (!history.canRedo) {
                void vscode.window.showInformationMessage('Nothing to redo in the Solution Explorer.');
                return;
            }
            try {
                const label = await history.redo();
                refresh();
                void vscode.window.setStatusBarMessage(`Redid: ${label}`, 3000);
            } catch (error) {
                void vscode.window.showErrorMessage(
                    `Could not redo: ${error instanceof Error ? error.message : String(error)}`
                );
                refresh();
            }
        }),

        vscode.commands.registerCommand('roslynSense.solutionExplorer.showAllFiles', () =>
            setToggle('showAllFiles', true)
        ),
        vscode.commands.registerCommand('roslynSense.solutionExplorer.hideNonProjectFiles', () =>
            setToggle('showAllFiles', false)
        ),
        vscode.commands.registerCommand('roslynSense.solutionExplorer.showIgnored', () =>
            setToggle('showIgnored', true)
        ),
        vscode.commands.registerCommand('roslynSense.solutionExplorer.hideIgnored', () =>
            setToggle('showIgnored', false)
        ),
        // Focus is the one-shot: put the tree on the file I am editing, now. Following is the
        // same thing standing — and turning it on has to move the tree straight away, because a
        // toggle whose only effect is on the *next* editor change reads as a button that does
        // nothing at all.
        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.focusCurrentFile',
            async () => {
                // A filter replaces the root listing with what matched it, so there is no chain
                // from the solution down to anything while one is on. Asking to be taken to the
                // current file is asking to stop looking at a filtered tree.
                if (filter) {
                    filter = undefined;
                    view.message = undefined;
                    refresh();
                }

                if (await revealActiveEditor()) {
                    return;
                }
                // Saying nothing here is what made this look broken rather than inapplicable.
                const editor = vscode.window.activeTextEditor;
                void vscode.window.showInformationMessage(
                    editor
                        ? `${Path.basename(editor.document.uri.fsPath)} is not part of the open ` +
                          'solution, so there is nowhere in the tree to select.'
                        : 'No file is open.'
                );
            }
        ),
        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.followCurrentFile',
            async () => {
                await setToggle('revealActiveFile', true);
                await revealActiveEditor();
            }
        ),
        vscode.commands.registerCommand('roslynSense.solutionExplorer.unfollowCurrentFile', () =>
            setToggle('revealActiveFile', false)
        ),
        vscode.commands.registerCommand('roslynSense.solutionExplorer.revealActiveFile', () =>
            vscode.commands.executeCommand(
                state.revealActiveFile
                    ? 'roslynSense.solutionExplorer.unfollowCurrentFile'
                    : 'roslynSense.solutionExplorer.followCurrentFile'
            )
        ),
        vscode.commands.registerCommand('roslynSense.solutionExplorer.toggleFileNesting', () =>
            setToggle('fileNesting', !state.fileNesting)
        ),

        vscode.commands.registerCommand('roslynSense.solutionExplorer.goToNode', () =>
            searchSolution(getClient, view)
        ),

        vscode.commands.registerCommand('roslynSense.solutionExplorer.filter', () => {
            // A QuickPick used as a live filter box: every keystroke narrows the tree behind
            // it. VS Code has no way to put an input inside a TreeView, and its own find widget
            // does not reach contributed views, so this is as close to a Rider speed search as
            // the API allows — incremental, and over the whole solution rather than the rows
            // that happen to be expanded.
            const box = vscode.window.createQuickPick();
            box.title = 'Filter the solution';
            box.placeholder = 'Type to narrow the tree — Enter keeps it, Escape clears it';
            box.value = filter ?? '';

            let pending: NodeJS.Timeout | undefined;
            let committed = false;

            const apply = (value: string) => {
                clearTimeout(pending);
                pending = setTimeout(() => {
                    filter = value.trim() ? value.trim() : undefined;
                    view.message = filter ? `Filtering by “${filter}”` : undefined;
                    refresh();
                }, 120);
            };

            box.onDidChangeValue(apply);
            box.onDidAccept(() => {
                committed = true;
                box.hide();
            });
            box.onDidHide(() => {
                clearTimeout(pending);
                if (!committed) {
                    filter = undefined;
                    view.message = undefined;
                    refresh();
                }
                box.dispose();
            });

            box.show();
        }),
        vscode.commands.registerCommand('roslynSense.solutionExplorer.clearFilter', () => {
            filter = undefined;
            view.message = undefined;
            refresh();
        }),

        onNode('roslynSense.solutionExplorer.revealInExplorer', (node) => {
            if (node.resourceUri) {
                void vscode.commands.executeCommand(
                    'revealFileInOS', vscode.Uri.parse(node.resourceUri));
            }
        }),
        onNode('roslynSense.solutionExplorer.findInFolder', (node) => {
            if (node.resourceUri) {
                void vscode.commands.executeCommand('workbench.action.findInFiles', {
                    filesToInclude: vscode.Uri.parse(node.resourceUri).fsPath,
                });
            }
        }),
        onNode('roslynSense.solutionExplorer.openProjectFile', (node) => {
            const target = node.id.startsWith('project:')
                ? vscode.Uri.file(node.id.slice('project:'.length))
                : node.resourceUri
                  ? vscode.Uri.parse(node.resourceUri)
                  : undefined;
            if (target) {
                void vscode.window.showTextDocument(target);
            }
        }),
        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.newFile',
            async (clicked: SolutionTreeNode | undefined, second?: unknown) => {
                const node = targetOf(clicked);
                if (!node) {
                    return;
                }
                // The second argument is a kind when the New Item menu supplies one, and the rest
                // of the selection when the context menu invokes this with several rows selected.
                const preselectedKind = typeof second === 'string' ? second : undefined;
                const name = await vscode.window.showInputBox({
                    title: 'New file',
                    prompt: 'File name, with or without an extension',
                    placeHolder: 'OrderTotal.cs',
                });
                if (!name) {
                    return;
                }
                const kind =
                    preselectedKind ??
                    (Path.extname(name) && Path.extname(name) !== '.cs'
                        ? 'empty'
                        : await vscode.window.showQuickPick(
                            ['class', 'interface', 'record', 'enum', 'empty'],
                            { title: 'What should it contain?' }));
                if (!kind) {
                    return;
                }

                const result = await edit({
                    action: 'addFile',
                    targetUri: containerUriOf(node),
                    projectPath: projectPathOf(node),
                    name,
                    kind,
                });
                // Only the folder it landed in changed; rebuilding the whole tree would collapse
                // every branch the user had open to get here.
                refresh(node);
                if (result?.ok && result.uri) {
                    await vscode.window.showTextDocument(vscode.Uri.parse(result.uri));
                }
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.addProjectReference',
            async (node) => {
                const client = getClient();
                const projectPath = projectPathOf(node);
                if (!client || !projectPath) {
                    return;
                }

                const projects = await client.sendRequest<{ path: string; name: string }[]>(
                    'roslynSense/solutionProjects'
                );
                const others = projects.filter((p) => !samePath(p.path, projectPath));
                if (others.length === 0) {
                    void vscode.window.showInformationMessage(
                        'There are no other projects in this solution to reference.');
                    return;
                }

                const picked = await vscode.window.showQuickPick(
                    others.map((p) => ({ label: p.name, description: p.path, project: p })),
                    { title: 'Add project reference', canPickMany: true }
                );
                if (!picked?.length) {
                    return;
                }

                for (const choice of picked) {
                    await edit({
                        action: 'addProjectReference',
                        projectPath,
                        destinationUri: vscode.Uri.file(choice.project.path).toString(),
                    });
                }
                refresh();
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.addAssemblyReference',
            async (node) => {
                const client = getClient();
                const projectPath = projectPathOf(node);
                if (!client || !projectPath) {
                    return;
                }

                const assemblies = await client.sendRequest<string[]>(
                    'roslynSense/assemblyReferences',
                    { query: projectPath, limit: 0 }
                );

                if (assemblies.length === 0) {
                    // The menu only offers this on a .NET Framework project, so reaching here
                    // means the reference assemblies for its target framework are not installed.
                    void vscode.window.showErrorMessage(
                        'No reference assemblies were found for this project’s target framework. ' +
                        'Install the matching .NET Framework targeting pack.');
                    return;
                }

                const picked = await vscode.window.showQuickPick(assemblies, {
                    title: 'Add reference',
                    placeHolder: 'System.ServiceModel',
                    canPickMany: true,
                });
                if (!picked?.length) {
                    return;
                }

                for (const assembly of picked) {
                    await edit({ action: 'addAssemblyReference', projectPath, name: assembly });
                }
                refresh();
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.newProject',
            async (node) => {
                const client = getClient();
                const solutionUri = node.id.startsWith('solution:')
                    ? vscode.Uri.file(node.id.slice('solution:'.length)).toString()
                    : solutionUriOf();
                if (!client || !solutionUri) {
                    return;
                }

                const choices = await vscode.window.withProgress(
                    { location: vscode.ProgressLocation.Window, title: 'Reading templates…' },
                    () => client.sendRequest<{
                        templates: { name: string; shortName: string; tags: string }[];
                        targetFrameworks: string[];
                    }>('roslynSense/projectTemplates')
                );

                if (!choices.templates.length) {
                    void vscode.window.showErrorMessage(
                        'No project templates were found. Is the .NET SDK on PATH?');
                    return;
                }

                const template = await vscode.window.showQuickPick(
                    choices.templates.map((t) => ({
                        label: t.name,
                        description: t.shortName,
                        detail: t.tags || undefined,
                        shortName: t.shortName,
                    })),
                    { title: 'New project', matchOnDescription: true, matchOnDetail: true }
                );
                if (!template) {
                    return;
                }

                const name = await vscode.window.showInputBox({
                    title: 'Project name',
                    placeHolder: 'Contoso.Widgets',
                });
                if (!name) {
                    return;
                }

                const framework = await vscode.window.showQuickPick(
                    [
                        { label: 'Template default', framework: undefined as string | undefined },
                        ...choices.targetFrameworks.map((f) => ({ label: f, framework: f })),
                    ],
                    { title: 'Target framework' }
                );
                if (!framework) {
                    return;
                }

                const result = await vscode.window.withProgress(
                    { location: vscode.ProgressLocation.Window, title: `Creating ${name}…` },
                    () =>
                        edit({
                            action: 'addProject',
                            targetUri: solutionUri,
                            // The solution folder to nest under, when invoked on one.
                            projectPath: node.id.startsWith('slnfolder:')
                                ? node.id.slice('slnfolder:'.length)
                                : undefined,
                            name,
                            kind: template.shortName,
                            targetFramework: framework.framework,
                        })
                );

                refresh();
                if (result?.ok && result.uri) {
                    await vscode.window.showTextDocument(vscode.Uri.parse(result.uri));
                }
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.newSolutionFolder',
            async (node) => {
                const name = await vscode.window.showInputBox({
                    title: 'New solution folder',
                    prompt: 'A grouping inside the solution file — not a directory on disk',
                });
                if (!name) {
                    return;
                }

                // Both ids carry what the server needs: the solution path for a root-level
                // folder, and the parent folder's own id when nesting one inside another.
                const solutionUri = node.id.startsWith('solution:')
                    ? vscode.Uri.file(node.id.slice('solution:'.length)).toString()
                    : solutionUriOf();
                const parentId = node.id.startsWith('slnfolder:')
                    ? node.id.slice('slnfolder:'.length)
                    : undefined;

                if (!solutionUri) {
                    return;
                }
                await edit({
                    action: 'addSolutionFolder',
                    targetUri: solutionUri,
                    projectPath: parentId,
                    name,
                });
                refresh();
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.newFolder',
            async (node) => {
                const name = await vscode.window.showInputBox({
                    title: 'New folder',
                    prompt: 'Folder name',
                });
                if (name) {
                    await edit({ action: 'addFolder', targetUri: containerUriOf(node), name });
                    refresh(node);
                }
            }
        ),
        // One Rename, doing whatever renaming means for the row it was pressed on — a solution
        // folder has no file to rename, and F2 landing on nothing is worse than no F2 at all.
        onNode(
            'roslynSense.solutionExplorer.rename',
            async (node) => {
                if (node.id.startsWith('slnfolder:')) {
                    const name = await vscode.window.showInputBox({
                        title: 'Rename solution folder',
                        value: node.label,
                    });
                    if (name && name !== node.label) {
                        await edit({
                            action: 'renameSolutionFolder',
                            targetUri: solutionUriOf(),
                            projectPath: node.id.slice('slnfolder:'.length),
                            name,
                        });
                        refresh();
                    }
                    return;
                }

                if (!node.resourceUri) {
                    return;
                }
                const current = Path.basename(vscode.Uri.parse(node.resourceUri).fsPath);
                const name = await vscode.window.showInputBox({
                    title: 'Rename',
                    value: current,
                    valueSelection: [0, current.lastIndexOf('.') > 0 ? current.lastIndexOf('.') : current.length],
                });
                if (!name || name === current) {
                    return;
                }
                await edit({ action: 'rename', targetUri: node.resourceUri, name });
                refresh(parentOf(node));
            }
        ),
        // Likewise Delete: a solution item is detached, a solution folder is dissolved, a project
        // leaves the solution, and only a real file is actually deleted.
        onNode(
            'roslynSense.solutionExplorer.delete',
            async (_node, selected) => {
                const targets = selected.filter(
                    (n) => n.resourceUri || n.id.startsWith('slnfolder:'));
                if (targets.length === 0) {
                    return;
                }

                // Deleting from a tree is easy to do by accident and impossible to undo, so the
                // prompt says exactly which of the four things is about to happen.
                const confirmed = await vscode.window.showWarningMessage(
                    describeDeleteAll(targets),
                    { modal: true },
                    'Delete'
                );
                if (confirmed !== 'Delete') {
                    return;
                }

                const solutionUri = solutionUriOf();
                await asOneStep(`Delete ${targets.length} items`, async () => {
                for (const target of targets) {
                    if (target.id.startsWith('slnfolder:')) {
                        await edit({
                            action: 'removeSolutionFolder',
                            targetUri: solutionUri,
                            projectPath: target.id.slice('slnfolder:'.length),
                        });
                    } else if (target.kind === 'solutionItem') {
                        await edit({
                            action: 'removeSolutionItem',
                            targetUri: target.resourceUri,
                            destinationUri: solutionUri,
                            projectPath: solutionFolderOf(target),
                        });
                    } else if (target.id.startsWith('project:')) {
                        await edit({
                            action: 'removeProject',
                            targetUri: target.resourceUri,
                            destinationUri: solutionUri,
                        });
                    } else {
                        await edit({ action: 'delete', targetUri: target.resourceUri });
                    }
                }
                });
                refresh();
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.addExistingItem',
            async (node) => {
                const folderId = solutionFolderIdOf(node);
                const picked = await vscode.window.showOpenDialog({
                    title: folderId
                        ? `Add existing item to ${node.label}`
                        : 'Add existing item',
                    canSelectMany: true,
                    openLabel: 'Add',
                    defaultUri: defaultDialogUri(node),
                });
                if (!picked?.length) {
                    return;
                }
                await dropFiles(node, picked);
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.addExistingProject',
            async (node) => {
                const solutionUri = solutionUriOf();
                if (!solutionUri) {
                    return;
                }
                const picked = await vscode.window.showOpenDialog({
                    title: 'Add existing project',
                    canSelectMany: true,
                    openLabel: 'Add',
                    filters: { 'Project files': ['csproj', 'vbproj', 'fsproj'] },
                    defaultUri: vscode.Uri.file(Path.dirname(vscode.Uri.parse(solutionUri).fsPath)),
                });
                if (!picked?.length) {
                    return;
                }

                for (const project of picked) {
                    await edit({
                        action: 'addExistingProject',
                        targetUri: solutionUri,
                        destinationUri: project.toString(),
                        projectPath: node.id.startsWith('slnfolder:')
                            ? node.id.slice('slnfolder:'.length)
                            : undefined,
                    });
                }
                refresh();
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.excludeFile',
            async (_node, selected) => {
                const uris = urisOf(selected);
                await asOneStep(`Exclude ${uris.length} files`, async () => {
                    for (const uri of uris) {
                        await edit({ action: 'excludeFile', targetUri: uri });
                    }
                });
                refresh(parentOf(selected[0]));
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.removeReference',
            async (_node, selected) => {
                const references = selected
                    .map((node) => ({
                        node,
                        // The id names the owner; parentOf is the fallback for a row that arrived
                        // without a recorded parent, such as one picked out of the filter results.
                        owner: referenceOwnerOf(node.id) ?? projectPathOf(parentOf(node)),
                    }))
                    .filter((entry) => entry.owner && entry.node.resourceUri);
                if (references.length === 0) {
                    return;
                }

                const first = references[0];
                const confirmed = await vscode.window.showWarningMessage(
                    references.length === 1
                        ? `Remove the reference to ${first.node.label} from ${nameOf(first.owner!)}?`
                        : `Remove ${references.length} project references?`,
                    { modal: true },
                    'Remove'
                );
                if (confirmed !== 'Remove') {
                    return;
                }

                await asOneStep(`Remove ${references.length} project references`, async () => {
                    for (const entry of references) {
                        await edit({
                            action: 'removeProjectReference',
                            projectPath: entry.owner,
                            destinationUri: entry.node.resourceUri,
                        });
                    }
                });
                refresh();
            }
        ),
        // Removing a package is NuGet's own operation rather than a project-file edit, so it goes
        // through the same server request the NuGet view uses — that is what keeps the restore,
        // the lock file and Central Package Management moving with it.
        onNode('roslynSense.solutionExplorer.removePackage', async (_node, selected) => {
            const client = getClient();
            const packages = selected
                .filter((node) => node.kind === 'package' && projectPathOf(node))
                .map((node) => ({
                    id: node.label,
                    // The row already carries the version — "13.0.3", or "13.0.3 (central)" under
                    // Central Package Management — and it is what undo has to put back.
                    version: node.description?.split(' ')[0],
                    project: projectPathOf(node)!,
                }));
            if (!client || packages.length === 0) {
                return;
            }

            const confirmed = await vscode.window.showWarningMessage(
                packages.length === 1
                    ? `Remove ${packages[0].id} from ${nameOf(packages[0].project)}?`
                    : `Remove ${packages.length} packages?`,
                { modal: true },
                'Remove'
            );
            if (confirmed !== 'Remove') {
                return;
            }

            await asOneStep(`Remove ${packages.length} packages`, async () => {
                for (const entry of packages) {
                    const removed = await nuget(
                        'uninstall',
                        { id: entry.id, project: entry.project },
                        `Removing ${entry.id}…`
                    );
                    if (!removed) {
                        continue;
                    }

                    // Undo puts the same version back, not "the latest" — restoring a package at a
                    // different version than the one that was there is a different project, and it
                    // is the kind of difference that only shows up at build time.
                    remember({
                        label: `Remove ${entry.id}`,
                        undo: async () => {
                            if (!(await nuget('install', entry, `Restoring ${entry.id}…`))) {
                                throw new Error(`${entry.id} could not be restored`);
                            }
                        },
                        redo: async () => {
                            if (
                                !(await nuget(
                                    'uninstall',
                                    { id: entry.id, project: entry.project },
                                    `Removing ${entry.id}…`
                                ))
                            ) {
                                throw new Error(`${entry.id} could not be removed again`);
                            }
                        },
                    });
                }
            });
            refresh();
        }),
        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.goToProject',
            async (node?: SolutionTreeNode) => {
                const target = node && projectPathOf(node);
                if (target && !(await revealProject(target))) {
                    void vscode.window.showInformationMessage(
                        `${Path.basename(target)} is not shown in this solution.`
                    );
                }
            }
        ),
        onNode('roslynSense.solutionExplorer.unloadProject', async (_node, selected) => {
            const paths = selected.map(projectPathOf).filter((p): p is string => Boolean(p));
            await setUnloaded([...unloaded, ...paths]);
        }),
        onNode('roslynSense.solutionExplorer.reloadProject', async (_node, selected) => {
            const paths = selected.map(projectPathOf).filter((p): p is string => Boolean(p));
            await setUnloaded(unloaded.filter((u) => !paths.some((p) => samePath(p, u))));
        }),
        onNode('roslynSense.solutionExplorer.setStartupProject', async (node) => {
            const projectPath = projectPathOf(node);
            if (projectPath) {
                startupProject = projectPath;
                await context.workspaceState.update('roslynSense.startupProject', projectPath);
                refresh();
            }
        }),
        onNode('roslynSense.solutionExplorer.collapseDescendants', (node) => refresh(node)),
        onNode('roslynSense.solutionExplorer.compareSelected', async (_node, selected) => {
            const uris = urisOf(selected);
            if (uris.length !== 2) {
                void vscode.window.showInformationMessage('Select exactly two files to compare.');
                return;
            }
            await vscode.commands.executeCommand(
                'vscode.diff', vscode.Uri.parse(uris[0]), vscode.Uri.parse(uris[1]));
        }),
        onNode('roslynSense.solutionExplorer.buildSolution', (node) => buildSolution(node, 'build')),
        onNode('roslynSense.solutionExplorer.rebuildSolution', (node) =>
            buildSolution(node, 'rebuild')
        ),
        onNode('roslynSense.solutionExplorer.cleanSolution', (node) => buildSolution(node, 'clean')),
        onNode('roslynSense.solutionExplorer.copy', (_node, selected) => {
            const items = draggableOf(selected);
            if (items.length > 0) {
                clipboard = { items, cut: false };
                view.message = `${describeCount(items.length)} copied.`;
            }
        }),
        onNode('roslynSense.solutionExplorer.cut', (_node, selected) => {
            const items = draggableOf(selected);
            if (items.length > 0) {
                clipboard = { items, cut: true };
                view.message = `${describeCount(items.length)} cut.`;
            }
        }),
        onNode(
            'roslynSense.solutionExplorer.paste',
            async (node) => {
                if (!clipboard) {
                    return;
                }
                // The same dispatch a drop goes through, so pasting a project into a solution
                // folder means what dragging it there means. A cut is a move, which carries the
                // namespace fixups with it; a copy is not, because the copy is a second type with
                // the original's name and renaming it is the user's next step rather than ours to
                // guess.
                await dropNodes(node, clipboard.items, clipboard.cut ? 'move' : 'copy');

                if (clipboard.cut) {
                    clipboard = undefined;
                }
                view.message = undefined;
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.duplicate',
            async (_node, selected) => {
                for (const uri of urisOf(selected)) {
                    // Duplicate is a paste back into the folder the file is already in.
                    await edit({ action: 'copy', targetUri: uri, destinationUri: uri });
                }
                refresh();
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.newItem',
            async (node) => {
                const kind = await vscode.window.showQuickPick(
                    ['class', 'interface', 'record', 'enum', 'empty file', 'folder'],
                    { title: 'New' }
                );
                if (!kind) {
                    return;
                }
                await vscode.commands.executeCommand(
                    kind === 'folder'
                        ? 'roslynSense.solutionExplorer.newFolder'
                        : 'roslynSense.solutionExplorer.newFile',
                    node,
                    kind === 'empty file' ? 'empty' : kind
                );
            }
        ),
        onNode('roslynSense.solutionExplorer.startupAndDebug', (node) =>
            launch(node, { debug: true })
        ),
        onNode('roslynSense.solutionExplorer.debugProject', (node) =>
            launch(node, { debug: true })
        ),
        onNode('roslynSense.solutionExplorer.runProject', (node) =>
            launch(node, { debug: false })
        ),
        onNode(
            'roslynSense.solutionExplorer.packageDetails',
            (node) => {
                // Package nodes are "package:<projectPath>|<id>"; the panel takes the project
                // and opens with that package selected.
                const [projectPath, packageId] = node.id.startsWith('package:')
                    ? node.id.slice('package:'.length).split('|')
                    : [undefined, undefined];
                void vscode.commands.executeCommand(
                    'roslynSense.manageNuGetForProject',
                    projectPath ? { id: `project:${projectPath}` } : node,
                    packageId
                );
            }
        ),
        onNode(
            'roslynSense.solutionExplorer.buildProject',
            async (node) => {
                const client = getClient();
                if (!client || !node.id.startsWith('project:')) {
                    return;
                }
                await vscode.window.withProgress(
                    { location: vscode.ProgressLocation.Window, title: 'Building…' },
                    () =>
                        client.sendRequest('workspace/executeCommand', {
                            command: 'roslynSense.build',
                            arguments: [node.id.slice('project:'.length), 'Debug'],
                        })
                );
            }
        )
    );

    // "Follow current file", off by default because it fights with manual navigation.
    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(() => {
            if (state.revealActiveFile && view.visible) {
                void revealActiveEditor();
            }
        }),
        // Editors change while the view is hidden, and revealing into a hidden tree is wasted
        // work — so the catch-up happens when it comes back, otherwise the view stays pointing at
        // whatever was open the last time anyone looked at it.
        view.onDidChangeVisibility((event) => {
            if (state.revealActiveFile && event.visible) {
                void revealActiveEditor();
            }
        })
    );
}

/**
 * Searches the whole solution, showing matches as they are typed.
 *
 * Results come from the server, so this reaches every project, folder, file, package and
 * generator in the solution rather than only the rows the tree has loaded. Typing inside the
 * tree itself filters what is on screen — that is VS Code's own find widget, and the two answer
 * different questions.
 */
async function searchSolution(
    getClient: () => LanguageClient | undefined,
    view: vscode.TreeView<SolutionTreeNode>
): Promise<void> {
    const client = getClient();
    if (!client) {
        return;
    }

    const picker = vscode.window.createQuickPick<vscode.QuickPickItem & { node: SolutionTreeNode }>();
    picker.title = 'Search the solution';
    picker.placeholder = 'Project, folder, file, package or generator';
    // Results are already ranked by the server; letting the picker re-sort by its own fuzzy
    // score would undo that.
    picker.matchOnDescription = false;

    let pending: NodeJS.Timeout | undefined;
    let sequence = 0;

    const search = (query: string) => {
        clearTimeout(pending);
        if (!query.trim()) {
            picker.items = [];
            picker.busy = false;
            return;
        }

        picker.busy = true;
        pending = setTimeout(async () => {
            const mine = ++sequence;
            try {
                const matches = await client.sendRequest<SolutionTreeNode[]>(
                    'roslynSense/solutionTreeSearch',
                    { query: query.trim(), limit: 50 }
                );
                // A slower earlier request must not overwrite a newer one's results.
                if (mine !== sequence) {
                    return;
                }
                picker.items = matches.map((node) => ({
                    label: node.label,
                    description: node.description ?? node.kind,
                    node,
                }));
            } catch {
                picker.items = [];
            } finally {
                if (mine === sequence) {
                    picker.busy = false;
                }
            }
        }, 120);
    };

    picker.onDidChangeValue(search);
    picker.onDidAccept(async () => {
        const picked = picker.selectedItems[0];
        picker.hide();
        if (picked) {
            await view.reveal(picked.node, { select: true, focus: true, expand: true });
        }
    });
    picker.onDidHide(() => {
        clearTimeout(pending);
        picker.dispose();
    });

    picker.show();
}

/** The files behind a set of nodes, skipping the ones that have none. */
function urisOf(nodes: SolutionTreeNode[]): string[] {
    return nodes.map((n) => n.resourceUri).filter((uri): uri is string => Boolean(uri));
}

/** The nodes that can be dragged or put on the clipboard, reduced to what a drop needs. */
function draggableOf(nodes: readonly SolutionTreeNode[]): DragItem[] {
    return nodes
        .filter((node) => node.resourceUri || node.id.startsWith('project:'))
        .map((node) => ({ id: node.id, resourceUri: node.resourceUri }));
}

/** Compares two file-system paths the way Windows does. */
function samePath(a: string, b: string): boolean {
    return normalisePath(a) === normalisePath(b);
}

/**
 * The solution-folder a node names: its id for a solution folder, the empty string for the
 * solution itself, and undefined for anything that is not part of the solution's own structure.
 */
function solutionFolderIdOf(node: SolutionTreeNode): string | undefined {
    if (node.id.startsWith('slnfolder:')) {
        return node.id.slice('slnfolder:'.length);
    }
    return node.id.startsWith('solution:') ? '' : undefined;
}

/**
 * `text/uri-list` is newline-separated. Entries that will not parse are skipped rather than
 * thrown on — the payload arrives malformed on some VS Code builds when several files are
 * dragged at once (microsoft/vscode#195048), and one bad line should not lose the rest.
 */
function parseUriList(value: string): vscode.Uri[] {
    const uris: vscode.Uri[] = [];
    for (const line of value.split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) {
            continue;
        }
        try {
            uris.push(vscode.Uri.parse(trimmed, true));
        } catch {
            // Not a URI this can act on.
        }
    }
    return uris;
}

function describeCount(count: number): string {
    return count === 1 ? '1 item' : `${count} items`;
}

/**
 * What Delete is about to do, said plainly.
 *
 * Three of the four cases do not touch the disk at all, and a dialog that says "Delete X?"
 * regardless is asking the user to guess which one they are in.
 */
/**
 * The same promise for a whole selection.
 *
 * "Delete 3 items?" is the wrong prompt for a batch where two of the three are only leaving the
 * solution and one is really going off the disk — the user has no way to tell which is which, and
 * the single-item wording taught them to expect that distinction to be made.
 */
function describeDeleteAll(nodes: SolutionTreeNode[]): string {
    if (nodes.length === 1) {
        return describeDelete(nodes[0]);
    }

    const onDisk = nodes.filter(
        (n) => !n.id.startsWith('slnfolder:')
            && !n.id.startsWith('project:')
            && n.kind !== 'solutionItem');
    const listedOnly = nodes.length - onDisk.length;

    if (listedOnly === 0) {
        return `Delete ${describeCount(onDisk.length)} from disk?`;
    }
    if (onDisk.length === 0) {
        return `Remove ${describeCount(listedOnly)} from the solution? Nothing is deleted from disk.`;
    }
    return `Delete ${describeCount(onDisk.length)} from disk and remove ` +
        `${describeCount(listedOnly)} from the solution?`;
}

function describeDelete(node: SolutionTreeNode): string {
    if (node.id.startsWith('slnfolder:')) {
        const path = node.id.slice('slnfolder:'.length);
        // Only a top-level folder can lose anything: its solution items have no folder above
        // them to move to, and the solution cannot list a file outside one.
        return path.replace(/^\/|\/$/g, '').includes('/')
            ? `Remove the solution folder ${node.label}? What is inside it moves up a level.`
            : `Remove the solution folder ${node.label}? Projects and folders inside it move up ` +
              'a level; any solution items it holds stop being listed. Nothing is deleted from disk.';
    }
    if (node.kind === 'solutionItem') {
        return `Remove ${node.label} from the solution folder? The file stays on disk.`;
    }
    if (node.id.startsWith('project:')) {
        return `Remove ${node.label} from the solution? Its files stay on disk.`;
    }
    return `Delete ${node.label}?`;
}

/** Where an "add existing" dialog should open: next to whatever it was invoked on. */
function defaultDialogUri(node: SolutionTreeNode): vscode.Uri | undefined {
    const container = containerUriOf(node);
    if (!container) {
        return undefined;
    }
    const path = vscode.Uri.parse(container).fsPath;
    return vscode.Uri.file(Path.extname(path) ? Path.dirname(path) : path);
}

/** Where a "new file here" lands: the node's own folder, or the folder its file sits in. */
function containerUriOf(node: SolutionTreeNode | undefined): string | undefined {
    if (!node) {
        return undefined;
    }
    if (node.resourceUri) {
        return node.resourceUri;
    }
    // A project node without a resource is still addressable through its id.
    return node.id.startsWith('project:')
        ? vscode.Uri.file(node.id.slice('project:'.length)).toString()
        : undefined;
}

function projectPathOf(node: SolutionTreeNode | undefined): string | undefined {
    if (node?.id.startsWith('project:')) {
        return node.id.slice('project:'.length);
    }
    // A reference names two projects — "projectref:<owner>|<target>" — and the one it *is* is the
    // target. The owner is what an edit to the reference needs; see referenceOwnerOf.
    if (node?.id.startsWith('projectref:')) {
        return node.id.slice('projectref:'.length).split('|')[1];
    }
    // Dependencies and its groups are named "<projectPath>!deps" and "group:<projectPath>|...".
    if (node?.id.endsWith('!deps')) {
        return node.id.slice(0, -'!deps'.length);
    }
    if (node?.id.startsWith('group:')) {
        return node.id.slice('group:'.length).split('|')[0];
    }
    // folder ids are "folder:<projectPath>|<directory>".
    if (node?.id.startsWith('folder:')) {
        return node.id.slice('folder:'.length).split('|')[0];
    }
    return undefined;
}

/** The file name at the end of a uri — what an undo label calls the thing it acted on. */
function basenameOfUri(uri: string): string {
    return Path.basename(vscode.Uri.parse(uri).fsPath);
}

/** The directory holding what a uri names, as a uri — where "move it back" means. */
function parentUriOf(uri: string): string {
    return vscode.Uri.file(Path.dirname(vscode.Uri.parse(uri).fsPath)).toString();
}

/**
 * What an edit is called in an undo notification. Says what happened, not which internal action
 * ran: "Undid: addProjectReference" is a log line, not a message to a person.
 */
function describeEdit(action: string, params: Record<string, unknown>): string {
    const target = params.targetUri as string | undefined;
    const destination = params.destinationUri as string | undefined;
    const name = (params.name as string | undefined) ?? (target && basenameOfUri(target));

    switch (action) {
        case 'addProjectReference':
            return `Add reference to ${destination ? basenameOfUri(destination) : 'project'}`;
        case 'removeProjectReference':
            return `Remove reference to ${destination ? basenameOfUri(destination) : 'project'}`;
        case 'excludeFile':
            return `Exclude ${name ?? 'file'}`;
        case 'includeExistingFile':
            return `Include ${name ?? 'file'}`;
        case 'addFile':
            return `New file ${name ?? ''}`.trim();
        case 'addFolder':
            return `New folder ${name ?? ''}`.trim();
        case 'copy':
            return `Copy ${name ?? 'file'}`;
        default:
            return action;
    }
}

/** A project path as it is written on its row: the file name without the .csproj. */
function nameOf(projectPath: string): string {
    return Path.basename(projectPath, Path.extname(projectPath));
}

/** The project that declares a reference, out of a "projectref:<owner>|<target>" id. */
function referenceOwnerOf(id: string): string | undefined {
    return id.startsWith('projectref:')
        ? id.slice('projectref:'.length).split('|')[0]
        : undefined;
}

function parentIdOf(id: string): string | undefined {
    const folder = solutionItemFolderOf(id);
    if (folder !== undefined) {
        return `slnfolder:${folder}`;
    }
    // Both halves of a reference id are paths, so the generic "everything before the last |"
    // rule below would answer "projectref:<owner>" — a node that does not exist. Its parent is
    // the Projects group of the project that declares it.
    const owner = referenceOwnerOf(id);
    if (owner !== undefined) {
        return `group:projects|${owner}`;
    }
    const separator = id.lastIndexOf('|');
    return separator > 0 ? id.slice(0, separator) : undefined;
}

/**
 * The solution folder a solution item is listed under, read out of its id
 * (`slnitem:<folder>|<file>`), or undefined for anything that is not a solution item.
 */
function solutionItemFolderOf(id: string): string | undefined {
    if (!id.startsWith('slnitem:')) {
        return undefined;
    }
    const rest = id.slice('slnitem:'.length);
    const separator = rest.lastIndexOf('|');
    return separator < 0 ? undefined : rest.slice(0, separator);
}

/** What a row can be drawn with: a codicon, a shipped badge, or a light/dark icon pair. */
type NodeIcon = vscode.ThemeIcon | vscode.Uri | { light: vscode.Uri; dark: vscode.Uri };

/**
 * One of the shipped structural icons, per theme.
 *
 * These are drawn rather than themed the way the codicons are: a codicon is a single glyph in a
 * single colour, and the structural rows want what ReSharper and Rider give them — a neutral
 * outline carrying one small accent, and composites like a folder with a package cube in the
 * corner. Two variants because "neutral" is a different grey on a light background.
 */
function treeIcon(name: string, extensionUri: vscode.Uri): NodeIcon {
    return {
        light: vscode.Uri.joinPath(extensionUri, 'media', 'tree', 'light', `${name}.svg`),
        dark: vscode.Uri.joinPath(extensionUri, 'media', 'tree', 'dark', `${name}.svg`),
    };
}

/**
 * Folders that mean something beyond their name, recognised the way Visual Studio recognises
 * them: by the name itself. `Properties` holds the app designer files; `wwwroot` is the web
 * root. A directory that merely shares the name gets the badge too, which is the trade Visual
 * Studio makes as well.
 */
function folderIconName(label: string): string {
    switch (label.toLowerCase()) {
        case 'properties':
        case 'my project':
            return 'folder-properties';
        case 'wwwroot':
            return 'folder-www';
        default:
            return 'folder';
    }
}

/**
 * The icon a project is drawn with, by the extension of its project file.
 *
 * Codicons have one project glyph and no notion of language, so a C# and a Visual Basic project
 * would be the same grey box — the one distinction Visual Studio, Rider and ReSharper all draw
 * first. Each has a `-dim` variant for when the project is unloaded.
 *
 * Projects only. A language mark on every `.cs` as well makes a project row indistinguishable
 * from the files inside it, and drowns out the one row in the branch that the mark is there for.
 */
const PROJECT_ICONS: Record<string, string> = {
    '.csproj': 'project-csharp',
    '.vbproj': 'project-vb',
    '.fsproj': 'project-fsharp',
};

/**
 * Everything else, as a tinted codicon.
 *
 * The point is coverage rather than fidelity: an extension that falls through here still gets a
 * glyph, so no row is ever drawn without one. See {@link iconFor} for why that matters.
 */
const FILE_CODICONS: Record<string, [string, string]> = {
    // The source languages, marked by colour rather than by a badge — the badge is the project's.
    '.cs': ['file-code', 'charts.purple'],
    '.csx': ['file-code', 'charts.purple'],
    '.vb': ['file-code', 'charts.blue'],
    '.fs': ['file-code', 'charts.green'],
    '.fsi': ['file-code', 'charts.green'],
    '.fsx': ['file-code', 'charts.green'],
    '.razor': ['code', 'charts.purple'],
    '.cshtml': ['code', 'charts.purple'],
    '.aspx': ['code', 'charts.purple'],
    '.ascx': ['code', 'charts.purple'],
    '.ashx': ['code', 'charts.purple'],
    '.master': ['code', 'charts.purple'],
    '.json': ['json', 'charts.blue'],
    '.xml': ['code', 'charts.green'],
    '.xaml': ['code', 'charts.green'],
    '.config': ['settings-gear', 'charts.blue'],
    '.props': ['settings-gear', 'charts.blue'],
    '.targets': ['settings-gear', 'charts.blue'],
    '.editorconfig': ['settings-gear', 'charts.blue'],
    '.yml': ['settings-gear', 'charts.blue'],
    '.yaml': ['settings-gear', 'charts.blue'],
    '.toml': ['settings-gear', 'charts.blue'],
    '.ini': ['settings-gear', 'charts.blue'],
    '.resx': ['symbol-string', 'charts.green'],
    '.md': ['markdown', 'charts.blue'],
    '.txt': ['note', 'descriptionForeground'],
    '.sql': ['database', 'charts.blue'],
    '.ts': ['file-code', 'charts.blue'],
    '.tsx': ['file-code', 'charts.blue'],
    '.js': ['file-code', 'charts.blue'],
    '.mjs': ['file-code', 'charts.blue'],
    '.css': ['symbol-color', 'charts.blue'],
    '.scss': ['symbol-color', 'charts.blue'],
    '.html': ['browser', 'charts.green'],
    '.htm': ['browser', 'charts.green'],
    '.sh': ['terminal', 'charts.green'],
    '.ps1': ['terminal', 'charts.blue'],
    '.cmd': ['terminal', 'descriptionForeground'],
    '.bat': ['terminal', 'descriptionForeground'],
    '.png': ['file-media', 'charts.purple'],
    '.jpg': ['file-media', 'charts.purple'],
    '.jpeg': ['file-media', 'charts.purple'],
    '.gif': ['file-media', 'charts.purple'],
    '.svg': ['file-media', 'charts.purple'],
    '.ico': ['file-media', 'charts.purple'],
    '.dll': ['library', 'charts.green'],
    '.exe': ['library', 'charts.green'],
    '.pdb': ['library', 'descriptionForeground'],
    '.snk': ['lock', 'charts.green'],
    '.pfx': ['lock', 'charts.green'],
    '.sln': ['versions', 'charts.purple'],
    '.slnx': ['versions', 'charts.purple'],
};

function extensionOf(resourceUri: string | null): string {
    return resourceUri
        ? Path.extname(vscode.Uri.parse(resourceUri).fsPath).toLowerCase()
        : '';
}

function badgeUri(name: string, extensionUri: vscode.Uri): vscode.Uri {
    return vscode.Uri.joinPath(extensionUri, 'media', `lang-${name}.svg`);
}

/** The icon for a project, by the language it is written in. */
function languageIcon(resourceUri: string | null, extensionUri: vscode.Uri): NodeIcon {
    return treeIcon(PROJECT_ICONS[extensionOf(resourceUri)] ?? 'project', extensionUri);
}

/**
 * The icon for a file.
 *
 * `ThemeIcon.File` hands the decision to the user's file icon theme, which is the friendlier
 * answer right up until the theme has nothing for the extension — or the user has no file icon
 * theme at all. Then the row is drawn without an icon, and one icon-less row is enough to shift a
 * whole branch out of line (see {@link iconFor}). Drawing files ourselves is what makes every row
 * the same width; `solutionExplorer.fileIcons` gives the icon theme back to anyone who prefers it.
 */
function fileIcon(
    resourceUri: string | null,
    extensionUri: vscode.Uri,
    fromIconTheme: boolean
): NodeIcon {
    if (fromIconTheme) {
        return vscode.ThemeIcon.File;
    }
    const extension = extensionOf(resourceUri);
    // The one file that keeps a shipped badge: no codicon says "protobuf", and `.proto` has no
    // project of its own for a badge to sit on instead.
    if (extension === '.proto') {
        return badgeUri('proto', extensionUri);
    }
    const [id, color] = FILE_CODICONS[extension] ?? ['file', 'descriptionForeground'];
    return tinted(id, color);
}

/**
 * A codicon with a tint, the way Rider and Visual Studio colour their tree.
 *
 * From the charts palette, blue, green and purple only. `charts.orange` and `charts.yellow`
 * resolve to #d18616 and #cca700, which at 16px on a dark background read as brown rather than
 * as a colour anyone chose.
 */
function tinted(id: string, color: string): vscode.ThemeIcon {
    return new vscode.ThemeIcon(id, new vscode.ThemeColor(color));
}

/**
 * What a row is drawn with.
 *
 * Every kind gets an icon of its own, because a tree drawn entirely in the foreground colour
 * reads as one undifferentiated list. A project carries the language badge; the files inside it
 * carry a glyph tinted in the same family, so the project is still the row that stands out.
 *
 * Every expandable row must end up with an icon, and that is why folders are shipped SVGs and
 * never `ThemeIcon('folder')`. VS Code special-cases the *id* `folder` (and `file`) on any row
 * that has a resourceUri: `ThemeIcon.isFolder` compares the id alone, colour and all, and hands
 * the row to the user's file icon theme. Most file icon themes — Seti, the default — ship file
 * icons and no folder icons, so the row renders with no icon at all. VS Code reacts to an
 * expandable row without an icon by collapsing the twistie column on its *leaf* siblings, to
 * line their icons up with the arrows; a row that does have one keeps the column. Mixing the two
 * indents the icon-bearing rows a whole extra level and lines up nothing with anything.
 *
 * Colour is deliberately sparse, the way ReSharper draws the same tree: a neutral outline with
 * at most one accent per icon. Blue is the dependency family end to end, purple is the solution's
 * mark and generated code, grey is what is dimmed or merely transitive. Language badges and file
 * glyphs carry their own colour; the structure around them stays quiet so they can.
 */
function iconFor(
    node: SolutionTreeNode,
    extensionUri: vscode.Uri,
    fileIconsFromTheme: boolean
): NodeIcon {
    // A project's kind stays "project" whether it is runnable or unloaded; only its context
    // value says which, and an unloaded one is drawn greyed the way its label already is.
    switch (node.kind === 'project' ? node.contextValue : node.kind) {
        case 'solution':
            return treeIcon('solution', extensionUri);
        // A solution folder is not a directory — it exists only in the .sln — so it is drawn
        // apart from the real folders it sits beside: the folder shape, carrying the solution's
        // purple mark.
        case 'solutionFolder':
            return treeIcon('solution-folder', extensionUri);
        case 'folder':
            return treeIcon(folderIconName(node.label), extensionUri);
        case 'project':
        case 'projectRunnable':
        case 'projectRef':
            // Dimmed here does not mean unloaded-by-choice — that is the case below, with its own
            // context value — it means the workspace has not loaded this project yet. Same icon
            // for both because it says the same thing about the row: nothing in it can answer.
            return node.dimmed
                ? treeIcon(
                    `${PROJECT_ICONS[extensionOf(node.resourceUri)] ?? 'project'}-dim`,
                    extensionUri
                )
                : languageIcon(node.resourceUri, extensionUri);
        case 'projectUnloaded':
            // Nothing about an unloaded project is live, and a full-colour icon says otherwise.
            return treeIcon(
                `${PROJECT_ICONS[extensionOf(node.resourceUri)] ?? 'project'}-dim`,
                extensionUri
            );
        // A graph of nodes, the way ReSharper and Rider draw it — Dependencies is the
        // relationships between things, not a list of references.
        case 'dependencies':
        case 'dependenciesNetFx':
            return treeIcon('dependencies', extensionUri);
        case 'imports':
        case 'import':
            return tinted('file-symlink-file', 'charts.blue');
        case 'framework':
            return tinted('layers', 'charts.blue');
        case 'packages':
            return treeIcon('packages-folder', extensionUri);
        case 'package':
            return treeIcon('package', extensionUri);
        case 'transitive':
        case 'transitivePackage':
            // The same cube, faint and dashed: these are packages too, just not ones the project
            // file names — so not something to right-click and uninstall.
            return treeIcon('package-transitive', extensionUri);
        case 'projects':
            return treeIcon('projects-folder', extensionUri);
        case 'assemblies':
        case 'assembly':
            return tinted('library', 'charts.blue');
        case 'analyzers':
        case 'analyzer':
            return tinted('circuit-board', 'charts.blue');
        case 'generator':
            return tinted('wand', 'charts.purple');
        // Generated output is a file the user never wrote, and telling it apart from one they
        // did is the whole point of showing it separately.
        case 'generatedFile':
            return treeIcon('generated-code', extensionUri);
        case 'file':
        case 'solutionItem':
            return fileIcon(node.resourceUri, extensionUri, fileIconsFromTheme);
        default:
            // An unknown kind is still a row, and a row still needs its slot filled.
            return node.hasChildren
                ? treeIcon('folder', extensionUri)
                : node.resourceUri
                  ? fileIcon(node.resourceUri, extensionUri, fileIconsFromTheme)
                  : tinted('circle-outline', 'descriptionForeground');
    }
}
