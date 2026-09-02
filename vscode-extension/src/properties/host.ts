import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { html } from './html';

/**
 * The extension-host half of the Properties panel: it talks to the server.
 *
 * The panel is a thin form over two requests. Everything it shows comes from
 * `roslynSense/itemProperties`, and every control it offers sends exactly one field to
 * `roslynSense/setItemProperties` — the server decides what that means for the project file,
 * because it is the half that knows whether the item is named or swept up by a wildcard.
 *
 * The panel is retargetable: opening Properties on another node points this one at it rather than
 * opening a second tab, which is how the Settings editor and the NuGet panel behave and is what
 * keeps a right-click habit from filling the tab bar. The returned function is how the command
 * retargets it.
 */
export function wire(
    context: vscode.ExtensionContext,
    panel: vscode.WebviewPanel,
    initialPath: string | undefined,
    onDispose: () => void,
    getClient: () => LanguageClient | undefined
): (path: string) => void {
    panel.webview.html = html(panel.webview, context.extensionUri);

    let target = initialPath;
    let current: PropsMsg.Properties | undefined;
    let disposed = false;

    panel.onDidDispose(() => {
        disposed = true;
        onDispose();
    });

    const post = (message: PropsMsg.ToView) => {
        if (!disposed) {
            void panel.webview.postMessage(message);
        }
    };

    function show(properties: PropsMsg.Properties, notice?: string): void {
        current = properties;
        panel.title = `Properties: ${basename(properties.path)}`;
        post({ type: 'state', properties, notice });
    }

    async function refresh(notice?: string): Promise<void> {
        const client = getClient();

        if (!target) {
            post({ type: 'failed', message: 'Nothing selected.' });
            return;
        }

        if (!client) {
            post({ type: 'failed', message: 'The language server is not running yet.' });
            return;
        }

        try {
            show(
                await client.sendRequest<PropsMsg.Properties>('roslynSense/itemProperties', {
                    path: target,
                }),
                notice
            );
        } catch (error) {
            post({ type: 'failed', message: messageOf(error) });
        }
    }

    /**
     * A write, and then the answer that write produced.
     *
     * The server returns the fresh properties alongside its result, so the page is redrawn from
     * what the project now says rather than from what the control was set to: a build action that
     * also moved the file out of a glob shows both changes, and a write the server declined
     * leaves the form showing the truth instead of the request.
     */
    async function apply(message: PropsMsg.Apply): Promise<void> {
        const client = getClient();

        if (!target || !client) {
            return;
        }

        try {
            const result = await client.sendRequest<{
                ok: boolean;
                message: string;
                properties?: PropsMsg.Properties | null;
            }>('roslynSense/setItemProperties', {
                path: target,
                itemType: message.itemType ?? null,
                copyToOutputDirectory: message.copyToOutputDirectory ?? null,
                generator: message.generator ?? null,
                customToolNamespace: message.customToolNamespace ?? null,
                namespaceProvider: message.namespaceProvider ?? null,
            });

            if (result.ok && result.properties) {
                show(result.properties, result.message);
                return;
            }

            await refresh(result.message);
        } catch (error) {
            await refresh(messageOf(error));
        }
    }

    async function reveal(which: 'project' | 'declaredIn'): Promise<void> {
        const file =
            which === 'project' ? current?.projectPath : current?.file?.declaredIn ?? null;

        if (file) {
            await vscode.window.showTextDocument(vscode.Uri.file(file), {
                viewColumn: vscode.ViewColumn.One,
            });
        }
    }

    panel.webview.onDidReceiveMessage(async (message: PropsMsg.ToHost) => {
        switch (message.type) {
            case 'ready':
                await refresh();
                break;
            case 'apply':
                await apply(message);
                break;
            case 'reveal':
                await reveal(message.target);
                break;
        }
    });

    // The project file and the settings layer are what the panel is a view of, so an edit to
    // either — by this panel, by the tree, or by hand in an editor — is what makes the form stale.
    const watcher = vscode.workspace.createFileSystemWatcher(
        '**/*.{csproj,vbproj,fsproj,DotSettings}'
    );
    const stale = () => void refresh();
    watcher.onDidChange(stale);
    watcher.onDidCreate(stale);
    watcher.onDidDelete(stale);
    panel.onDidDispose(() => watcher.dispose());

    return (path: string) => {
        target = path;
        void refresh();
    };
}

function basename(path: string): string {
    const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
    return cut < 0 ? path : path.slice(cut + 1);
}

function messageOf(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}
