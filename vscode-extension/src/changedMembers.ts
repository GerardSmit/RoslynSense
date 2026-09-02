import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * The Changed Members view: what the git diff touched, listed as methods and properties instead
 * of hunks. Clicking a member goes to the first changed line inside it, so a change can be
 * validated symbol by symbol; the inline actions open the same spot as a git diff or as a plain
 * editor, whichever the click itself was not configured to do. Members can be ticked off as
 * reviewed — a tick that turns gray if the member changes again afterwards — and staging counts
 * as a tick, block by block, so `git add` is a way to mark a change read. Deleted members and
 * types list by name with a "removed" tag; clicking one lands where the deletion is visible, or
 * on the base revision's copy when the whole file is gone. Changed files that
 * are not C# — markup, contracts, configs — have no members to list, so each is one file row
 * that opens at its first changed line; the ... menu can hide them.
 */

interface ChangedBlockInfo {
    startLine: number;
    endLine: number;
    preview: string;
    /** Every line of the run is in the index already. */
    staged: boolean;
}

interface ChangedMemberInfo {
    name: string;
    containerType: string;
    namespace: string;
    kind: string;
    startLine: number;
    endLine: number;
    firstChangedLine: number;
    changedLineCount: number;
    blocks: ChangedBlockInfo[];
    /** The member's whole change is staged — nothing of it is left dirty. */
    staged: boolean;
    /** The diff deleted the member outright; the lines point at where the deletion is visible. */
    removed: boolean;
}

interface ChangedMembersFileInfo {
    filePath: string;
    wholeFile: boolean;
    members: ChangedMemberInfo[];
    isTest: boolean;
    /** Where a click on the file itself lands — the point of a row with no members under it. */
    firstChangedLine: number;
    /** The file's whole change is staged. Always false outside the uncommitted scope. */
    staged: boolean;
    /** How many lines the diff touched in the file; zero for a whole-file change. */
    changedLineCount: number;
    /** The diff deleted the file itself; only the diff base's version is left to open. */
    deleted: boolean;
}

interface ChangedMembersResult {
    files: ChangedMembersFileInfo[];
    description: string;
    error: string | null;
    diffBaseRef: string | null;
}

/** A member with the file it lives in — the unit every view mode ultimately shows. */
interface Row {
    member: ChangedMemberInfo;
    filePath: string;
    wholeFile: boolean;
    isTest: boolean;
}

type Node = FileNode | GroupNode | MemberNode | BlockNode;

interface FileNode {
    kind: 'file';
    filePath: string;
    wholeFile: boolean;
    isTest: boolean;
    firstChangedLine: number;
    staged: boolean;
    changedLineCount: number;
    deleted: boolean;
    children: MemberNode[];
}

/** What a file needs to be ticked off as a whole — the shape both the wire and the tree have. */
interface ReviewableFile {
    filePath: string;
    wholeFile: boolean;
    changedLineCount: number;
    staged: boolean;
}

interface GroupNode {
    kind: 'namespace' | 'type';
    label: string;
    children: Node[];
}

interface MemberNode {
    kind: 'member';
    row: Row;
    /** What the row says besides the member's own name; varies by view mode. */
    detail: string;
}

/** One contiguous changed run inside a member — a rung to click through a big method with. */
interface BlockNode {
    kind: 'block';
    row: Row;
    block: ChangedBlockInfo;
}

type ViewMode = 'simpleTree' | 'fullTree' | 'flat';

function config(): vscode.WorkspaceConfiguration {
    return vscode.workspace.getConfiguration('roslynSense');
}

function viewMode(): ViewMode {
    return config().get<ViewMode>('changedMembers.view', 'simpleTree');
}

function opensInDiff(): boolean {
    return config().get<'editor' | 'gitDiff'>('changedMembers.openIn', 'editor') === 'gitDiff';
}

function showsBlocks(): boolean {
    return config().get<boolean>('changedMembers.showBlocks', true);
}

function showsTests(): boolean {
    return config().get<boolean>('changedMembers.showTests', true);
}

function showsOtherFiles(): boolean {
    return config().get<boolean>('changedMembers.showOtherFiles', true);
}

/** Whether what has already been reviewed — ticked off, or staged — still takes up a row. */
function showsReviewed(): boolean {
    return config().get<boolean>('changedMembers.showReviewed', true);
}

/** A changed file with no member breakdown: markup, contracts, configs — anything not C#. */
function isOtherFile(filePath: string): boolean {
    return !filePath.toLowerCase().endsWith('.cs');
}

function scope(): 'uncommitted' | 'branch' {
    return config().get<'uncommitted' | 'branch'>('changedMembers.scope', 'uncommitted');
}

/**
 * The blocks a member offers as rows. Staged runs are reviewed runs, so hiding reviewed work
 * leaves a half-staged member showing only what is still dirty.
 */
function visibleBlocks(member: ChangedMemberInfo): ChangedBlockInfo[] {
    return showsReviewed() ? member.blocks : member.blocks.filter((b) => !b.staged);
}

/** Block rows only earn their place when there is more than one spot to step between. */
function hasBlockChildren(node: MemberNode): boolean {
    return showsBlocks() && visibleBlocks(node.row.member).length > 1;
}

/**
 * The reviewed ticks, keyed by member identity, valued by a fingerprint of the member's change
 * at the moment it was ticked. A tick whose fingerprint no longer matches renders gray: it was
 * reviewed, and then the member changed again. Line numbers stay out of the fingerprint, so an
 * edit elsewhere in the file does not un-gray anything.
 */
class ReviewStore {
    private static readonly stateKey = 'roslynSense.changedMembersReviewed';

    constructor(private readonly state: vscode.Memento) {}

    private all(): Record<string, string> {
        return this.state.get<Record<string, string>>(ReviewStore.stateKey, {});
    }

    fingerprintAtReview(key: string): string | undefined {
        return this.all()[key];
    }

    /** Ticks a whole set at once — a file or a namespace is one action, not one per member. */
    async mark(ticks: Iterable<readonly [string, string]>): Promise<void> {
        await this.state.update(ReviewStore.stateKey, {
            ...this.all(),
            ...Object.fromEntries(ticks),
        });
    }

    async unmark(keys: Iterable<string>): Promise<void> {
        const entries = { ...this.all() };
        for (const key of keys) {
            delete entries[key];
        }
        await this.state.update(ReviewStore.stateKey, entries);
    }

    /** Ticks for members no longer in the diff are finished business — committed or reverted. */
    async prune(liveKeys: Set<string>): Promise<void> {
        const entries = this.all();
        const kept = Object.fromEntries(Object.entries(entries).filter(([key]) => liveKeys.has(key)));
        if (Object.keys(kept).length !== Object.keys(entries).length) {
            await this.state.update(ReviewStore.stateKey, kept);
        }
    }
}

/** Every member row a node stands for: itself for a member, the whole subtree for a group. */
function rowsUnder(node: Node): Row[] {
    switch (node.kind) {
        case 'member':
            return [node.row];
        case 'block':
            return [node.row];
        default:
            return node.children.flatMap(rowsUnder);
    }
}

function rowOf(file: ChangedMembersFileInfo, member: ChangedMemberInfo): Row {
    return {
        member,
        filePath: file.filePath,
        wholeFile: file.wholeFile,
        isTest: file.isTest,
    };
}

function reviewKey(row: Row): string {
    const { member } = row;
    return [row.filePath, member.namespace, member.containerType, member.name, member.kind].join('|');
}

/**
 * A file with nothing to break down — markup, a config, C# the parser could not read — is
 * reviewed as a whole, under a key no member can collide with: members always have a kind.
 */
function fileReviewKey(filePath: string): string {
    return [filePath, '', '', '', 'file'].join('|');
}

/**
 * What a file's change looked like when it was ticked. A whole-file change has no line count to
 * go by, so a new file ticked off stays ticked until it is committed or reverted.
 */
function fileFingerprint(file: ReviewableFile): string {
    return file.wholeFile ? 'whole' : `${file.changedLineCount}`;
}

function fingerprint(row: Row): string {
    const { member } = row;
    return row.wholeFile
        ? `whole:${member.changedLineCount}`
        : `${member.changedLineCount}:${member.blocks.map((b) => b.preview).join(' ')}`;
}

class ChangedMembersProvider implements vscode.TreeDataProvider<Node> {
    private readonly changed = new vscode.EventEmitter<Node | undefined>();
    readonly onDidChangeTreeData = this.changed.event;

    private roots: Node[] = [];
    private result: ChangedMembersResult | undefined;

    /** What the diff view's left side should show; refreshed with the tree. */
    diffBaseRef = 'HEAD';

    constructor(
        private readonly getClient: () => LanguageClient | undefined,
        private readonly reviews: ReviewStore
    ) {}

    async refresh(): Promise<void> {
        const client = this.getClient();
        if (!client) {
            return;
        }

        try {
            this.result = await client.sendRequest<ChangedMembersResult>('roslynSense/changedMembers', {
                anchorPath: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? null,
                scope: scope(),
            });
            this.diffBaseRef = this.result.diffBaseRef ?? 'HEAD';
            await this.reviews.prune(
                new Set([
                    ...this.allRows().map(reviewKey),
                    ...(this.result?.files ?? [])
                        .filter((file) => file.members.length === 0)
                        .map((file) => fileReviewKey(file.filePath)),
                ])
            );
        } catch {
            this.result = undefined;
        }

        this.rebuild();
    }

    /** Re-groups what was last fetched — a view-mode change needs no new request. */
    rebuild(): void {
        this.roots = this.result ? build(this.visibleFiles(), viewMode()) : [];
        this.changed.fire(undefined);
    }

    private visibleFiles(): ChangedMembersFileInfo[] {
        let files = this.result?.files ?? [];
        if (!showsTests()) {
            files = files.filter((f) => !f.isTest);
        }
        if (!showsOtherFiles()) {
            files = files.filter((f) => !isOtherFile(f.filePath));
        }
        if (!showsReviewed()) {
            files = files.flatMap((file) => {
                // A file with no member breakdown is one row, reviewed only as a whole.
                if (file.members.length === 0) {
                    return this.isFileReviewed(file) ? [] : [file];
                }
                const members = file.members.filter((m) => !this.isReviewed(rowOf(file, m)));
                return members.length > 0 ? [{ ...file, members }] : [];
            });
        }
        return files;
    }

    /**
     * Whether a member is done with: staged, or ticked off and unchanged since. A stale tick
     * is not reviewed work — the member moved after it was read — so it stays in view.
     */
    private isReviewed(row: Row): boolean {
        if (row.member.staged) {
            return true;
        }
        const reviewedAs = this.reviews.fingerprintAtReview(reviewKey(row));
        return reviewedAs !== undefined && reviewedAs === fingerprint(row);
    }

    /** Every member the diff touched, filters or no filters — what a tick can belong to. */
    private allRows(): Row[] {
        return (this.result?.files ?? []).flatMap((file) =>
            file.members.map((member) => rowOf(file, member))
        );
    }

    /** Whether a file with no members to tick off has been ticked off itself. */
    private isFileReviewed(file: ReviewableFile): boolean {
        if (file.staged) {
            return true;
        }
        const reviewedAs = this.reviews.fingerprintAtReview(fileReviewKey(file.filePath));
        return reviewedAs !== undefined && reviewedAs === fileFingerprint(file);
    }

    /**
     * How much of a row is dealt with. A file or a group is only as reviewed as the members
     * under it, and it is "staged" only when staging is the whole reason — there is no tick
     * left to take back.
     */
    reviewStateOf(node: Node): 'open' | 'reviewed' | 'staged' {
        if (node.kind === 'block') {
            return node.block.staged ? 'staged' : 'open';
        }
        if (node.kind === 'member') {
            return node.row.member.staged
                ? 'staged'
                : this.isReviewed(node.row)
                  ? 'reviewed'
                  : 'open';
        }
        if (node.kind === 'file' && node.children.length === 0) {
            return node.staged ? 'staged' : this.isFileReviewed(node) ? 'reviewed' : 'open';
        }

        const rows = rowsUnder(node);
        if (rows.length === 0 || !rows.every((row) => this.isReviewed(row))) {
            return 'open';
        }
        return rows.every((row) => row.member.staged) ? 'staged' : 'reviewed';
    }

    /** Ticks off everything under a node, skipping what staging already covers. */
    async mark(node: Node): Promise<void> {
        const rows = rowsUnder(node).filter((row) => !row.member.staged);
        const ticks: [string, string][] = rows.map((row) => [reviewKey(row), fingerprint(row)]);
        if (node.kind === 'file' && node.children.length === 0 && !node.staged) {
            ticks.push([fileReviewKey(node.filePath), fileFingerprint(node)]);
        }
        await this.reviews.mark(ticks);
        this.rebuild();
    }

    async unmark(node: Node): Promise<void> {
        const keys = rowsUnder(node).map(reviewKey);
        if (node.kind === 'file' && node.children.length === 0) {
            keys.push(fileReviewKey(node.filePath));
        }
        await this.reviews.unmark(keys);
        this.rebuild();
    }

    /**
     * Every clickable change spot in view order — what next/previous stepping walks. Members
     * whose lines are unknowable (whole files) contribute their one landing line. Deleted files
     * stay out: their lines belong to the base revision, and stepping walks the working tree.
     */
    targets(): { filePath: string; line: number }[] {
        return this.visibleFiles()
            .filter((file) => !file.deleted)
            .flatMap((file) =>
                file.members.length > 0
                    ? file.members.flatMap((member) =>
                          visibleBlocks(member).length > 0
                              ? visibleBlocks(member).map((b) => ({
                                    filePath: file.filePath,
                                    line: b.startLine,
                                }))
                              : [{ filePath: file.filePath, line: member.firstChangedLine }]
                      )
                    : [{ filePath: file.filePath, line: file.firstChangedLine }]
            )
            .sort((a, b) => a.filePath.localeCompare(b.filePath) || a.line - b.line);
    }

    /** What the view header says about the tree under it. */
    describe(): string {
        if (this.result?.error) {
            return this.result.error;
        }
        const files = this.visibleFiles();
        if (!this.result || files.length === 0) {
            // Everything filtered away by the reviewed filter is a different answer from
            // nothing having changed.
            if (!showsReviewed() && (this.result?.files.length ?? 0) > 0) {
                return 'nothing left to review';
            }
            return scope() === 'branch' ? 'no changes on this branch' : 'no uncommitted changes';
        }
        const csFiles = files.filter((f) => !isOtherFile(f.filePath));
        const others = files.length - csFiles.length;
        const members = files.reduce((sum, f) => sum + f.members.length, 0);
        const counted = (count: number, noun: string) => `${count} ${noun}${count === 1 ? '' : 's'}`;
        const what = [
            csFiles.length > 0
                ? `${counted(members, 'member')} in ${counted(csFiles.length, 'file')}`
                : '',
            others > 0 ? counted(others, 'other file') : '',
        ]
            .filter(Boolean)
            .join(', ');
        return scope() === 'branch' ? `${what} — whole branch` : what;
    }

    getChildren(node?: Node): Node[] {
        if (!node) {
            return this.roots;
        }
        if (node.kind === 'member') {
            return hasBlockChildren(node)
                ? visibleBlocks(node.row.member).map(
                      (block): BlockNode => ({ kind: 'block', row: node.row, block })
                  )
                : [];
        }
        return node.kind === 'block' ? [] : node.children;
    }

    getTreeItem(node: Node): vscode.TreeItem {
        if (node.kind === 'file') {
            const item = new vscode.TreeItem(
                vscode.workspace.asRelativePath(node.filePath),
                node.children.length > 0
                    ? vscode.TreeItemCollapsibleState.Expanded
                    : vscode.TreeItemCollapsibleState.None
            );
            item.resourceUri = vscode.Uri.file(node.filePath);
            const fileState = this.reviewStateOf(node);
            item.iconPath = fileState !== 'open'
                ? new vscode.ThemeIcon('check', new vscode.ThemeColor('testing.iconPassed'))
                : node.isTest
                  ? new vscode.ThemeIcon('beaker')
                  : vscode.ThemeIcon.File;
            item.tooltip = reviewTooltip(fileState);
            if (node.deleted) {
                item.description = 'deleted';
            } else if (node.wholeFile) {
                item.description = 'new';
            }
            // A file with nothing under it is its own click target — non-C# files always,
            // and a C# file the parser could not break down.
            if (node.children.length === 0) {
                item.command = clickCommand(node, 'Go to First Change');
            }
            item.contextValue =
                (isOtherFile(node.filePath) ? 'changedOtherFile' : 'changedFile') +
                reviewSuffix(fileState);
            return item;
        }

        if (node.kind === 'block') {
            const { block } = node;
            const item = new vscode.TreeItem(
                block.preview || '(blank line)',
                vscode.TreeItemCollapsibleState.None
            );
            item.description = block.endLine > block.startLine
                ? `${block.startLine}–${block.endLine}`
                : `${block.startLine}`;
            item.iconPath = block.staged
                ? new vscode.ThemeIcon('check', new vscode.ThemeColor('testing.iconPassed'))
                : new vscode.ThemeIcon('diff-modified');
            item.tooltip = block.staged
                ? `Lines ${block.startLine}–${block.endLine} — staged.`
                : `Lines ${block.startLine}–${block.endLine}`;
            item.command = clickCommand(node, 'Go to Change');
            item.contextValue = 'changedBlock';
            return item;
        }

        if (node.kind !== 'member') {
            const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.Expanded);
            const groupState = this.reviewStateOf(node);
            item.iconPath = groupState !== 'open'
                ? new vscode.ThemeIcon('check', new vscode.ThemeColor('testing.iconPassed'))
                : new vscode.ThemeIcon(
                      node.kind === 'namespace' ? 'symbol-namespace' : 'symbol-class'
                  );
            item.tooltip = reviewTooltip(groupState);
            item.contextValue = node.kind + reviewSuffix(groupState);
            return item;
        }

        const { member } = node.row;
        const item = new vscode.TreeItem(
            member.name,
            hasBlockChildren(node)
                ? vscode.TreeItemCollapsibleState.Collapsed
                : vscode.TreeItemCollapsibleState.None
        );
        const tags = [
            node.detail,
            member.removed ? 'removed' : '',
            node.row.isTest && viewMode() !== 'simpleTree' ? 'test' : '',
        ].filter(Boolean);
        item.description = tags.join(' · ');

        const staged = member.staged;
        const reviewedAs = this.reviews.fingerprintAtReview(reviewKey(node.row));
        const stale = !staged && reviewedAs !== undefined && reviewedAs !== fingerprint(node.row);
        item.iconPath = staged || reviewedAs !== undefined
            ? new vscode.ThemeIcon(
                  'check',
                  new vscode.ThemeColor(stale ? 'disabledForeground' : 'testing.iconPassed')
              )
            : member.removed
              ? new vscode.ThemeIcon(
                    iconFor(member.kind),
                    new vscode.ThemeColor('gitDecoration.deletedResourceForeground')
                )
              : new vscode.ThemeIcon(iconFor(member.kind));

        item.resourceUri = vscode.Uri.file(node.row.filePath);
        item.tooltip = new vscode.MarkdownString(
            [
                `**${qualifiedName(member)}**`,
                '',
                member.removed
                    ? `Removed — ${member.changedLineCount} deleted line${member.changedLineCount === 1 ? '' : 's'}; ` +
                      'the line points at where it used to be.'
                    : node.row.wholeFile
                      ? 'New file — everything is a change.'
                      : `${member.changedLineCount} changed line${member.changedLineCount === 1 ? '' : 's'}, ` +
                        `first at ${member.firstChangedLine}`,
                staged
                    ? 'Staged — counted as reviewed.'
                    : stale
                      ? 'Reviewed, but changed again since.'
                      : reviewedAs !== undefined
                        ? 'Reviewed.'
                        : '',
                vscode.workspace.asRelativePath(node.row.filePath),
            ]
                .filter(Boolean)
                .join('\n\n')
        );
        item.command = clickCommand(node, 'Go to First Change');
        // A staged member is reviewed by the index, not by a tick, so neither the tick nor
        // the untick action applies to it.
        item.contextValue = staged
            ? 'changedMemberStaged'
            : reviewedAs !== undefined
              ? 'changedMemberReviewed'
              : 'changedMember';
        return item;
    }
}

/**
 * The context value's tail, which is all a menu can see of the review state: a row with a tick
 * to take back offers to take it back, one that staging covers offers neither action.
 */
function reviewSuffix(state: 'open' | 'reviewed' | 'staged'): string {
    return state === 'open' ? '' : state === 'staged' ? 'Staged' : 'Reviewed';
}

function reviewTooltip(state: 'open' | 'reviewed' | 'staged'): string | undefined {
    return state === 'staged'
        ? 'Staged — counted as reviewed.'
        : state === 'reviewed'
          ? 'Reviewed.'
          : undefined;
}

function clickCommand(node: FileNode | MemberNode | BlockNode, title: string): vscode.Command {
    return {
        command: opensInDiff()
            ? 'roslynSense.changedMembers.openDiff'
            : 'roslynSense.changedMembers.openFile',
        title,
        arguments: [node],
    };
}

export function registerChangedMembers(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const reviews = new ReviewStore(context.workspaceState);
    const provider = new ChangedMembersProvider(getClient, reviews);
    const view = vscode.window.createTreeView('roslynSense.changedMembers', {
        treeDataProvider: provider,
        showCollapseAll: true,
    });

    const refresh = async () => {
        await provider.refresh();
        view.description = provider.describe();
    };

    // The ... menu writes the same settings the Settings editor does; the context keys mirror
    // them so each menu entry can hide the state the view is already in.
    const setSetting = (key: string, value: string | boolean) =>
        config().update(`changedMembers.${key}`, value, vscode.ConfigurationTarget.Global);

    context.subscriptions.push(
        view,
        vscode.commands.registerCommand('roslynSense.refreshChangedMembers', refresh),
        vscode.commands.registerCommand('roslynSense.changedMembers.openFile', (node) =>
            openInEditor(node, provider.diffBaseRef)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.openDiff', (node) =>
            openInDiffView(node, provider.diffBaseRef)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.showBlocks', () =>
            setSetting('showBlocks', true)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.hideBlocks', () =>
            setSetting('showBlocks', false)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.showTests', () =>
            setSetting('showTests', true)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.hideTests', () =>
            setSetting('showTests', false)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.showOtherFiles', () =>
            setSetting('showOtherFiles', true)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.showReviewed', () =>
            setSetting('showReviewed', true)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.hideReviewed', () =>
            setSetting('showReviewed', false)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.hideOtherFiles', () =>
            setSetting('showOtherFiles', false)
        ),
        // A member, a type, a namespace or a whole file: whatever the row stands for, ticking
        // it off ticks off every member under it.
        vscode.commands.registerCommand('roslynSense.changedMembers.markReviewed', (node: Node) =>
            provider.mark(node)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.unmarkReviewed', (node: Node) =>
            provider.unmark(node)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.nextChange', () =>
            step(provider, refresh, 1)
        ),
        vscode.commands.registerCommand('roslynSense.changedMembers.previousChange', () =>
            step(provider, refresh, -1)
        )
    );

    // Every option of a radio group keeps its row in the ... menu, with a check on the one in
    // force. VS Code paints that check from a toggled state extensions cannot contribute, so
    // each option has a twin command whose title carries the tick; both set the same value, and
    // the menu shows whichever of the two matches the setting.
    const options = [
        ['viewSimpleTree', 'view', 'simpleTree'],
        ['viewFullTree', 'view', 'fullTree'],
        ['viewFlat', 'view', 'flat'],
        ['clickOpensEditor', 'openIn', 'editor'],
        ['clickOpensDiff', 'openIn', 'gitDiff'],
        ['scopeUncommitted', 'scope', 'uncommitted'],
        ['scopeBranch', 'scope', 'branch'],
    ] as const;

    for (const [command, setting, value] of options) {
        for (const id of [command, `${command}Active`]) {
            context.subscriptions.push(
                vscode.commands.registerCommand(`roslynSense.changedMembers.${id}`, () =>
                    setSetting(setting, value)
                )
            );
        }
    }

    syncContext();

    // Filled in when the view is first shown rather than on activation: the diff is cheap, but
    // there is no reason to take it for a window that never opens the view.
    context.subscriptions.push(
        view.onDidChangeVisibility((event) => {
            if (event.visible) {
                void refresh();
            }
        })
    );

    // A save is the moment the working tree — and so the diff — actually changes. Debounced,
    // because Save All fires one event per file.
    let pending: ReturnType<typeof setTimeout> | undefined;
    const queueRefresh = () => {
        if (!view.visible) {
            return;
        }
        clearTimeout(pending);
        pending = setTimeout(() => void refresh(), 500);
    };

    context.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument((document) => {
            if (document.fileName.endsWith('.cs') || showsOtherFiles()) {
                queueRefresh();
            }
        }),
        { dispose: () => clearTimeout(pending) },
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (!event.affectsConfiguration('roslynSense.changedMembers')) {
                return;
            }
            syncContext();
            if (event.affectsConfiguration('roslynSense.changedMembers.scope')) {
                // A different comparison is a different diff, not a different grouping.
                void refresh();
            } else {
                provider.rebuild();
                view.description = provider.describe();
            }
        })
    );

    // Commits, checkouts and stashes change the diff without a single editor save. HEAD moves
    // on checkout/commit, the index on stage/stash — watching both covers the git side.
    const gitDir = findGitDir(vscode.workspace.workspaceFolders?.[0]?.uri.fsPath);
    if (gitDir) {
        const watcher = vscode.workspace.createFileSystemWatcher(
            new vscode.RelativePattern(vscode.Uri.file(gitDir), '{HEAD,index}')
        );
        context.subscriptions.push(
            watcher,
            watcher.onDidChange(queueRefresh),
            watcher.onDidCreate(queueRefresh)
        );
    }
}

/** Mirrors the settings into context keys, which is the only state a menu `when` can see. */
function syncContext(): void {
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.changedMembersView', viewMode());
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.changedMembersOpenIn', opensInDiff() ? 'gitDiff' : 'editor');
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.changedMembersBlocks', showsBlocks());
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.changedMembersScope', scope());
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.changedMembersTests', showsTests());
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.changedMembersOtherFiles', showsOtherFiles());
    void vscode.commands.executeCommand(
        'setContext', 'roslynSense.changedMembersReviewed', showsReviewed());
}

/**
 * Where the repository's own files live. `.git` is a directory in a normal clone, but a file
 * containing `gitdir: <path>` in a worktree or submodule.
 */
function findGitDir(start: string | undefined): string | undefined {
    for (let directory = start; directory; ) {
        const dotGit = path.join(directory, '.git');
        try {
            const stat = fs.statSync(dotGit);
            if (stat.isDirectory()) {
                return dotGit;
            }
            const target = fs.readFileSync(dotGit, 'utf8').match(/^gitdir:\s*(.+)$/m)?.[1].trim();
            return target ? path.resolve(directory, target) : undefined;
        } catch {
            // No .git here; keep walking up.
        }
        const parent = path.dirname(directory);
        if (parent === directory) {
            break;
        }
        directory = parent;
    }
    return undefined;
}

/**
 * Jump to the change after (or before) the cursor, in view order, wrapping at the ends. The
 * whole diff can be walked this way without touching the tree.
 */
async function step(
    provider: ChangedMembersProvider,
    refresh: () => Promise<void>,
    direction: 1 | -1
): Promise<void> {
    let targets = provider.targets();
    if (targets.length === 0) {
        await refresh();
        targets = provider.targets();
        if (targets.length === 0) {
            void vscode.window.showInformationMessage('RoslynSense: no changes to step through.');
            return;
        }
    }

    const editor = vscode.window.activeTextEditor;
    const herePath = editor?.document.uri.fsPath ?? '';
    const hereLine = editor ? editor.selection.active.line + 1 : 0;

    const after = (t: { filePath: string; line: number }) => {
        const order = t.filePath.localeCompare(herePath) || t.line - hereLine;
        return direction === 1 ? order > 0 : order < 0;
    };

    const candidates = direction === 1 ? targets : [...targets].reverse();
    const target = candidates.find(after) ?? candidates[0];

    const line = Math.max(0, target.line - 1);
    const selection = new vscode.Range(line, 0, line, 0);
    if (opensInDiff()) {
        await showDiff(vscode.Uri.file(target.filePath), selection, provider.diffBaseRef);
    } else {
        await vscode.window.showTextDocument(vscode.Uri.file(target.filePath), { selection });
    }
}

function targetOf(node: FileNode | MemberNode | BlockNode): { uri: vscode.Uri; selection: vscode.Range } {
    const line = Math.max(
        0,
        (node.kind === 'file'
            ? node.firstChangedLine
            : node.kind === 'block'
              ? node.block.startLine
              : node.row.member.firstChangedLine) - 1
    );
    const filePath = node.kind === 'file' ? node.filePath : node.row.filePath;
    return {
        uri: vscode.Uri.file(filePath),
        selection: new vscode.Range(line, 0, line, 0),
    };
}

async function openInEditor(node: FileNode | MemberNode | BlockNode, baseRef: string): Promise<void> {
    const { uri, selection } = targetOf(node);
    if (!fs.existsSync(uri.fsPath)) {
        await showBase(uri, selection, baseRef);
        return;
    }
    await vscode.window.showTextDocument(uri, { selection });
}

async function openInDiffView(node: FileNode | MemberNode | BlockNode, baseRef: string): Promise<void> {
    const { uri, selection } = targetOf(node);
    if (!fs.existsSync(uri.fsPath)) {
        await showBase(uri, selection, baseRef);
        return;
    }
    await showDiff(uri, selection, baseRef);
}

function gitApi(): { toGitUri(uri: vscode.Uri, ref: string): vscode.Uri } | undefined {
    const git = vscode.extensions.getExtension<GitExtension>('vscode.git')?.exports;
    return git?.enabled ? git.getAPI(1) : undefined;
}

/**
 * The base revision's version of a file the working tree no longer has — the only side of a
 * deleted file left to open. Without the Git extension there is nothing to show.
 */
async function showBase(uri: vscode.Uri, selection: vscode.Range, baseRef: string): Promise<void> {
    const api = gitApi();
    if (!api) {
        void vscode.window.showInformationMessage(
            `RoslynSense: ${basename(uri.fsPath)} was deleted, and the Git extension is not available to show what it was.`
        );
        return;
    }
    await vscode.window.showTextDocument(api.toGitUri(uri, baseRef), { selection });
}

/**
 * The same spot, but beside what it replaced: the diff base on the left, the working tree on
 * the right, scrolled to the changed line. The left side comes from the built-in Git
 * extension's content provider; without it (git disabled) the plain editor is all there is.
 */
async function showDiff(uri: vscode.Uri, selection: vscode.Range, baseRef: string): Promise<void> {
    const api = gitApi();
    if (!api) {
        await vscode.window.showTextDocument(uri, { selection });
        return;
    }

    await vscode.commands.executeCommand(
        'vscode.diff',
        api.toGitUri(uri, baseRef),
        uri,
        `${basename(uri.fsPath)} (Working Tree)`,
        { selection }
    );
}

/** The sliver of the built-in Git extension's API this view touches. */
interface GitExtension {
    enabled: boolean;
    getAPI(version: 1): { toGitUri(uri: vscode.Uri, ref: string): vscode.Uri };
}

function basename(filePath: string): string {
    return filePath.replace(/^.*[\\/]/, '');
}

function build(files: ChangedMembersFileInfo[], mode: ViewMode): Node[] {
    const rows: Row[] = files.flatMap((file) =>
        file.members.map((member) => rowOf(file, member))
    );

    // Files with no member breakdown — non-C# files, or C# the parser could not read. The
    // grouped modes have nothing to group them by, so they trail the members as file rows.
    const bareFiles = files
        .filter((file) => file.members.length === 0)
        .sort((a, b) => a.filePath.localeCompare(b.filePath))
        .map((file): FileNode => ({
            kind: 'file',
            filePath: file.filePath,
            wholeFile: file.wholeFile,
            isTest: file.isTest,
            firstChangedLine: file.firstChangedLine,
            staged: file.staged,
            changedLineCount: file.changedLineCount,
            deleted: file.deleted,
            children: [],
        }));

    switch (mode) {
        case 'flat':
            return [
                ...rows
                    .sort(byLocation)
                    .map((row) => {
                        const { namespace, containerType } = row.member;
                        const container = namespace ? `${namespace}.${containerType}` : containerType;
                        return memberNode(row, withLine(row.member, container));
                    }),
                ...bareFiles,
            ];

        case 'fullTree':
            return [...buildFullTree(rows), ...bareFiles];

        default:
            return files.map((file): FileNode => ({
                kind: 'file',
                filePath: file.filePath,
                wholeFile: file.wholeFile,
                isTest: file.isTest,
                firstChangedLine: file.firstChangedLine,
                staged: file.staged,
                changedLineCount: file.changedLineCount,
                deleted: file.deleted,
                children: file.members.map((member) =>
                    memberNode(rowOf(file, member), withLine(member, member.containerType))
                ),
            }));
    }
}

/**
 * namespace → type → member. Every member nests under both levels, however few share
 * them: a row's place in the tree is where it belongs, not how crowded it is.
 */
function buildFullTree(rows: Row[]): Node[] {
    const namespaces = new Map<string, Row[]>();
    for (const row of rows) {
        const ns = row.member.namespace || '(global)';
        const list = namespaces.get(ns);
        if (list) {
            list.push(row);
        } else {
            namespaces.set(ns, [row]);
        }
    }

    const roots: Node[] = [];
    for (const [ns, inNamespace] of [...namespaces].sort(([a], [b]) => a.localeCompare(b))) {
        const types = new Map<string, Row[]>();
        for (const row of inNamespace) {
            const type = row.member.containerType || '(no type)';
            const inType = types.get(type);
            if (inType) {
                inType.push(row);
            } else {
                types.set(type, [row]);
            }
        }

        roots.push({
            kind: 'namespace',
            label: ns,
            children: [...types]
                .sort(([a], [b]) => a.localeCompare(b))
                .map(([type, inType]): GroupNode => ({
                    kind: 'type',
                    label: type,
                    children: inType
                        .sort(byLocation)
                        .map((row) => memberNode(row, withLine(row.member, ''))),
                })),
        });
    }
    return roots;
}

function memberNode(row: Row, detail: string): MemberNode {
    return { kind: 'member', row, detail };
}

function qualifiedName(member: ChangedMemberInfo): string {
    return member.containerType ? `${member.containerType}.${member.name}` : member.name;
}

function withLine(member: ChangedMemberInfo, prefix: string): string {
    const line = `:${member.firstChangedLine}`;
    return prefix ? `${prefix}${line}` : line;
}

function byLocation(a: Row, b: Row): number {
    return a.filePath.localeCompare(b.filePath) || a.member.startLine - b.member.startLine;
}

function iconFor(kind: string): string {
    switch (kind) {
        case 'property':
            return 'symbol-property';
        case 'field':
            return 'symbol-field';
        case 'event':
            return 'symbol-event';
        case 'operator':
            return 'symbol-operator';
        default:
            return 'symbol-method';
    }
}
