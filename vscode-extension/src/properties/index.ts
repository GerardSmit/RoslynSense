import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { wire } from './host';

/**
 * Properties for one file or folder in the Solution Explorer.
 *
 * A file's build action, where it copies to and which tool generates it are things Visual Studio
 * and Rider both put behind a Properties window, and that VS Code has never had anywhere: the
 * only way to change them is to open the `.csproj` and know what to type. A folder's one property
 * — whether its name is part of the namespace — is worse, because it lives in a `.DotSettings`
 * file whose format nobody writes by hand.
 *
 * A webview panel rather than a tree decoration or a hover, because the point is to change these,
 * and because a form is what they are. It does not appear in the tree rows themselves: what a
 * file's build action is matters when you go looking, not on every row of every project.
 */

const VIEW_TYPE = 'roslynSense.properties';

export function registerPropertiesPanel(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined = () => undefined
): void {
    let panel: vscode.WebviewPanel | undefined;
    let retarget: ((path: string) => void) | undefined;

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'roslynSense.solutionExplorer.properties',
            (node?: { resourceUri?: string } | vscode.Uri) => {
                const path = pathOf(node);

                if (!path) {
                    void vscode.window.showInformationMessage(
                        'Select a file or folder to see its properties.'
                    );
                    return;
                }

                // One panel, pointed at whatever was asked about. Properties is a right-click
                // away from every row, and a tab per row is how the tab bar fills up.
                if (panel && retarget) {
                    panel.reveal(undefined, true);
                    retarget(path);
                    return;
                }

                panel = create(context, path, () => (panel = undefined), getClient, (fn) => {
                    retarget = fn;
                });
            }
        )
    );
}

function create(
    context: vscode.ExtensionContext,
    path: string,
    onDispose: () => void,
    getClient: () => LanguageClient | undefined,
    keep: (retarget: (path: string) => void) => void
): vscode.WebviewPanel {
    const panel = vscode.window.createWebviewPanel(
        VIEW_TYPE,
        'Properties',
        { viewColumn: vscode.ViewColumn.Active, preserveFocus: false },
        {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [
                vscode.Uri.joinPath(context.extensionUri, 'out', 'webview'),
                vscode.Uri.joinPath(context.extensionUri, 'media'),
            ],
        }
    );

    keep(wire(context, panel, path, onDispose, getClient));
    return panel;
}

/**
 * The path a Properties command was invoked on.
 *
 * Tree rows arrive as solution nodes carrying a `resourceUri`; the command palette and the file
 * explorer send a `Uri`. Both are answered, because the panel does not care which tree asked.
 */
function pathOf(node: { resourceUri?: string } | vscode.Uri | undefined): string | undefined {
    if (node instanceof vscode.Uri) {
        return node.fsPath;
    }

    if (node?.resourceUri) {
        return vscode.Uri.parse(node.resourceUri).fsPath;
    }

    return vscode.window.activeTextEditor?.document.uri.fsPath;
}
