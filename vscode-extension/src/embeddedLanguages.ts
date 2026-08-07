import * as vscode from 'vscode';
import { project, scan, Region } from './embeddedProjection';

/**
 * JavaScript and CSS inside markup, answered by the language services VS Code already ships.
 *
 * A `.aspx` is HTML with server code cut into it, and the two embedded languages a page carries —
 * the `<script>` and the `<style>` — are exactly the two the built-in HTML extension already
 * understands. That extension does not run tsserver for them: it carries its own TypeScript
 * language service with `lib.es2020.full.d.ts` and jQuery bundled in, and its own CSS service, and
 * it registers on the language id alone with no scheme filter. So a virtual document that looks
 * like HTML gets the whole of it, `document.querySelector` and `border-radius` included.
 *
 * The document handed over is the page with its server constructs blanked to spaces — directives,
 * `<% %>` islands and `<script runat="server">` bodies. Blanking rather than deleting is what makes
 * this cheap: every offset in the projection is the same offset in the page, so a position needs no
 * mapping in either direction and a result needs no translation back.
 *
 * Requests are forwarded only from inside a client `<script>` or `<style>`. The HTML service would
 * happily complete tags and attributes across the whole file, but that half of the page is the
 * WebForms server's to answer — it knows what an `<asp:Button>` is and which of its attributes are
 * events, and two providers answering the same position is two of every suggestion.
 */

const SCHEME = 'roslynsense-embedded';

/** The language ids this runs for. Both halves of the WebForms family are markup. */
const MARKUP_LANGUAGE = 'webforms';

export function registerEmbeddedLanguages(context: vscode.ExtensionContext): void {
    const changed = new vscode.EventEmitter<vscode.Uri>();

    const contents: vscode.TextDocumentContentProvider = {
        onDidChange: changed.event,

        provideTextDocumentContent(uri) {
            // Said in the document rather than answered with an empty one. A projection tab
            // restored from a previous window names a page this session has not opened, and an
            // editor that is simply blank is the least debuggable thing this feature could show —
            // the command that opens it is meant to be the tool that explains a failure.
            const source = sourceUriOf(uri);
            const open = source === undefined
                ? undefined
                : vscode.workspace.textDocuments.find(
                    (document) => document.uri.toString() === source);

            return open
                ? projectionOf(open).text
                : '<!-- No projection: this is a stale tab from an earlier window, or the page it '
                    + 'belongs to is not open. Reopen the page and run the command again. -->';
        },
    };

    const selector: vscode.DocumentSelector = { language: MARKUP_LANGUAGE };

    context.subscriptions.push(
        changed,
        vscode.workspace.registerTextDocumentContentProvider(SCHEME, contents),

        // The projection is re-read on change rather than pushed: VS Code caches a virtual
        // document's text until the provider says it moved, and a stale one would complete against
        // the keystroke before last.
        vscode.workspace.onDidChangeTextDocument((e) => {
            if (e.document.languageId === MARKUP_LANGUAGE) {
                changed.fire(virtualUriOf(e.document.uri));
            }
        }),

        vscode.workspace.onDidCloseTextDocument((document) => {
            s_projections.delete(document.uri.toString());

            // The name itself is kept: reopening the page reuses it, and a projection the HTML
            // server already knows under that name stays the same document rather than becoming a
            // second one.
            const virtual = virtualUriFor(document.uri);
            if (virtual) {
                s_sources.delete(virtual.toString());
            }
        }),

        // Opening the projection alongside the page is what activates the HTML extension, which
        // declares itself on `onLanguage:html` and so is not running at all in a window where no
        // HTML file was ever opened. Left until the first completion, that activation races the
        // request that triggered it and the first Ctrl+Space of a session answers nothing.
        vscode.workspace.onDidOpenTextDocument((document) => {
            invalidate(document);
            void warm(document);
        }),

        // What the HTML services are actually being shown. When a completion list is empty the
        // question is always which of the two halves failed — the projection or the forwarding —
        // and opening the projection answers it: blank where server code was, and its own
        // IntelliSense working inside it, means the projection is fine.
        vscode.commands.registerCommand('roslynSense.showEmbeddedProjection', async () => {
            const source = vscode.window.activeTextEditor?.document;
            if (source?.languageId !== MARKUP_LANGUAGE) {
                void vscode.window.showInformationMessage(
                    'Open a markup file (.aspx, .ascx, .master) to see its projection.');
                return;
            }

            await htmlServiceReady();
            if (s_htmlMissing) {
                void vscode.window.showWarningMessage(
                    `The ${HTML_EXTENSION} extension is not available, so JavaScript and CSS ` +
                    'IntelliSense in markup cannot work. The projection below is still what it ' +
                    'would be shown.');
            }

            const projected = await vscode.workspace.openTextDocument(virtualUriOf(source.uri));
            await vscode.window.showTextDocument(projected, { preview: true });

            const regions = projectionOf(source).regions;
            void vscode.window.setStatusBarMessage(
                `Projection: ${regions.length} embedded region(s) — `
                + regions.map((r) => r.kind).join(', '),
                8000);
        }),

        vscode.languages.registerCompletionItemProvider(
            selector,
            {
                async provideCompletionItems(document, position, token, completionContext) {
                    if (!enabled() || !regionAt(document, position)) {
                        return undefined;
                    }

                    return await forward<vscode.CompletionList>(
                        'vscode.executeCompletionItemProvider',
                        document,
                        position,
                        completionContext.triggerCharacter
                    );
                },
            },
            // The union of what the CSS and JavaScript services ask to be woken for. A character
            // missing here is not a missing feature, only a list the user has to ask for with
            // Ctrl+Space.
            '.', ':', ';', ',', '(', '\'', '"', '`', '/', '@', '#', '-', '!', '$', '<', ' '
        ),

        vscode.languages.registerHoverProvider(selector, {
            async provideHover(document, position) {
                if (!enabled() || !regionAt(document, position)) {
                    return undefined;
                }

                const hovers = await forward<vscode.Hover[]>(
                    'vscode.executeHoverProvider', document, position);
                return hovers?.[0];
            },
        }),

        vscode.languages.registerDefinitionProvider(selector, {
            async provideDefinition(document, position) {
                if (!enabled() || !regionAt(document, position)) {
                    return undefined;
                }

                const found = await forward<Array<vscode.Location | vscode.LocationLink>>(
                    'vscode.executeDefinitionProvider', document, position);

                // A definition inside the projection is a position in the page — same offsets — so
                // it is reported against the page. One in a bundled lib is a URI of its own and is
                // left alone. Both shapes the command may return are handled: the services in play
                // answer with Location today, but LocationLink is equally allowed and reading
                // `.uri` off one would throw here, outside the forward's own guard.
                return found?.map((entry) => {
                    const uri = 'targetUri' in entry ? entry.targetUri : entry.uri;

                    // A link's targetRange is the whole declaration; its selection range is the
                    // name, which is where the caret belongs.
                    const range = 'targetRange' in entry
                        ? entry.targetSelectionRange ?? entry.targetRange
                        : entry.range;

                    return isVirtual(uri)
                        ? new vscode.Location(document.uri, range)
                        : new vscode.Location(uri, range);
                });
            },
        }),

        vscode.languages.registerDocumentHighlightProvider(selector, {
            async provideDocumentHighlights(document, position) {
                if (!enabled() || !regionAt(document, position)) {
                    return undefined;
                }

                return await forward<vscode.DocumentHighlight[]>(
                    'vscode.executeDocumentHighlights', document, position);
            },
        }),

        vscode.languages.registerSignatureHelpProvider(
            selector,
            {
                async provideSignatureHelp(document, position) {
                    if (!enabled() || regionAt(document, position)?.kind !== 'javascript') {
                        return undefined;
                    }

                    return await forward<vscode.SignatureHelp>(
                        'vscode.executeSignatureHelpProvider', document, position);
                },
            },
            '(', ','
        )
    );

    // The window may already have pages open when this runs — same situation as a reload, so they
    // are invalidated too rather than only warmed.
    for (const open of vscode.workspace.textDocuments) {
        invalidate(open);
        void warm(open);
    }

    /**
     * Tells VS Code the page's projection moved, and records the name while doing it.
     *
     * Both halves matter, and both have to happen with no await in between. VS Code caches a
     * virtual document's text until the provider says otherwise and hands `openTextDocument` the
     * cached copy without asking again — so a projection tab restored from a previous window, which
     * resolves before anything has recorded a name for it, caches an empty document that every
     * later request then reads. That is the same silent "no suggestions" this feature has already
     * shipped more than once. Firing for a URI nothing has opened is a no-op, so it costs nothing
     * in the ordinary case.
     */
    function invalidate(document: vscode.TextDocument): void {
        if (document.languageId === MARKUP_LANGUAGE) {
            changed.fire(virtualUriOf(document.uri));
        }
    }
}

/**
 * Opens a page's projection so the HTML extension is running before anything asks it a question.
 */
async function warm(document: vscode.TextDocument): Promise<void> {
    if (document.languageId !== MARKUP_LANGUAGE || !enabled()) {
        return;
    }

    try {
        await htmlServiceReady();
        await vscode.workspace.openTextDocument(virtualUriOf(document.uri));
    } catch {
        // Nothing depends on this having worked; the first request builds it again.
    }
}

function enabled(): boolean {
    return vscode.workspace
        .getConfiguration('roslynSense')
        .get<boolean>('embeddedLanguages', true);
}

// ---- Forwarding ----------------------------------------------------------------------------

/**
 * The extension that owns the HTML, CSS and JavaScript services, started on demand.
 */
const HTML_EXTENSION = 'vscode.html-language-features';

let s_htmlReady: Promise<void> | undefined;

/** Whether the HTML extension turned out not to be there at all. */
let s_htmlMissing = false;

/**
 * Starts the HTML extension, once, and resolves when it is running.
 *
 * It has to be asked. Its manifest declares `onLanguage:html` and `onLanguage:handlebars` and
 * nothing else, so in a window where no HTML file was ever opened it is not running — and every
 * request forwarded to it answers nothing, which is indistinguishable from a language with no
 * suggestions to offer. That gap is a known VS Code bug (microsoft/vscode#160585, open), and
 * activating it from code is the maintainer's own advice and what the extensions that depend on
 * this already ship.
 */
function htmlServiceReady(): Promise<void> {
    s_htmlReady ??= (async () => {
        try {
            const html = vscode.extensions.getExtension(HTML_EXTENSION);
            if (!html) {
                // Said once rather than swallowed. Without it every request answers nothing for
                // the rest of the session, and "no suggestions" is what a language with nothing
                // to suggest looks like too — the failure would be invisible.
                s_htmlMissing = true;
                console.warn(
                    `[RoslynSense] ${HTML_EXTENSION} is not installed or is disabled; ` +
                    'JavaScript and CSS IntelliSense in markup is unavailable.');
                return;
            }

            if (!html.isActive) {
                await html.activate();
            }
        } catch {
            // Left to be retried rather than remembered as broken: a failure here is usually a
            // window still starting up, and a memoized rejection would disable the feature for
            // the rest of the session.
            s_htmlReady = undefined;
        }
    })();

    return s_htmlReady;
}

async function forward<T>(
    command: string,
    document: vscode.TextDocument,
    position: vscode.Position,
    ...rest: unknown[]
): Promise<T | undefined> {
    try {
        await htmlServiceReady();

        return await vscode.commands.executeCommand<T>(
            command, virtualUriOf(document.uri), position, ...rest);
    } catch {
        // A language service that is still starting, or one that declines a scheme it has never
        // seen, is a feature that is not there yet — not a request that failed. Answering nothing
        // leaves the server's own reply for this position untouched.
        return undefined;
    }
}

/**
 * Which page each virtual document stands for.
 *
 * Recorded rather than recovered from the URI. Carrying one URI inside another does not survive the
 * trip: `Uri.parse` percent-decodes the path, so `file:///d%3A/…` comes back as `file:///d:/…` and
 * no longer equals the `toString()` of the document it names — which is the comparison the content
 * provider makes, and a miss there is an empty projection and a completion list with nothing in it.
 * A page whose name contains a `#` does not survive at all: the decode turns it into a fragment.
 */
const s_sources = new Map<string, string>();

/** The short name each page's projection is filed under, so the same page reuses the same one. */
const s_names = new Map<string, string>();

/**
 * The virtual document a page projects to.
 *
 * The path is a short opaque name, not the page's own URI carried inside this one. That is not
 * tidiness: the HTML extension's JavaScript mode hands the document's URI straight to TypeScript as
 * a *file name* (`getScriptFileNames: () => [currentTextDocument.uri]`), and a name with a nested
 * `file:` scheme and `%25` escapes in it is not one TypeScript can resolve. The snapshot lookup
 * then misses, the script reads as empty, and every position answers with global scope — no
 * members after a dot, no definition, no hover. CSS never noticed because it does not go near
 * TypeScript. The mapping back to the page lives in <see cref="s_sources"/> instead.
 *
 * The `.html` suffix stays, because it is what decides the language, and the language is what the
 * HTML extension selects on.
 */
function virtualUriOf(source: vscode.Uri): vscode.Uri {
    const key = source.toString();

    let name = s_names.get(key);
    if (name === undefined) {
        name = `page-${s_names.size}`;
        s_names.set(key, name);
    }

    const virtual = vscode.Uri.parse(`${SCHEME}://markup/${name}.html`);
    s_sources.set(virtual.toString(), key);
    return virtual;
}

/** The same URI without recording it, for the close handler — which is forgetting, not asking. */
function virtualUriFor(source: vscode.Uri): vscode.Uri | undefined {
    const name = s_names.get(source.toString());
    return name === undefined
        ? undefined
        : vscode.Uri.parse(`${SCHEME}://markup/${name}.html`);
}

function sourceUriOf(virtual: vscode.Uri): string | undefined {
    // Only what was recorded. The name carries no information of its own, so a projection VS Code
    // restores from a previous window — before anything asked for one — resolves to nothing until
    // the page is opened again, at which point `warm` files it afresh.
    return s_sources.get(virtual.toString());
}

function isVirtual(uri: vscode.Uri): boolean {
    return uri.scheme === SCHEME;
}

// ---- Projection ----------------------------------------------------------------------------

interface Projection {
    /** The page with its server constructs blanked, offset for offset. */
    text: string;
    /** The embedded regions, found in that text rather than in the page. */
    regions: Region[];
}

/**
 * The projection of one open page, memoized on its version.
 *
 * Kept because completion, hover and signature help each ask for it, and the content provider asks
 * again for the same keystroke — and a `.ascx` in a real site runs to hundreds of kilobytes, which
 * is not a thing to rebuild four times between two characters.
 */
const s_projections = new Map<string, { version: number; projection: Projection }>();

function projectionOf(document: vscode.TextDocument): Projection {
    const key = document.uri.toString();
    const cached = s_projections.get(key);
    if (cached?.version === document.version) {
        return cached.projection;
    }

    const text = project(document.getText());
    const projection: Projection = { text, regions: scan(text) };

    s_projections.set(key, { version: document.version, projection });
    return projection;
}

/** The embedded region the position sits in, or undefined when it is in markup. */
function regionAt(document: vscode.TextDocument, position: vscode.Position): Region | undefined {
    const offset = document.offsetAt(position);
    return projectionOf(document).regions.find((r) => offset >= r.start && offset <= r.end);
}
