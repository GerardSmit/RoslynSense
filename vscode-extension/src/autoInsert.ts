import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

interface AutoInsertResult {
    edit: { range: unknown; newText: string };
    cursor: { line: number; character: number };
}

/** Insert an XML documentation skeleton only while the triggering buffer is unchanged. */
export function registerOnAutoInsert(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined,
    code2Protocol: (uri: vscode.Uri) => string,
): void {
    context.subscriptions.push(vscode.workspace.onDidChangeTextDocument(async (event) => {
        const connection = getClient();
        const document = event.document;
        const change = event.contentChanges[0];
        if (!connection || document.languageId !== 'csharp' || event.contentChanges.length !== 1
            || change.text !== '/' || change.rangeLength !== 0) {
            return;
        }

        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document !== document) {
            return;
        }
        const position = change.range.start.translate(0, 1);
        const linePrefix = document.lineAt(position.line).text.substring(0, position.character);
        if (linePrefix.trimStart() !== '///') {
            return;
        }

        // TextDocument is a live object: comparing editor.document.version with
        // event.document.version after an await compares the same updated version twice.
        const version = document.version;
        try {
            const result = await connection.sendRequest<AutoInsertResult | null>(
                'roslynSense/onAutoInsert', {
                    textDocument: { uri: code2Protocol(document.uri) },
                    position: { line: position.line, character: position.character },
                });
            if (!result || document.isClosed || document.version !== version
                || vscode.window.activeTextEditor !== editor || getClient() !== connection) {
                return;
            }

            const applied = await editor.edit(
                (builder) => builder.insert(position, result.edit.newText),
                { undoStopBefore: true, undoStopAfter: true });
            if (applied && vscode.window.activeTextEditor === editor) {
                const cursor = new vscode.Position(result.cursor.line, result.cursor.character);
                editor.selection = new vscode.Selection(cursor, cursor);
            }
        } catch {
            // Typing during a disconnect must remain an ordinary edit, not an unhandled
            // rejection from a workspace event listener.
        }
    }));
}
