import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { deferred, loadWithMocks } from './mockModule';

function setup() {
    let receive!: (message: unknown) => Promise<void>;
    let ready!: () => void;
    let close!: () => void;
    const requests: {
        query: string;
        token: { isCancellationRequested: boolean };
        response: ReturnType<typeof deferred<unknown>>;
    }[] = [];
    const posted: { type: string; id?: number }[] = [];
    const tokenized: unknown[] = [];
    const documents = new Map<string, ReturnType<typeof deferred<unknown>>>();
    const panel = {
        webview: {
            html: '',
            onDidReceiveMessage: (callback: typeof receive) => { receive = callback; },
            postMessage: (message: typeof posted[number]) => { posted.push(message); },
        },
        onDidDispose: (callback: typeof close) => { close = callback; },
    };
    const module = loadWithMocks<typeof import('../search/host')>(require.resolve('../search/host'), {
        vscode: {
            CancellationTokenSource: class {
                readonly token = { isCancellationRequested: false };
                cancel() { this.token.isCancellationRequested = true; }
                dispose() {}
            },
            workspace: {
                openTextDocument: (uri: string) => documents.get(uri)!.promise,
                asRelativePath: (uri: string) => uri,
            },
            Uri: { parse: (uri: string) => uri },
        },
        './html': { html: () => '' },
        '../solutionReady': { onSolutionReady: (callback: typeof ready) => {
            ready = callback;
            return { dispose() {} };
        } },
        './textmate': { tokenizePreview: async (document: unknown) => { tokenized.push(document); return null; } },
    });
    const connection = { sendRequest: (_method: string, params: { query: string },
        token: { isCancellationRequested: boolean }) => {
        const response = deferred<unknown>();
        requests.push({ query: params.query, token, response });
        return response.promise;
    } };
    module.wire({} as vscode.ExtensionContext, panel as unknown as vscode.WebviewPanel,
        () => connection as unknown as LanguageClient, () => {});
    return {
        receive, ready, close, requests, posted, documents, tokenized,
        search: (id: number, query: string) => receive({ type: 'search', id, tab: 'symbols', query }),
    };
}

describe('Search Everywhere request lifecycle', () => {
    it('does not let solution readiness rerun an old query over a newer pending query', async () => {
        const state = setup();
        const first = state.search(1, 'old');
        state.requests[0].response.resolve({ items: [], truncated: false, loading: true });
        await first;

        const second = state.search(2, 'new');
        state.ready();
        assert.equal(state.requests.length, 2);
        assert.equal(state.requests[1].token.isCancellationRequested, false);
        state.requests[1].response.resolve({ items: [], truncated: false });
        await second;
        assert.equal(state.posted.at(-1)?.id, 2);
    });

    it('still reruns the current provisional result once the solution is ready', async () => {
        const state = setup();
        const first = state.search(1, 'current');
        state.requests[0].response.resolve({ items: [], truncated: false, loading: true });
        await first;
        state.ready();
        assert.equal(state.requests.length, 2);
        assert.equal(state.requests[1].query, 'current');
        state.requests[1].response.resolve({ items: [], truncated: false });
    });

    it('cancels a search when its panel closes and discards the late answer', async () => {
        const state = setup();
        const pending = state.search(1, 'current');
        state.close();
        assert.equal(state.requests[0].token.isCancellationRequested, true);
        state.requests[0].response.resolve({ items: [], truncated: false });
        await pending;
        assert.equal(state.posted.length, 0);
    });

    it('skips tokenization of a preview superseded while its document opens', async () => {
        const state = setup();
        const oldDocument = deferred<unknown>();
        const newDocument = deferred<unknown>();
        state.documents.set('old.cs', oldDocument);
        state.documents.set('new.cs', newDocument);
        const oldPreview = state.receive({ type: 'preview', id: 1, uri: 'old.cs', line: 0 });
        await Promise.resolve(); // Let the first preview start opening its document.
        const newPreview = state.receive({ type: 'preview', id: 2, uri: 'new.cs', line: 0 });
        const document = { lineCount: 1, languageId: 'csharp', lineAt: () => ({ text: 'class Example {}' }) };
        newDocument.resolve(document);
        await newPreview;
        oldDocument.resolve(document);
        await oldPreview;
        assert.equal(state.tokenized.length, 1);
        assert.equal(state.posted.length, 1);
        assert.equal(state.posted[0].id, 2);
    });
});
