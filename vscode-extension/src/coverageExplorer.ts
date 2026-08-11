import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * The Coverage view: a dotCover-style tree of namespace → class → method, each row carrying its
 * statement coverage and how many tests are known to reach it.
 *
 * It reads the stored snapshot rather than a live run, so opening it costs nothing and it still
 * has something to show in a fresh window. What it shows is as old as the last coverage run —
 * the header row says when that was.
 */

interface CoverageMethod {
    namespace: string;
    className: string;
    methodName: string;
    filePath: string;
    line: number;
    coveredStatements: number;
    totalStatements: number;
    coveredBranches: number;
    totalBranches: number;
    tests: number;
}

interface CoverageSnapshot {
    collectedAtUtc: string | null;
    methods: CoverageMethod[];
    mappedTests: number;
}

type Node = GroupNode | MethodNode;

interface GroupNode {
    kind: 'namespace' | 'class';
    label: string;
    covered: number;
    total: number;
    tests: number;
    children: Node[];
}

interface MethodNode {
    kind: 'method';
    label: string;
    covered: number;
    total: number;
    tests: number;
    method: CoverageMethod;
}

class CoverageTreeProvider implements vscode.TreeDataProvider<Node> {
    private readonly changed = new vscode.EventEmitter<Node | undefined>();
    readonly onDidChangeTreeData = this.changed.event;

    private roots: Node[] = [];
    private snapshot: CoverageSnapshot | undefined;

    constructor(private readonly getClient: () => LanguageClient | undefined) {}

    async refresh(): Promise<void> {
        const client = this.getClient();
        if (!client) {
            return;
        }

        try {
            this.snapshot = await client.sendRequest<CoverageSnapshot>('roslynSense/coverageSnapshot', {
                anchorPath: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? null,
            });
            this.roots = build(this.snapshot.methods);
        } catch {
            this.snapshot = undefined;
            this.roots = [];
        }

        this.changed.fire(undefined);
    }

    /** What the view's title says: the measurement's age, or that there is none. */
    describe(): string {
        if (!this.snapshot || this.snapshot.methods.length === 0) {
            return 'no coverage collected yet';
        }
        const covered = this.snapshot.methods.reduce((sum, m) => sum + m.coveredStatements, 0);
        const total = this.snapshot.methods.reduce((sum, m) => sum + m.totalStatements, 0);
        const when = this.snapshot.collectedAtUtc
            ? new Date(this.snapshot.collectedAtUtc).toLocaleString()
            : 'unknown';
        return `${percent(covered, total)} of ${total} statements — measured ${when}`;
    }

    getChildren(node?: Node): Node[] {
        if (!node) {
            return this.roots;
        }
        return node.kind === 'method' ? [] : node.children;
    }

    getTreeItem(node: Node): vscode.TreeItem {
        const item = new vscode.TreeItem(
            node.label,
            node.kind === 'method'
                ? vscode.TreeItemCollapsibleState.None
                : vscode.TreeItemCollapsibleState.Collapsed
        );

        // The bar dotCover draws is a column this API does not have; the percentage leads the
        // description instead so the eye can still run down it.
        item.description = `${percent(node.covered, node.total)}  ${node.covered}/${node.total}` +
            (node.tests > 0 ? `  ·  ${node.tests} test${node.tests === 1 ? '' : 's'}` : '');

        item.iconPath = new vscode.ThemeIcon(
            node.kind === 'method' ? 'symbol-method' : node.kind === 'class' ? 'symbol-class' : 'symbol-namespace',
            new vscode.ThemeColor(colorFor(node.covered, node.total))
        );

        item.tooltip = new vscode.MarkdownString(
            [
                `**${node.label}**`,
                '',
                `Statements: ${node.covered} / ${node.total} (${percent(node.covered, node.total)})`,
                node.kind === 'method' && node.method.totalBranches > 0
                    ? `Branches: ${node.method.coveredBranches} / ${node.method.totalBranches}`
                    : '',
                node.tests > 0 ? `Covered by ${node.tests} test(s)` : 'No test attributed — build the coverage map',
            ]
                .filter(Boolean)
                .join('\n\n')
        );

        if (node.kind === 'method' && node.method.filePath) {
            item.command = {
                command: 'roslynSense.openLocation',
                title: 'Open',
                arguments: [
                    vscode.Uri.file(node.method.filePath).toString(),
                    Math.max(0, node.method.line - 1),
                    0,
                ],
            };
            item.resourceUri = vscode.Uri.file(node.method.filePath);
        }

        item.contextValue = node.kind;
        return item;
    }
}

export function registerCoverageExplorer(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const provider = new CoverageTreeProvider(getClient);
    const view = vscode.window.createTreeView('roslynSense.coverage', {
        treeDataProvider: provider,
        showCollapseAll: true,
    });

    const refresh = async () => {
        await provider.refresh();
        view.description = provider.describe();
    };

    context.subscriptions.push(
        view,
        vscode.commands.registerCommand('roslynSense.refreshCoverage', refresh)
    );

    // Filled in when the view is first shown rather than on activation: the snapshot is a file
    // read, but there is no reason to do it for a window that never opens the view.
    context.subscriptions.push(
        view.onDidChangeVisibility((event) => {
            if (event.visible) {
                void refresh();
            }
        })
    );
}

/** namespace → class → method, with each level summing what is under it. */
function build(methods: CoverageMethod[]): Node[] {
    const namespaces = new Map<string, Map<string, MethodNode[]>>();

    for (const method of methods) {
        const ns = method.namespace || '(global)';
        let classes = namespaces.get(ns);
        if (!classes) {
            classes = new Map();
            namespaces.set(ns, classes);
        }

        const shortClass = method.className.startsWith(`${ns}.`)
            ? method.className.slice(ns.length + 1)
            : method.className;

        const existing = classes.get(shortClass);
        const node: MethodNode = {
            kind: 'method',
            label: method.methodName,
            covered: method.coveredStatements,
            total: method.totalStatements,
            tests: method.tests,
            method,
        };
        if (existing) {
            existing.push(node);
        } else {
            classes.set(shortClass, [node]);
        }
    }

    const roots: GroupNode[] = [];
    for (const [ns, classes] of namespaces) {
        const classNodes: GroupNode[] = [];
        for (const [className, methodNodes] of classes) {
            methodNodes.sort((a, b) => ratio(a) - ratio(b));
            classNodes.push({
                kind: 'class',
                label: className,
                covered: sum(methodNodes, (n) => n.covered),
                total: sum(methodNodes, (n) => n.total),
                tests: sum(methodNodes, (n) => n.tests),
                children: methodNodes,
            });
        }
        classNodes.sort((a, b) => ratio(a) - ratio(b));
        roots.push({
            kind: 'namespace',
            label: ns,
            covered: sum(classNodes, (n) => n.covered),
            total: sum(classNodes, (n) => n.total),
            tests: sum(classNodes, (n) => n.tests),
            children: classNodes,
        });
    }

    // Worst coverage first: a coverage window is opened to find what is not tested.
    roots.sort((a, b) => ratio(a) - ratio(b));
    return roots;
}

function ratio(node: Node): number {
    return node.total === 0 ? 1 : node.covered / node.total;
}

function sum<T>(items: T[], select: (item: T) => number): number {
    return items.reduce((total, item) => total + select(item), 0);
}

function percent(covered: number, total: number): string {
    return total === 0 ? '—' : `${Math.round((covered / total) * 100)}%`;
}

function colorFor(covered: number, total: number): string {
    if (total === 0) {
        return 'disabledForeground';
    }
    const rate = covered / total;
    if (rate >= 0.8) {
        return 'testing.iconPassed';
    }
    return rate >= 0.4 ? 'testing.iconQueued' : 'testing.iconFailed';
}
