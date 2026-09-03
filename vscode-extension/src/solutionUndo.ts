import * as vscode from 'vscode';

/**
 * Undo and redo for the Solution Explorer's own edits.
 *
 * VS Code's undo is per text document. A tree view gets nothing: removing a package, deleting a
 * file or renaming a folder from a contributed view leaves the editor's undo stack untouched, so
 * Ctrl+Z after one of those either does nothing or — worse — undoes a typing edit in whatever
 * document was last focused. That is why this exists rather than a call into the editor's stack:
 * there is no API to push onto it, and the operations here are not text edits at all.
 *
 * Every step is an explicit pair. Nothing is inferred by replaying an edit backwards, because the
 * inverse of "delete" is not another edit — it is content that no longer exists anywhere unless
 * something captured it first. A step is recorded only when its inverse is exact; an operation
 * whose inverse would be a guess records nothing, and Ctrl+Z reaches past it to the last step that
 * can honestly be undone.
 */
export interface UndoStep {
    /** What is shown when the step is undone or redone: "Delete Program.cs". */
    readonly label: string;
    undo(): Promise<void>;
    redo(): Promise<void>;
}

/**
 * A bounded undo/redo history.
 *
 * Bounded because the steps hold file content: an unbounded history of deletes is a memory leak
 * shaped like a feature.
 */
export class UndoStack {
    private readonly done: UndoStep[] = [];
    private readonly undone: UndoStep[] = [];

    constructor(private readonly limit = 50) {}

    get canUndo(): boolean {
        return this.done.length > 0;
    }

    get canRedo(): boolean {
        return this.undone.length > 0;
    }

    push(step: UndoStep): void {
        this.done.push(step);
        if (this.done.length > this.limit) {
            this.done.shift();
        }
        // A new edit is a new branch of history; what was undone before it can no longer be redone
        // on top of it, exactly as in a text editor.
        this.undone.length = 0;
    }

    /** Undoes the last step and returns its label, or undefined when there is nothing to undo. */
    async undo(): Promise<string | undefined> {
        const step = this.done.pop();
        if (!step) {
            return undefined;
        }

        try {
            await step.undo();
        } catch (error) {
            // A step that could not be undone is not put back: the world is in whatever state the
            // failure left it, and offering to undo it again would compound that rather than fix
            // it. The caller reports the error.
            throw error;
        }
        this.undone.push(step);
        return step.label;
    }

    async redo(): Promise<string | undefined> {
        const step = this.undone.pop();
        if (!step) {
            return undefined;
        }

        await step.redo();
        this.done.push(step);
        return step.label;
    }

    clear(): void {
        this.done.length = 0;
        this.undone.length = 0;
    }
}

/** Several edits that happened together — a multi-select delete — undone as one. */
export function composite(label: string, steps: UndoStep[]): UndoStep {
    return {
        label,
        // Reverse order: the steps ran forwards, so undoing them backwards is the only order that
        // is safe when they touch the same place — a file created inside a folder that was also
        // created has to go before the folder does.
        undo: async () => {
            for (const step of [...steps].reverse()) {
                await step.undo();
            }
        },
        redo: async () => {
            for (const step of steps) {
                await step.redo();
            }
        },
    };
}

/** A file or directory captured whole, so deleting it is reversible. */
export interface Snapshot {
    readonly uri: vscode.Uri;
    readonly isDirectory: boolean;
    /** Relative path → bytes. One entry, keyed "", for a single file. */
    readonly files: Map<string, Uint8Array>;
}

/**
 * How much a single snapshot may hold. Past this, no snapshot is taken and the operation records
 * no undo step at all — the alternative is holding hundreds of megabytes of build output in
 * memory on the chance that somebody presses Ctrl+Z.
 */
const SnapshotLimitBytes = 32 * 1024 * 1024;

/**
 * Captures what is at a uri, or undefined when it is missing or too large to hold.
 *
 * Bytes rather than text: the tree deletes .resx, .ico and .snk as readily as .cs, and restoring
 * a binary through a string round-trip corrupts it.
 */
export async function snapshot(uri: vscode.Uri): Promise<Snapshot | undefined> {
    let stat: vscode.FileStat;
    try {
        stat = await vscode.workspace.fs.stat(uri);
    } catch {
        return undefined;
    }

    const files = new Map<string, Uint8Array>();
    let total = 0;

    const readFile = async (from: vscode.Uri, key: string): Promise<boolean> => {
        const content = await vscode.workspace.fs.readFile(from);
        total += content.byteLength;
        if (total > SnapshotLimitBytes) {
            return false;
        }
        files.set(key, content);
        return true;
    };

    try {
        if (stat.type & vscode.FileType.Directory) {
            const walk = async (directory: vscode.Uri, prefix: string): Promise<boolean> => {
                for (const [name, type] of await vscode.workspace.fs.readDirectory(directory)) {
                    const child = vscode.Uri.joinPath(directory, name);
                    const key = prefix ? `${prefix}/${name}` : name;
                    const ok =
                        type & vscode.FileType.Directory
                            ? await walk(child, key)
                            : await readFile(child, key);
                    if (!ok) {
                        return false;
                    }
                }
                // An empty directory is worth recording: it is part of what was deleted.
                if (!prefix) {
                    return true;
                }
                if (![...files.keys()].some((key) => key.startsWith(`${prefix}/`))) {
                    files.set(`${prefix}/`, new Uint8Array());
                }
                return true;
            };

            if (!(await walk(uri, ''))) {
                return undefined;
            }
            return { uri, isDirectory: true, files };
        }

        if (!(await readFile(uri, ''))) {
            return undefined;
        }
        return { uri, isDirectory: false, files };
    } catch {
        return undefined;
    }
}

/** Puts a snapshot back where it came from. */
export async function restore(captured: Snapshot): Promise<void> {
    if (!captured.isDirectory) {
        await vscode.workspace.fs.writeFile(captured.uri, captured.files.get('') ?? new Uint8Array());
        return;
    }

    await vscode.workspace.fs.createDirectory(captured.uri);
    for (const [relative, content] of captured.files) {
        const target = vscode.Uri.joinPath(captured.uri, relative);
        if (relative.endsWith('/')) {
            await vscode.workspace.fs.createDirectory(target);
        } else {
            await vscode.workspace.fs.writeFile(target, content);
        }
    }
}
