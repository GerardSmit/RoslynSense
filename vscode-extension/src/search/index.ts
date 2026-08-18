import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { wire } from './host';

/**
 * Search Everywhere, Rider-style: one box over types, members, files, IDE actions and plain
 * text, with a tab per kind, a preview pane, and an "include non-solution items" switch that
 * reaches into referenced assemblies (results open as decompiled source).
 *
 * A webview panel rather than a QuickPick because the popup's defining features — the tab row,
 * the preview under the list, the checkbox — have no home in the QuickPick API. The ranking is
 * the server's; the webview renders the list exactly as sent.
 */

const VIEW_TYPE = 'roslynSense.searchEverywhere';

export function registerSearchEverywhere(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    let panel: vscode.WebviewPanel | undefined;

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.searchEverywhere', () => {
            if (panel) {
                panel.reveal();
                return;
            }
            panel = createPanel(context, getClient, () => (panel = undefined));
        }),
        vscode.window.registerWebviewPanelSerializer(VIEW_TYPE, {
            async deserializeWebviewPanel(restored) {
                panel = restored;
                wire(context, restored, getClient, () => (panel = undefined));
            },
        })
    );
}

function createPanel(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined,
    onDispose: () => void
): vscode.WebviewPanel {
    const panel = vscode.window.createWebviewPanel(
        VIEW_TYPE,
        'Search Everywhere',
        vscode.ViewColumn.Active,
        {
            enableScripts: true,
            // Deliberately NOT retainContextWhenHidden: the panel is a popup that is opened,
            // used and closed — holding a hidden webview process for it is pure cost.
            localResourceRoots: [
                vscode.Uri.joinPath(context.extensionUri, 'out', 'webview'),
                vscode.Uri.joinPath(context.extensionUri, 'media'),
            ],
        }
    );
    wire(context, panel, getClient, onDispose);
    return panel;
}
