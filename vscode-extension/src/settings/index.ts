import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { wire } from './host';

/**
 * A settings page for `roslynsense.json`, with a tab per scope.
 *
 * The file is layered — global, the team's, yours for this checkout, yours out of tree — and the
 * layering is exactly what makes it hard to edit by hand: the question is never "what does this
 * file say" but "what is in effect, and which of the four said so". A form that shows the
 * effective value and names its origin answers that; a JSON editor cannot.
 *
 * A webview panel rather than a tree view because the controls are a form — toggles, dropdowns,
 * list editors — and because the schema it is generated from already describes them.
 */

const VIEW_TYPE = 'roslynSense.settings';

export function registerSettingsPanel(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined = () => undefined
): void {
    let panel: vscode.WebviewPanel | undefined;

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.openSettings', () => {
            if (panel) {
                panel.reveal();
                return;
            }
            panel = createPanel(context, () => (panel = undefined), getClient);
        }),
        vscode.window.registerWebviewPanelSerializer(VIEW_TYPE, {
            async deserializeWebviewPanel(restored) {
                panel = restored;
                wire(context, restored, () => (panel = undefined), getClient);
            },
        })
    );
}

function createPanel(
    context: vscode.ExtensionContext,
    onDispose: () => void,
    getClient: () => LanguageClient | undefined
): vscode.WebviewPanel {
    const panel = vscode.window.createWebviewPanel(
        VIEW_TYPE,
        'RoslynSense Settings',
        vscode.ViewColumn.Active,
        {
            enableScripts: true,
            // A form in the middle of being filled in is not something to throw away because
            // somebody looked at another tab. The page holds unsaved text, an open dropdown and a
            // scroll position, and rebuilding it loses all three.
            retainContextWhenHidden: true,
            localResourceRoots: [
                vscode.Uri.joinPath(context.extensionUri, 'out', 'webview'),
                vscode.Uri.joinPath(context.extensionUri, 'media'),
            ],
        }
    );
    wire(context, panel, onDispose, getClient);
    return panel;
}
