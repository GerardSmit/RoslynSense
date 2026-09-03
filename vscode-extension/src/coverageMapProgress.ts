import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { onClientReady } from './clientReady';

/**
 * The server's coverage-map build progress, fanned out to everything that renders it: the
 * notification's bar in the window that asked for the build, and the Coverage view's header in
 * every window. One subscription to the client, because `onNotification` holds a single handler
 * per method — a second subscriber would silently replace the first.
 */

export interface CoverageMapProgressEvent {
    message: string;
    /** 0–100, spanning the whole build across every test project. */
    percentage: number;
    /** The last event of a build, sent however the build ended. */
    done: boolean;
}

const emitter = new vscode.EventEmitter<CoverageMapProgressEvent>();

export const onCoverageMapProgress = emitter.event;

export function registerCoverageMapProgress(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    onClientReady(context, getClient, (client) => {
        context.subscriptions.push(
            client.onNotification('roslynSense/coverageMapProgress', (event: CoverageMapProgressEvent) =>
                emitter.fire(event)
            )
        );
    });
}
