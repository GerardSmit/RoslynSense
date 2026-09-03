import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { onClientReady } from './clientReady';

/**
 * "The set of loaded projects changed", fanned out to everything that draws it.
 *
 * One subscription to the client, because `onNotification` holds a single handler per method — a
 * second subscriber would silently replace the first, and the symptom would be a view that stopped
 * refreshing for no reason anybody could see.
 *
 * The event fires for the transition that matters most and is easiest to miss: a window opened
 * while the solution was still loading. Everything asked at that moment is answered "nothing yet",
 * and nothing asks again on its own.
 */

const emitter = new vscode.EventEmitter<void>();

/** Fires when the server's project set changes, and once when a client first connects. */
export const onProjectSetChanged = emitter.event;

export function registerProjectSet(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    onClientReady(context, getClient, (client) => {
        // A new client is itself a change: it may already have a solution that the last one did
        // not, and anything asked before it existed was answered by nobody.
        emitter.fire();

        context.subscriptions.push(
            client.onNotification('roslynSense/projectSetChanged', () => emitter.fire())
        );
    });
}
