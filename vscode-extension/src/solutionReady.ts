import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { onClientReady } from './clientReady';

/**
 * "The bound solution has finished loading", fanned out to whatever answered a question early.
 *
 * One subscription to the client, for the reason `projectSet.ts` gives: `onNotification` holds a
 * single handler per method, so a second subscriber would silently replace the first.
 *
 * Separate from `onProjectSetChanged`, which fires whenever the loaded set moves — every build,
 * every restore, for the life of the session. This fires once per load, and means something
 * narrower: anything answered from a stand-in while MSBuild was busy can now be asked for real.
 * Search Everywhere is the view that needs it.
 */

const emitter = new vscode.EventEmitter<void>();

export const onSolutionReady = emitter.event;

export function registerSolutionReady(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    onClientReady(context, getClient, (client) => {
        context.subscriptions.push(
            client.onNotification('roslynSense/solutionReady', () => emitter.fire())
        );
    });
}
