import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import type * as vscode from 'vscode';
import { loadWithMocks } from './mockModule';

const file = (fsPath: string) => ({ fsPath }) as vscode.Uri;
const moduleUnderTest = loadWithMocks<typeof import('../solutionUndo')>(require.resolve('../solutionUndo'), {
    vscode: {
        FileType: { File: 1, Directory: 2 },
        Uri: { joinPath: (uri: vscode.Uri, ...parts: string[]) => file(path.join(uri.fsPath, ...parts)) },
        workspace: { fs: {
            stat: async (uri: vscode.Uri) => {
                const stat = await fs.stat(uri.fsPath);
                return { type: stat.isDirectory() ? 2 : 1, size: stat.size };
            },
            readFile: (uri: vscode.Uri) => fs.readFile(uri.fsPath),
            readDirectory: async (uri: vscode.Uri) => (await fs.readdir(uri.fsPath, { withFileTypes: true }))
                .map(entry => [entry.name, entry.isDirectory() ? 2 : 1]),
            createDirectory: (uri: vscode.Uri) => fs.mkdir(uri.fsPath, { recursive: true }),
            writeFile: (uri: vscode.Uri, content: Uint8Array) => fs.writeFile(uri.fsPath, content),
        } },
    },
});

describe('Solution Explorer undo', () => {
    it('restores deleted nested directories, binary files, and empty directories exactly', async () => {
        const root = await fs.mkdtemp(path.join(os.tmpdir(), 'roslynsense-undo-'));
        const target = path.join(root, 'deleted');
        const binary = Buffer.from([0, 255, 128, 13, 10, 0, 42]);
        try {
            await fs.mkdir(path.join(target, 'assets', 'icons'), { recursive: true });
            await fs.mkdir(path.join(target, 'empty', 'nested'), { recursive: true });
            await fs.writeFile(path.join(target, 'assets', 'icons', 'app.ico'), binary);
            await fs.writeFile(path.join(target, 'Program.cs'), 'class Program {}\r\n');
            const captured = await moduleUnderTest.snapshot(file(target));
            assert.ok(captured);

            await fs.rm(target, { recursive: true });
            await moduleUnderTest.restore(captured);

            assert.deepEqual(await fs.readFile(path.join(target, 'assets', 'icons', 'app.ico')), binary);
            assert.equal(await fs.readFile(path.join(target, 'Program.cs'), 'utf8'), 'class Program {}\r\n');
            assert.ok((await fs.stat(path.join(target, 'empty', 'nested'))).isDirectory());
        } finally {
            await fs.rm(root, { recursive: true, force: true });
        }
    });

    it('discards redo history when an edit creates a new branch of changes', async () => {
        const stack = new moduleUnderTest.UndoStack();
        const effects: string[] = [];
        const step = (label: string) => ({ label,
            undo: async () => { effects.push(`undo ${label}`); },
            redo: async () => { effects.push(`redo ${label}`); } });
        stack.push(step('rename'));
        stack.push(step('delete'));
        assert.equal(await stack.undo(), 'delete');
        assert.equal(stack.canRedo, true);

        stack.push(step('create'));

        assert.equal(stack.canRedo, false);
        assert.equal(await stack.redo(), undefined);
        assert.equal(await stack.undo(), 'create');
        assert.equal(await stack.undo(), 'rename');
        assert.deepEqual(effects, ['undo delete', 'undo create', 'undo rename']);
    });

    it('does not retry or offer the inverse of a partially failed undo or redo', async () => {
        for (const failure of ['undo', 'redo'] as const) {
            const stack = new moduleUnderTest.UndoStack();
            let attempts = 0;
            const action = async () => { attempts++; throw new Error('file is locked'); };
            stack.push({ label: 'Delete', undo: failure === 'undo' ? action : async () => {},
                redo: failure === 'redo' ? action : async () => {} });
            if (failure === 'redo') { await stack.undo(); }

            await assert.rejects(stack[failure](), /file is locked/);

            assert.equal(stack.canUndo, false);
            assert.equal(stack.canRedo, false);
            assert.equal(await stack[failure](), undefined);
            assert.equal(attempts, 1);
        }
    });

    it('undoes dependent edits in reverse order and stops after a partial failure', async () => {
        const operations: string[] = [];
        let fail = false;
        const group = moduleUnderTest.composite('Create folder and file', ['folder', 'file'].map(label => ({
            label,
            undo: async () => {
                operations.push(`undo ${label}`);
                if (fail) { throw new Error('locked'); }
            },
            redo: async () => { operations.push(`redo ${label}`); },
        })));
        await group.undo();
        await group.redo();
        assert.deepEqual(operations, ['undo file', 'undo folder', 'redo folder', 'redo file']);

        operations.length = 0;
        fail = true;
        await assert.rejects(group.undo(), /locked/);
        assert.deepEqual(operations, ['undo file']);
    });
});
