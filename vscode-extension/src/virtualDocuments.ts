import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * Documents the compiler knows about but the file system does not: source-generated files and
 * decompiled metadata.
 *
 * One provider serves both schemes. Content is fetched from the daemon rather than written to a
 * temp file, because a generator's output exists only inside the compilation — a copy on disk is
 * stale the moment the source it was generated from changes.
 */

const GENERATED_SCHEME = 'roslynsense-generated';
const METADATA_SCHEME = 'roslynsense-metadata';

interface VirtualDocument {
    text: string;
    description: string;
    languageId: string;
}

export function registerVirtualDocuments(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const changeEmitter = new vscode.EventEmitter<vscode.Uri>();

    /** What each open virtual document is, shown on open instead of inside the text. */
    const descriptions = new Map<string, string>();

    const provider: vscode.TextDocumentContentProvider = {
        onDidChange: changeEmitter.event,

        async provideTextDocumentContent(uri, token) {
            const client = getClient();
            if (!client) {
                return '// The RoslynSense server is not running.';
            }

            try {
                const document = await client.sendRequest<VirtualDocument | null>(
                    'roslynSense/virtualDocument',
                    { uri: uri.toString() },
                    token
                );
                if (!document) {
                    return `// Could not load ${uri.path}.\n// The generator may no longer produce it, or the assembly could not be read.`;
                }

                descriptions.set(uri.toString(), document.description);

                // Generated documents exchange positions with the server, so their text has to
                // be exactly what the compilation holds — a banner would shift every line by
                // two and put go-to-definition and diagnostics on the wrong ones. Decompiled
                // metadata is read-only prose with no such traffic, so it keeps its header.
                return uri.scheme === GENERATED_SCHEME
                    ? document.text
                    : `// ${document.description}\n\n${document.text}`;
            } catch (err) {
                return `// Could not load ${uri.path}: ${String(err)}`;
            }
        },
    };

    context.subscriptions.push(
        changeEmitter,
        vscode.workspace.registerTextDocumentContentProvider(GENERATED_SCHEME, provider),
        vscode.workspace.registerTextDocumentContentProvider(METADATA_SCHEME, provider),

        vscode.commands.registerCommand(
            'roslynSense.openVirtualDocument',
            async (uri: string | vscode.Uri) => {
                const parsed = typeof uri === 'string' ? vscode.Uri.parse(uri) : uri;
                const document = await vscode.workspace.openTextDocument(parsed);
                await vscode.languages.setTextDocumentLanguage(document, 'csharp');
                await vscode.window.showTextDocument(document, { preview: true });

                const description = descriptions.get(parsed.toString());
                if (description) {
                    vscode.window.setStatusBarMessage(description, 6000);
                }
            }
        ),

        // Generated output changes whenever its inputs do; re-fetch what is open on save rather
        // than leaving a stale buffer that quietly disagrees with the compiler.
        vscode.workspace.onDidSaveTextDocument(() => {
            for (const open of vscode.workspace.textDocuments) {
                if (open.uri.scheme === GENERATED_SCHEME) {
                    changeEmitter.fire(open.uri);
                }
            }
        })
    );
}
