import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { openSourcesConfig, wire } from './host';

/**
 * NuGet package management, Rider-style: Browse / Installed / Updates / Consolidate, a versions
 * dropdown, and a details pane with the README, license, dependency groups and any known
 * advisories.
 *
 * All network access happens in the daemon, never here: private feeds need NuGet.config
 * credentials and credential providers, which a webview cannot supply. The webview therefore runs
 * under a strict CSP with no remote content at all — even package icons arrive as data URIs
 * proxied by the server.
 */

const VIEW_TYPE = 'roslynSense.nuget';

export function registerNuGetPanel(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    let panel: vscode.WebviewPanel | undefined;

    const open = (scopeProjects?: string[], selectPackage?: string) => {
        const pending =
            scopeProjects && scopeProjects.length > 0
                ? { projectPaths: scopeProjects, selectPackage: selectPackage ?? null }
                : undefined;

        if (panel) {
            panel.reveal();
            // Already live, so it is listening — post directly.
            if (pending) {
                void panel.webview.postMessage({ type: 'scope', ...pending } satisfies NuGetMsg.ToView);
            }
            return;
        }

        // A fresh webview has not attached its message listener yet, so posting now would drop
        // the scope on the floor — and the boot reply would overwrite it in any case. It is
        // handed to wire() instead, which replays it once the panel says it is ready.
        panel = createPanel(context, getClient, () => (panel = undefined), pending);
    };

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.manageNuGet', () => open()),
        vscode.commands.registerCommand(
            'roslynSense.manageNuGetForProject',
            (node: { id?: string }, selectPackage?: string) => {
                const projectPath = projectPathOf(node?.id);
                open(projectPath ? [projectPath] : undefined, selectPackage);
            }
        ),
        // Deliberately opens the Updates tab rather than running anything: a one-click
        // solution-wide mutation from the command palette is not something to ship.
        vscode.commands.registerCommand('roslynSense.nuget.updateAll', () => {
            if (panel) {
                panel.reveal();
                void panel.webview.postMessage({ type: 'goToTab', tab: 'updates' } satisfies NuGetMsg.ToView);
            } else {
                panel = createPanel(context, getClient, () => (panel = undefined), undefined, 'updates');
            }
        }),
        vscode.commands.registerCommand('roslynSense.nuget.clearCredentials', () =>
            clearCredentials(context)
        ),
        vscode.commands.registerCommand('roslynSense.nuget.openSourcesConfig', async () => {
            const client = getClient();
            if (!client) {
                return;
            }
            await openSourcesConfig(
                await client.sendRequest<NuGetMsg.PackageSource[]>('roslynSense/nuget/sources', {})
            );
        }),
        vscode.window.registerWebviewPanelSerializer(VIEW_TYPE, {
            async deserializeWebviewPanel(restored) {
                panel = restored;
                wire(context, restored, getClient, () => (panel = undefined));
            },
        })
    );
}

/**
 * The project a Solution Explorer node belongs to.
 *
 * The command is contributed on the project, Dependencies and Packages nodes, and each spells its
 * id differently — matching only `project:` meant that right-clicking Dependencies or Packages
 * opened the panel with nothing selected.
 */
function projectPathOf(id: string | undefined): string | undefined {
    if (!id) {
        return undefined;
    }
    if (id.startsWith('project:')) {
        return id.slice('project:'.length);
    }
    // "<projectPath>!deps"
    if (id.endsWith('!deps')) {
        return id.slice(0, -'!deps'.length);
    }
    // "group:<kind>|<projectPath>[|…]" — the kind comes first, the path second.
    if (id.startsWith('group:')) {
        return id.slice('group:'.length).split('|')[1];
    }
    // "package:<projectPath>|<id>"
    if (id.startsWith('package:')) {
        return id.slice('package:'.length).split('|')[0];
    }
    return undefined;
}

function createPanel(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined,
    onDispose: () => void,
    pendingScope?: { projectPaths: string[]; selectPackage: string | null },
    pendingTab?: NuGetMsg.Tab
): vscode.WebviewPanel {
    const panel = vscode.window.createWebviewPanel(VIEW_TYPE, 'NuGet', vscode.ViewColumn.Active, {
        enableScripts: true,
        retainContextWhenHidden: true,
        // Unset, this defaults to the extension directory *plus every workspace folder*, which
        // would let the panel read the user's source.
        localResourceRoots: [
            vscode.Uri.joinPath(context.extensionUri, 'out', 'webview'),
            vscode.Uri.joinPath(context.extensionUri, 'media'),
        ],
    });
    wire(context, panel, getClient, onDispose, pendingScope, pendingTab);
    return panel;
}

/**
 * SecretStorage has no enumeration API, so the feeds we have stored a credential for are tracked
 * separately purely so they can be cleared again.
 */
const FEED_INDEX_KEY = 'roslynSense.nuget.credentialFeeds';

async function clearCredentials(context: vscode.ExtensionContext): Promise<void> {
    const feeds = context.globalState.get<string[]>(FEED_INDEX_KEY, []);
    if (feeds.length === 0) {
        void vscode.window.showInformationMessage('No NuGet feed credentials are stored.');
        return;
    }

    const confirm = await vscode.window.showWarningMessage(
        `Forget saved credentials for ${feeds.length} NuGet feed(s)?`,
        { modal: true },
        'Forget'
    );
    if (confirm !== 'Forget') {
        return;
    }

    for (const feed of feeds) {
        await context.secrets.delete(feed);
    }
    await context.globalState.update(FEED_INDEX_KEY, []);
    void vscode.window.showInformationMessage('Saved NuGet feed credentials were removed.');
}
