import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { lensesToPreResolve } from './codeLensPrewarm';

const PRE_RESOLVE_TIMEOUT_MS = 400;

/** Resolve visible lenses on the connection that created their opaque resolve data. */
export async function preResolveVisibleLenses(
    connection: LanguageClient,
    document: vscode.TextDocument,
    lenses: vscode.CodeLens[],
    token: vscode.CancellationToken,
): Promise<vscode.CodeLens[]> {
    if (token.isCancellationRequested) {
        return lenses;
    }

    const visible = vscode.window.visibleTextEditors
        .filter((editor) => editor.document === document)
        .flatMap((editor) => editor.visibleRanges);
    const chosen = lensesToPreResolve(lenses, visible);
    if (chosen.length === 0) {
        return lenses;
    }

    const requests = new vscode.CancellationTokenSource();
    const merged = lenses.slice();
    let accepting = true;
    let expire: ReturnType<typeof setTimeout> | undefined;
    let cancelled: vscode.Disposable | undefined;

    try {
        const deadline = new Promise<void>((resolve) => {
            expire = setTimeout(resolve, PRE_RESOLVE_TIMEOUT_MS);
            cancelled = token.onCancellationRequested(() => {
                requests.cancel();
                resolve();
            });
        });

        const resolving = Promise.allSettled(chosen.map(async (index) => {
            const sent = connection.code2ProtocolConverter.asCodeLens(lenses[index]);
            const answer = await connection.sendRequest<typeof sent>(
                'codeLens/resolve', sent, requests.token);
            const resolved = connection.protocol2CodeConverter.asCodeLens(answer);
            // Keep partial successes when one expensive reference search exhausts the budget.
            // A late response must never mutate a list already handed back to the editor.
            if (accepting && !token.isCancellationRequested && resolved?.command) {
                merged[index] = resolved;
            }
        }));

        await Promise.race([resolving, deadline]);
        return merged;
    } finally {
        accepting = false;
        clearTimeout(expire);
        cancelled?.dispose();
        // Returning unresolved lenses causes the editor to resolve them itself. Stop the
        // speculative copies before that happens instead of doubling the server's searches.
        requests.cancel();
        requests.dispose();
    }
}
