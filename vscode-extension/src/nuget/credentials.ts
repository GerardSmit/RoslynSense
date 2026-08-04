import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * Answers the daemon when a NuGet feed rejects a request.
 *
 * This is the only route by which a credential the user keeps in their OS keychain — rather than
 * in plain text in NuGet.config — can reach package operations. The daemon asks; nothing is stored
 * on its side.
 */

interface CredentialRequest {
    uri: string;
    sourceName: string | null;
    message: string | null;
    isRetry: boolean;
}

interface CredentialReply {
    username: string;
    password: string;
}

const FEED_INDEX_KEY = 'roslynSense.nuget.credentialFeeds';

/**
 * A trace channel that will not log a feed password.
 *
 * `roslynSense.trace.server` at `verbose` makes vscode-languageclient write every server→client
 * request *and its response* to the trace channel, and a trace channel is exactly what someone
 * pastes into a bug report.
 *
 * The payload is redacted by a latch rather than by matching the method name, because the client
 * writes the header and the body as two separate calls: `Sending response 'roslynSense/nuget/
 * credentialRequest - (1)'...` and then, on the very next line, `Result: {"username":…,
 * "password":…}` — which contains no method name at all. Matching only the header would redact the
 * line that is harmless and print the one that is not.
 */
export function createRedactingTraceChannel(): vscode.OutputChannel {
    const inner = vscode.window.createOutputChannel('RoslynSense Trace');

    const NOTICE = '[RoslynSense] NuGet credential traffic redacted.';
    let expectPayload = false;

    const redact = (value: string): string => {
        if (value.includes('credentialRequest')) {
            // The data line follows its header, so arm the latch for it.
            expectPayload = true;
            return NOTICE;
        }
        if (expectPayload) {
            expectPayload = false;
            return NOTICE;
        }
        // Belt and braces for any framing this misses — a password field never belongs here.
        return /"password"\s*:/.test(value) ? NOTICE : value;
    };

    return {
        get name() {
            return inner.name;
        },
        append: (value: string) => inner.append(redact(value)),
        appendLine: (value: string) => inner.appendLine(redact(value)),
        replace: (value: string) => inner.replace(redact(value)),
        clear: () => inner.clear(),
        show: (...args: unknown[]) => (inner.show as (...a: unknown[]) => void)(...args),
        hide: () => inner.hide(),
        dispose: () => inner.dispose(),
    } as vscode.OutputChannel;
}

/** Prompts are serialized: two feeds failing at once would otherwise race two input boxes, and
 *  VS Code cancels the first one when the second opens. */
let queue: Promise<unknown> = Promise.resolve();

/** Feeds already asked about in this session. A second ask means the stored credential was
 *  rejected, so it is discarded rather than replayed forever. */
const attempted = new Set<string>();

export function wireNuGetCredentials(
    client: LanguageClient,
    context: vscode.ExtensionContext
): void {
    attempted.clear();

    client.onRequest(
        'roslynSense/nuget/credentialRequest',
        (request: CredentialRequest): Promise<CredentialReply | null> => {
            const next = queue.then(() => resolve(request, context));
            queue = next.catch(() => undefined);
            return next;
        }
    );
}

async function resolve(
    request: CredentialRequest,
    context: vscode.ExtensionContext
): Promise<CredentialReply | null> {
    const key = secretKey(request.uri);

    // isRetry is the daemon saying the credential it was given was rejected. Inferring it from
    // "have we been asked before" instead would throw away a working credential whenever the same
    // host is legitimately asked twice — two Azure DevOps feeds on one host being the common case.
    const stale = request.isRetry;
    attempted.add(key);

    if (stale) {
        await forget(context, key);
    } else {
        const stored = await context.secrets.get(key);
        if (stored) {
            try {
                return JSON.parse(stored) as CredentialReply;
            } catch {
                await forget(context, key);
            }
        }
    }

    const feed = request.sourceName ?? request.uri;

    const username = await vscode.window.showInputBox({
        title: `Sign in to ${feed}`,
        // Showing the origin matters: the user is about to type a credential and is entitled to
        // know exactly which host will receive it.
        prompt: request.uri,
        placeHolder: 'User name (for a token-based feed, any value works)',
        ignoreFocusOut: true,
    });
    if (username === undefined) {
        return null;
    }

    const password = await vscode.window.showInputBox({
        title: `Sign in to ${feed}`,
        prompt: `Password or personal access token for ${request.uri}`,
        password: true,
        ignoreFocusOut: true,
    });
    if (password === undefined) {
        return null;
    }

    const reply: CredentialReply = { username, password };

    if (vscode.workspace.getConfiguration('roslynSense').get<boolean>('nuget.saveCredentials', true)) {
        await context.secrets.store(key, JSON.stringify(reply));
        await remember(context, key);
    }

    return reply;
}

/** Called when the panel's "Sign in" affordance is used: drops the stored credential so the next
 *  feed request prompts again. */
export async function forgetCredential(
    context: vscode.ExtensionContext,
    feedUrl: string
): Promise<void> {
    const key = secretKey(feedUrl);
    attempted.delete(key);
    await forget(context, key);
}

/**
 * Keyed on the origin, not the feed's name: two repositories can name the same feed differently,
 * and one name can point at different hosts in different repositories.
 */
function secretKey(uri: string): string {
    let origin = uri;
    try {
        const parsed = new URL(uri);
        origin = `${parsed.protocol}//${parsed.host}`;
    } catch {
        // Not a URL — use it verbatim rather than dropping the credential on the floor.
    }
    return `roslynSense.nuget.credential:${origin.toLowerCase()}`;
}

async function remember(context: vscode.ExtensionContext, key: string): Promise<void> {
    const feeds = context.globalState.get<string[]>(FEED_INDEX_KEY, []);
    if (!feeds.includes(key)) {
        await context.globalState.update(FEED_INDEX_KEY, [...feeds, key]);
    }
}

async function forget(context: vscode.ExtensionContext, key: string): Promise<void> {
    await context.secrets.delete(key);
    const feeds = context.globalState.get<string[]>(FEED_INDEX_KEY, []);
    await context.globalState.update(
        FEED_INDEX_KEY,
        feeds.filter((f) => f !== key)
    );
}
