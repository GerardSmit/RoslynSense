import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { deferred, loadWithMocks } from './mockModule';

class CancellationSource {
    private readonly listeners = new Set<() => void>();
    readonly token = {
        isCancellationRequested: false,
        onCancellationRequested: (listener: () => void) => {
            this.listeners.add(listener);
            return { dispose: () => this.listeners.delete(listener) };
        },
    };
    cancel() {
        if (!this.token.isCancellationRequested) {
            this.token.isCancellationRequested = true;
            for (const listener of this.listeners) { listener(); }
        }
    }
    dispose() { this.listeners.clear(); }
    get listenerCount() { return this.listeners.size; }
}

function setup() {
    const document = {} as vscode.TextDocument;
    const range = { start: { line: 0 }, end: { line: 5 } };
    const window = { visibleTextEditors: [{ document, visibleRanges: [range] }] };
    const module = loadWithMocks<typeof import('../visibleCodeLenses')>(
        require.resolve('../visibleCodeLenses'), {
            vscode: { window, CancellationTokenSource: CancellationSource },
        });
    const first = deferred<vscode.CodeLens>();
    const second = deferred<vscode.CodeLens>();
    const tokens: vscode.CancellationToken[] = [];
    const lenses = [{ range, data: 'one' }, { range, data: 'two' }] as unknown as vscode.CodeLens[];
    const resolved = { ...lenses[0], command: { title: '1 reference', command: 'references' } };
    const connection = {
        code2ProtocolConverter: { asCodeLens: (lens: unknown) => lens },
        protocol2CodeConverter: { asCodeLens: (lens: unknown) => lens },
        sendRequest: (_method: string, lens: unknown, token: vscode.CancellationToken) => {
            tokens.push(token);
            return lens === lenses[0] ? first.promise : second.promise;
        },
    } as unknown as LanguageClient;
    const cancellation = new CancellationSource();
    return {
        window, first, second, tokens, lenses, resolved, cancellation,
        run: () => module.preResolveVisibleLenses(connection, document, lenses,
            cancellation.token as vscode.CancellationToken),
    };
}

describe('visible CodeLens prewarming', () => {
    it('retains successful resolves when another lens fails', async () => {
        const state = setup();
        const pending = state.run();
        state.first.resolve(state.resolved);
        state.second.reject(new Error('server unavailable'));
        const result = await pending;
        assert.equal(result[0], state.resolved);
        assert.equal(result[1], state.lenses[1]);
        assert.equal(state.cancellation.listenerCount, 0);
    });

    it('keeps partial results, cancels work at the deadline, and ignores late replies', async () => {
        const state = setup();
        const pending = state.run();
        state.first.resolve(state.resolved);
        const result = await pending; // The second request deliberately never finishes in budget.
        assert.equal(result[0], state.resolved);
        assert.equal(result[1], state.lenses[1]);
        assert.ok(state.tokens.every((token) => token.isCancellationRequested));
        state.second.resolve(state.resolved);
        await new Promise<void>((resolve) => setImmediate(resolve));
        assert.equal(result[1], state.lenses[1]);
    });

    it('returns immediately and cancels pending requests when the editor cancels', async () => {
        const state = setup();
        const pending = state.run();
        state.cancellation.cancel();
        const result = await pending;
        assert.equal(result[0], state.lenses[0]);
        assert.ok(state.tokens.every((token) => token.isCancellationRequested));
        assert.equal(state.cancellation.listenerCount, 0);
    });

    it('does not issue requests for an already cancelled lens list', async () => {
        const state = setup();
        state.cancellation.cancel();
        assert.equal(await state.run(), state.lenses);
        assert.equal(state.tokens.length, 0);
    });

    it('does not issue speculative requests for background documents', async () => {
        const state = setup();
        state.window.visibleTextEditors = [];
        assert.equal(await state.run(), state.lenses);
        assert.equal(state.tokens.length, 0);
    });
});
