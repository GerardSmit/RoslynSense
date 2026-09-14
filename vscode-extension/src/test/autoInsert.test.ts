import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { deferred, loadWithMocks } from './mockModule';

class Position {
    constructor(readonly line: number, readonly character: number) {}
    translate(line: number, character: number) {
        return new Position(this.line + line, this.character + character);
    }
}

function setup() {
    const reply = deferred<unknown>();
    let requests = 0;
    let edits = 0;
    let changed!: (event: unknown) => Promise<void>;
    const document = {
        languageId: 'csharp', version: 1, isClosed: false,
        uri: { toString: () => 'file:///Example.cs' },
        lineAt: () => ({ text: '    ///' }),
    };
    const editor = {
        document,
        edit: async (apply: (builder: unknown) => void) => {
            apply({ insert: () => edits++ });
            return true;
        },
        selection: undefined,
    };
    const window: { activeTextEditor: typeof editor | undefined } = { activeTextEditor: editor };
    const connection = { sendRequest: () => { requests++; return reply.promise; } };
    const module = loadWithMocks<typeof import('../autoInsert')>(require.resolve('../autoInsert'), {
        vscode: {
            workspace: { onDidChangeTextDocument: (callback: typeof changed) => { changed = callback; } },
            window, Position, Selection: class {},
        },
    });
    module.registerOnAutoInsert({ subscriptions: [] } as unknown as vscode.ExtensionContext,
        () => connection as unknown as LanguageClient, (uri) => uri.toString());
    const event = {
        document,
        contentChanges: [{ text: '/', rangeLength: 0, range: { start: new Position(0, 6) } }],
    };
    return {
        reply, document, window, event, changed,
        requests: () => requests, edits: () => edits,
        answer: () => reply.resolve({ edit: { newText: ' <summary>\n/// \n/// </summary>' },
            cursor: { line: 1, character: 4 } }),
    };
}

describe('XML documentation auto-insert', () => {
    it('inserts a skeleton when the triggering buffer remains current', async () => {
        const state = setup();
        const pending = state.changed(state.event);
        state.answer();
        await pending;
        assert.equal(state.requests(), 1);
        assert.equal(state.edits(), 1);
    });

    it('ignores a delayed answer after an edit elsewhere in the same live document', async () => {
        const state = setup();
        const pending = state.changed(state.event);
        state.document.version++; // The /// prefix is deliberately unchanged.
        state.answer();
        await pending;
        assert.equal(state.edits(), 0);
    });

    it('ignores a delayed answer after the editor loses focus', async () => {
        const state = setup();
        const pending = state.changed(state.event);
        state.window.activeTextEditor = undefined;
        state.answer();
        await pending;
        assert.equal(state.edits(), 0);
    });

    it('does not treat pasted multiline text as typing the third slash', async () => {
        const state = setup();
        state.event.contentChanges[0].text = '\n///';
        await state.changed(state.event);
        assert.equal(state.requests(), 0);
    });

    it('keeps server disconnects from rejecting a workspace event handler', async () => {
        const state = setup();
        const pending = state.changed(state.event);
        state.reply.reject(new Error('connection closed'));
        await assert.doesNotReject(pending);
        assert.equal(state.edits(), 0);
    });
});
