import * as vscode from 'vscode';
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

export function registerSettingsPanel(context: vscode.ExtensionContext): void {
    let panel: vscode.WebviewPanel | undefined;

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.openSettings', () => {
            if (panel) {
                panel.reveal();
                return;
            }
            panel = createPanel(context, () => (panel = undefined));
        }),
        vscode.window.registerWebviewPanelSerializer(VIEW_TYPE, {
            async deserializeWebviewPanel(restored) {
                panel = restored;
                wire(context, restored, () => (panel = undefined));
            },
        })
    );
}

function createPanel(
    context: vscode.ExtensionContext,
    onDispose: () => void
): vscode.WebviewPanel {
    const panel = vscode.window.createWebviewPanel(
        VIEW_TYPE,
        'RoslynSense Settings',
        vscode.ViewColumn.Active,
        {
            enableScripts: true,
            localResourceRoots: [
                vscode.Uri.joinPath(context.extensionUri, 'out', 'webview'),
                vscode.Uri.joinPath(context.extensionUri, 'media'),
            ],
        }
    );
    wire(context, panel, onDispose);
    return panel;
}
