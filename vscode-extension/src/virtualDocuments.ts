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
                // A banner rather than a read-only flag: the scheme is already read-only, and
                // what the reader actually needs is to know why the file has no path.
                return `// ${document.description}\n\n${document.text}`;
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
