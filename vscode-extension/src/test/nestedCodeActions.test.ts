import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { loadWithMocks } from './mockModule';

interface Group {
    title: string;
    id?: number | null;
    children?: Group[];
}

function setup() {
    let invoke!: (group: Group, clientKey?: string) => Promise<void>;
    const selections: (number | undefined)[] = [];
    const pickers: { items: { label: string; description?: string; child: Group }[]; title: string }[] = [];
    const clientKeys: (string | undefined)[] = [];
    const requests: { method: string; action: { title: string; kind: string; data: { id: number } } }[] = [];
    const converted: unknown[] = [];
    const applied: unknown[] = [];
    const warnings: string[] = [];
    const protocolEdit = { changes: { 'file:///Second/Example.cs': [] } };
    const workspaceEdit = { converted: true };
    const response: { edit: typeof protocolEdit | null; error?: unknown } = { edit: protocolEdit };
    const client = {
        sendRequest: async (method: string, action: typeof requests[number]['action']) => {
            requests.push({ method, action });
            if (response.error !== undefined) { throw response.error; }
            return response;
        },
        protocol2CodeConverter: { asWorkspaceEdit: async (edit: unknown) => {
            converted.push(edit);
            return workspaceEdit;
        } },
    };
    const module = loadWithMocks<typeof import('../nestedCodeActions')>(require.resolve('../nestedCodeActions'), {
        vscode: {
            commands: { registerCommand: (_name: string, callback: typeof invoke) => { invoke = callback; } },
            window: {
                showQuickPick: async (items: typeof pickers[number]['items'], options: { title: string }) => {
                    pickers.push({ items, title: options.title });
                    const index = selections.shift();
                    return index === undefined ? undefined : items[index];
                },
                showWarningMessage: (message: string) => { warnings.push(message); },
            },
            workspace: { applyEdit: async (edit: unknown) => { applied.push(edit); return true; } },
        },
    });
    module.registerNestedCodeActions({ subscriptions: [] } as unknown as vscode.ExtensionContext,
        (key) => {
            clientKeys.push(key);
            // IDs belong to the client that supplied the action, even if another root is active.
            assert.equal(key, 'second-root');
            return client as unknown as LanguageClient;
        });
    return { module, invoke, selections, pickers, clientKeys, requests, converted, applied,
        warnings, protocolEdit, workspaceEdit, response };
}

describe('nested code actions', () => {
    it('routes the selected leaf back to its originating client and applies the converted edit', async () => {
        const state = setup();
        const group: Group = { title: 'Configure rule', children: [
            { title: 'Severity', children: [{ title: 'Error', id: 0 }, { title: 'Warning', id: 1 }] },
            { title: 'Suppress', id: 2 },
        ] };
        const command: vscode.Command = {
            title: group.title, command: state.module.PICK_NESTED_ACTION_COMMAND, arguments: [group],
        };
        const ordinary: vscode.Command = { title: 'Other fix', command: 'other.command', arguments: ['keep'] };
        state.module.bindNestedCodeActions([
            { title: group.title, command }, { title: ordinary.title, command: ordinary }, ordinary,
        ], 'second-root');
        state.selections.push(0, 0);

        await state.invoke(command.arguments![0], command.arguments![1]);

        assert.deepEqual(state.clientKeys, ['second-root']);
        assert.equal(command.arguments!.length, 2);
        assert.equal(command.arguments![0], group);
        assert.deepEqual(ordinary.arguments, ['keep']);
        assert.deepEqual(state.pickers.map(p => p.title), ['Configure rule', 'Severity']);
        assert.equal(state.pickers[0].items[0].description, '…');
        assert.equal(state.requests.length, 1);
        assert.equal(state.requests[0].method, 'codeAction/resolve');
        assert.equal(state.requests[0].action.title, 'Error');
        assert.equal(state.requests[0].action.kind, 'quickfix');
        assert.equal(state.requests[0].action.data.id, 0);
        assert.deepEqual(state.converted, [state.protocolEdit]);
        assert.deepEqual(state.applied, [state.workspaceEdit]);
        assert.equal(state.warnings.length, 0);
    });

    it('abandons the entire action when a deeper picker is dismissed', async () => {
        const state = setup();
        state.selections.push(0, undefined);
        await state.invoke({ title: 'Configure', children: [
            { title: 'Severity', children: [{ title: 'Error', id: 3 }, { title: 'Warning', id: 4 }] },
            { title: 'Suppress', id: 5 },
        ] }, 'second-root');

        assert.equal(state.pickers.length, 2);
        assert.equal(state.requests.length, 0);
        assert.equal(state.applied.length, 0);
        assert.equal(state.warnings.length, 0);
    });

    it('skips single-choice groups and warns when the selected action has expired', async () => {
        const state = setup();
        state.response.edit = null;
        await state.invoke({ title: 'Configure', children: [
            { title: 'Rule', children: [{ title: 'Set severity', id: 7 }] },
        ] }, 'second-root');

        assert.equal(state.pickers.length, 0);
        assert.equal(state.requests.length, 1);
        assert.equal(state.requests[0].action.data.id, 7);
        assert.equal(state.converted.length, 0);
        assert.equal(state.applied.length, 0);
        assert.equal(state.warnings.length, 1);
        assert.match(state.warnings[0], /Set severity.*could not be applied/);
    });

    it('offers a fresh menu after ContentModified without applying an expired edit', async () => {
        const state = setup();
        state.response.error = { code: -32801, message: 'Expired' };
        await state.invoke({ title: 'Old action', id: 8 }, 'second-root');
        assert.equal(state.applied.length, 0);
        assert.equal(state.converted.length, 0);
        assert.match(state.warnings[0], /Reopen the lightbulb menu/);
    });

    it('does not resolve an empty group without a valid action ID', async () => {
        const state = setup();
        await state.invoke({ title: 'No choices', children: [] }, 'second-root');
        await state.invoke({ title: 'No choices', id: null }, 'second-root');

        assert.equal(state.pickers.length, 0);
        assert.equal(state.requests.length, 0);
        assert.equal(state.applied.length, 0);
    });
});
