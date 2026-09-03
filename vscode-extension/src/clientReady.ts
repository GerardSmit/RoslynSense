import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';

/**
 * Runs `callback` once the language client exists, and again whenever it is replaced.
 *
 * A view registered at activation is asked for its contents before the client has connected —
 * VS Code builds the tree immediately and the client is started afterwards — so the first fetch
 * has nobody to ask and returns empty. Nothing re-asks on its own: a `TreeDataProvider` is only
 * consulted again when it fires `onDidChangeTreeData`, which is why an empty first answer used to
 * be the last answer, and why the view stayed blank until the user typed in it.
 *
 * Polling rather than subscribing because the object to subscribe to does not exist yet, and a
 * restart or a solution switch hands out a new one.
 */
export function onClientReady(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined,
    callback: (client: LanguageClient) => void
): void {
    let seen: LanguageClient | undefined;

    const check = () => {
        const client = getClient();
        if (!client || client === seen) {
            return;
        }
        seen = client;
        callback(client);
    };

    check();
    const timer = setInterval(check, 2000);
    context.subscriptions.push({ dispose: () => clearInterval(timer) });
}
