import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * Publishes what the user is looking at, so an AI chat can resolve "this method" / "this
 * error" to what is actually on screen rather than guessing.
 *
 * Debounced, and off entirely behind `roslynSense.shareEditorContext`. Only paths, the cursor,
 * the current selection, and the diagnostics already visible in the active editor are sent —
 * never whole file contents.
 */

interface VisibleDiagnostic {
    severity: string;
    code: string | null;
    message: string;
    line: number;
}

export function registerEditorContext(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined,
    getSolutionPath: () => string | undefined
): void {
    let timer: NodeJS.Timeout | undefined;

    const enabled = () =>
        vscode.workspace.getConfiguration('roslynSense').get<boolean>('shareEditorContext', true);

    const publish = async () => {
        const client = getClient();
        const solutionPath = getSolutionPath();
        if (!client || !solutionPath || !enabled()) {
            return;
        }

        const editor = vscode.window.activeTextEditor;
        const document = editor?.document;
        const position = editor?.selection.active;

        let enclosingSymbol: string | undefined;
        if (document && position) {
            enclosingSymbol = await findEnclosingSymbol(document.uri, position);
        }

        const selection =
            editor && !editor.selection.isEmpty
                ? editor.document.getText(editor.selection)
                : undefined;

        const diagnostics: VisibleDiagnostic[] = document
            ? vscode.languages.getDiagnostics(document.uri).map((d) => ({
                  severity: vscode.DiagnosticSeverity[d.severity],
                  code: typeof d.code === 'object' ? String(d.code.value) : (d.code ?? null)?.toString() ?? null,
                  message: d.message,
                  line: d.range.start.line,
              }))
            : [];

        const open = vscode.workspace.textDocuments.filter((d) => d.uri.scheme === 'file');

        try {
            await client.sendNotification('roslynSense/editorContext', {
                solutionPath,
                activeFile: document?.uri.fsPath ?? null,
                line: position?.line ?? 0,
                character: position?.character ?? 0,
                enclosingSymbol: enclosingSymbol ?? null,
                // Cap the selection: this is context, not a file transfer.
                selectionText: selection ? selection.slice(0, 8000) : null,
                openFiles: open.map((d) => d.uri.fsPath),
                dirtyFiles: open.filter((d) => d.isDirty).map((d) => d.uri.fsPath),
                diagnostics,
            });
        } catch {
            // Advisory; a failed report must never disturb the editor.
        }
    };

    const schedule = () => {
        clearTimeout(timer);
        timer = setTimeout(() => void publish(), 750);
    };

    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(schedule),
        vscode.window.onDidChangeTextEditorSelection(schedule),
        vscode.workspace.onDidOpenTextDocument(schedule),
        vscode.workspace.onDidCloseTextDocument(schedule),
        vscode.workspace.onDidSaveTextDocument(schedule),
        vscode.languages.onDidChangeDiagnostics(schedule),
        new vscode.Disposable(() => clearTimeout(timer))
    );

    schedule();
}

/** The symbol containing the cursor, as "Class.Method" where possible. */
async function findEnclosingSymbol(
    uri: vscode.Uri,
    position: vscode.Position
): Promise<string | undefined> {
    try {
        const symbols = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>(
            'vscode.executeDocumentSymbolProvider',
            uri
        );
        if (!symbols?.length) {
            return undefined;
        }

        const path: string[] = [];
        let current: vscode.DocumentSymbol[] | undefined = symbols;
        while (current) {
            const match: vscode.DocumentSymbol | undefined = current.find((symbol) =>
                symbol.range.contains(position)
            );
            if (!match) {
                break;
            }
            path.push(match.name);
            current = match.children;
        }
        return path.length ? path.join('.') : undefined;
    } catch {
        return undefined;
    }
}
