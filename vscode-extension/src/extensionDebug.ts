import * as vscode from 'vscode';

let channel: vscode.OutputChannel | undefined;

/**
 * The one output channel for diagnostics about the extension and server themselves — the
 * solution/discovery trees explaining why they answered empty, the server's self-diagnostics
 * arriving over `roslynSense/debugLog`. One channel rather than one per view: each view's
 * channel sat empty in the Output dropdown for the life of a healthy session, which made every
 * one of them look like a feature that was not working.
 *
 * Not for anything user-facing: the main `RoslynSense` channel is the user's view of their
 * solution, `RoslynSense Server` is the server's stderr, and this is for whoever is debugging
 * RoslynSense itself.
 */
export function extensionDebug(): vscode.OutputChannel {
    channel ??= vscode.window.createOutputChannel('RoslynSense Extension Debug');
    return channel;
}
