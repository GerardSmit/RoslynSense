import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

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

/** Toggle state, persisted per workspace so the view opens the way it was left. */
interface ViewState {
    showAllFiles: boolean;
    showIgnored: boolean;
    revealActiveFile: boolean;
}

export function registerSolutionExplorer(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const state: ViewState = {
        showAllFiles: context.workspaceState.get('roslynSense.showAllFiles', false),
        showIgnored: context.workspaceState.get('roslynSense.showIgnored', false),
        revealActiveFile: context.workspaceState.get('roslynSense.revealActiveFile', false),
    };

    let filter: string | undefined;

    const changeEmitter = new vscode.EventEmitter<SolutionTreeNode | undefined>();
    const nodesById = new Map<string, SolutionTreeNode>();

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
            // Files use the resource for their icon so the user's file icon theme applies;
            // logical nodes get a ThemeIcon since they have no file behind them.
            const themeIcon = iconFor(node.kind);
            if (themeIcon) {
                item.iconPath = themeIcon;
            }
            if (node.kind === 'file' || node.kind === 'solutionItem' || node.kind === 'import') {
                item.command = {
                    command: 'vscode.open',
                    title: 'Open',
                    arguments: [item.resourceUri],
                };
            }
            if (node.dimmed) {
                item.description = node.description ?? 'not in project';
            }
            return item;
        },

        async getChildren(node) {
            const client = getClient();
            if (!client) {
                return [];
            }
            try {
                const children = await client.sendRequest<SolutionTreeNode[]>(
                    'roslynSense/solutionTree',
                    {
                        nodeId: node?.id ?? null,
                        showAllFiles: state.showAllFiles,
                        showIgnored: state.showIgnored,
                        filter: filter ?? null,
                    }
                );
                children.forEach((child) => nodesById.set(child.id, child));
                return children;
            } catch {
                return [];
            }
        },

        getParent(node) {
            // Reveal needs a parent chain; ids encode enough to find it for files.
            const parentId = parentIdOf(node.id);
            return parentId ? nodesById.get(parentId) : undefined;
        },
    };

    const view = vscode.window.createTreeView(VIEW_ID, {
        treeDataProvider: provider,
        canSelectMany: true,
        showCollapseAll: true,
    });
    context.subscriptions.push(view, changeEmitter);

    const refresh = () => changeEmitter.fire(undefined);

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

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.solutionExplorer.refresh', refresh),

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
        vscode.commands.registerCommand('roslynSense.solutionExplorer.revealActiveFile', () =>
            setToggle('revealActiveFile', !state.revealActiveFile)
        ),

        vscode.commands.registerCommand('roslynSense.solutionExplorer.search', async () => {
            const query = await vscode.window.showInputBox({
                title: 'Filter the solution',
                prompt: 'Show only matching nodes',
                value: filter,
            });
            filter = query?.trim() ? query.trim() : undefined;
            view.message = filter ? `Filtering by “${filter}”` : undefined;
            refresh();
        }),
        vscode.commands.registerCommand('roslynSense.solutionExplorer.clearSearch', () => {
            filter = undefined;
            view.message = undefined;
            refresh();
        }),

        vscode.commands.registerCommand('roslynSense.solutionExplorer.goToNode', async () => {
            const client = getClient();
            if (!client) {
                return;
            }
            const query = await vscode.window.showInputBox({
                title: 'Go to node',
                prompt: 'Project, folder, file, or package name',
            });
            if (!query) {
                return;
            }
            const matches = await client.sendRequest<SolutionTreeNode[]>(
                'roslynSense/solutionTreeSearch',
                { query, limit: 50 }
            );
            const picked = await vscode.window.showQuickPick(
                matches.map((node) => ({
                    label: node.label,
                    description: node.description ?? node.kind,
                    node,
                })),
                { title: `${matches.length} match(es)` }
            );
            if (picked) {
                await view.reveal(picked.node, { select: true, focus: true, expand: true });
            }
        }),

        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.revealInExplorer',
            (node: SolutionTreeNode) => {
                if (node.resourceUri) {
                    void vscode.commands.executeCommand(
                        'revealFileInOS', vscode.Uri.parse(node.resourceUri));
                }
            }
        ),
        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.findInFolder',
            (node: SolutionTreeNode) => {
                if (node.resourceUri) {
                    void vscode.commands.executeCommand('workbench.action.findInFiles', {
                        filesToInclude: vscode.Uri.parse(node.resourceUri).fsPath,
                    });
                }
            }
        ),
        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.openProjectFile',
            (node: SolutionTreeNode) => {
                const target = node.id.startsWith('project:')
                    ? vscode.Uri.file(node.id.slice('project:'.length))
                    : node.resourceUri
                      ? vscode.Uri.parse(node.resourceUri)
                      : undefined;
                if (target) {
                    void vscode.window.showTextDocument(target);
                }
            }
        ),
        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.buildProject',
            async (node: SolutionTreeNode) => {
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

    // "Always select opened file", off by default because it fights with manual navigation.
    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(async (editor) => {
            if (!state.revealActiveFile || !editor || !view.visible) {
                return;
            }
            const node = nodesById.get(`file:${editor.document.uri.fsPath}`);
            if (node) {
                await view.reveal(node, { select: true, focus: false });
            }
        })
    );
}

function parentIdOf(id: string): string | undefined {
    const separator = id.lastIndexOf('|');
    return separator > 0 ? id.slice(0, separator) : undefined;
}

function iconFor(kind: string): vscode.ThemeIcon | undefined {
    switch (kind) {
        case 'solution':
            return new vscode.ThemeIcon('versions');
        case 'solutionFolder':
        case 'folder':
            return new vscode.ThemeIcon('folder');
        case 'project':
        case 'projectRef':
            return new vscode.ThemeIcon('project');
        case 'dependencies':
            return new vscode.ThemeIcon('references');
        case 'imports':
        case 'import':
            return new vscode.ThemeIcon('file-symlink-file');
        case 'framework':
            return new vscode.ThemeIcon('layers');
        case 'packages':
        case 'package':
            return new vscode.ThemeIcon('package');
        case 'projects':
            return new vscode.ThemeIcon('type-hierarchy');
        case 'assemblies':
        case 'assembly':
            return new vscode.ThemeIcon('library');
        case 'analyzers':
        case 'analyzer':
            return new vscode.ThemeIcon('circuit-board');
        case 'generator':
            return new vscode.ThemeIcon('wand');
        case 'generatedFile':
            return new vscode.ThemeIcon('file-code');
        default:
            return undefined;
    }
}
