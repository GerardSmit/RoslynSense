import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

interface RuntimeValue { pid: number; input: string; message: string; length: number }

function readValue(file: string): RuntimeValue | undefined {
    try {
        const text = fs.readFileSync(file, 'utf8');
        // Ignore a partially appended last line while the target is writing.
        const lines = text.slice(0, text.lastIndexOf('\n')).trim().split(/\r?\n/);
        const match = /^(\d+)\|([^|])\|(.+)$/.exec(lines.at(-1) ?? '');
        return match ? { pid: Number(match[1]), input: match[2], message: match[3], length: text.length } : undefined;
    } catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'ENOENT') { return undefined; }
        throw error;
    }
}

async function eventually<T>(probe: () => T | undefined, description: string, timeout = 30_000): Promise<T> {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
        const result = probe();
        if (result !== undefined) { return result; }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error(`Timed out waiting for ${description}.`);
}

async function bounded<T>(operation: Thenable<T>, description: string, timeout = 60_000): Promise<T> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
        return await Promise.race([Promise.resolve(operation), new Promise<never>((_, reject) => {
            timer = setTimeout(() => reject(new Error(`Timed out during ${description}.`)), timeout);
        })]);
    } finally {
        if (timer) { clearTimeout(timer); }
    }
}

/** The real editor command, terminal task, LSP buffer sync, delta emitter and running CLR. */
export async function testHotReload(root: vscode.WorkspaceFolder): Promise<void> {
    const projectPath = path.join(root.uri.fsPath, 'Fixture.csproj');
    const sourcePath = path.join(root.uri.fsPath, 'Greeter.cs');
    const log = path.join(root.uri.fsPath, 'bin', 'Debug', 'net10.0', 'hotreload-values.txt');
    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(sourcePath));
    await vscode.window.showTextDocument(document);
    const original = document.getText();
    const originalDisk = fs.readFileSync(sourcePath, 'utf8');
    assert.ok(original.includes('"hello"'), 'The fixture must start with its original greeting.');
    fs.rmSync(log, { force: true });

    let session: vscode.DebugSession | undefined;
    let runTask: vscode.TaskExecution | undefined;
    let terminalPid: number | undefined;
    let terminated = false;
    const listeners = [
        vscode.debug.onDidStartDebugSession(started => {
            if (started.configuration.projectPath?.toLowerCase() === projectPath.toLowerCase()) {
                session = started;
            }
        }),
        vscode.debug.onDidTerminateDebugSession(ended => {
            if (ended.id === session?.id) { terminated = true; }
        }),
        vscode.tasks.onDidStartTaskProcess(started => {
            if (started.execution.task.definition.type === 'roslynsense-run') {
                runTask = started.execution;
                terminalPid = started.processId;
            }
        }),
    ];

    const diagnostics = () => vscode.languages.getDiagnostics(document.uri)
        .map(diagnostic => `${diagnostic.source ?? ''} ${diagnostic.code ?? ''}: ${diagnostic.message}`).join('\n');
    try {
        await bounded(vscode.commands.executeCommand('roslynSense.runWithHotReload'), 'Run with Hot Reload', 120_000);
        const runningSession = await eventually(() => session, 'the VS Code run session');
        const terminalTask = await eventually(() => runTask, 'the integrated terminal ProcessExecution task');
        assert.ok(terminalTask.task.execution instanceof vscode.ProcessExecution);
        const terminal = await eventually(() => vscode.window.terminals.find(
            candidate => candidate.name.includes(terminalTask.task.name)), 'the target task terminal');
        // Console.ReadKey throws with redirected stdin. The fixture cannot reach its loop until
        // this real PTY input arrives, so output also proves interactive console support.
        terminal.sendText('R', false);
        const baseline = await eventually(() => {
            const value = readValue(log);
            return value?.message === 'hello' ? value : undefined;
        }, 'the launched target to print hello');
        assert.strictEqual(baseline.input, 'R', 'Console.ReadKey must receive input from the integrated terminal.');
        assert.strictEqual(terminalPid, baseline.pid, 'The terminal task must report the running CLR PID.');
        assert.strictEqual(runningSession.configuration.console, 'integratedTerminal');

        let previousMessage = 'hello';
        for (const nextMessage of ['first hot reload', 'second hot reload']) {
            const previousLength = readValue(log)!.length;
            const text = document.getText();
            const offset = text.indexOf(`"${previousMessage}"`);
            assert.ok(offset >= 0, 'The next edit must update the previous editor generation.');
            const edit = new vscode.WorkspaceEdit();
            edit.replace(document.uri, new vscode.Range(
                document.positionAt(offset), document.positionAt(offset + previousMessage.length + 2)),
            `"${nextMessage}"`);
            assert.strictEqual(await vscode.workspace.applyEdit(edit), true);
            assert.strictEqual(document.isDirty, true, 'Hot reload must consume an unsaved editor buffer.');

            await bounded(vscode.commands.executeCommand('roslynSense.applyHotReload'), `applying ${nextMessage}`);
            const updated = await eventually(() => {
                const value = readValue(log);
                return value && value.length > previousLength && value.message === nextMessage ? value : undefined;
            }, `fresh runtime output '${nextMessage}' (diagnostics: ${diagnostics()})`);
            assert.strictEqual(updated.pid, baseline.pid, 'Hot reload must update the existing process without restarting it.');
            assert.strictEqual(terminated, false, 'The original VS Code run session must remain alive.');
            assert.strictEqual(fs.readFileSync(sourcePath, 'utf8'), originalDisk, 'The edit must not depend on saving or rebuilding.');
            previousMessage = nextMessage;
        }
        console.log(`VS Code Hot Reload E2E: PID ${baseline.pid}, hello -> first hot reload -> second hot reload.`);
    } finally {
        try {
            await bounded(vscode.commands.executeCommand('roslynSense.stopHotReload'), 'closing the hot reload session', 15_000);
        } finally {
            try {
                if (session && !terminated) {
                    await bounded(vscode.debug.stopDebugging(session), 'stopping the target', 15_000);
                    await eventually(() => terminated ? true : undefined, 'the original run session to terminate', 15_000);
                }
            } finally {
                // Also covers a launch that failed before a debug session was reported.
                if (runTask && vscode.tasks.taskExecutions.includes(runTask)) { runTask.terminate(); }
                for (const listener of listeners) { listener.dispose(); }
                if (document.getText() !== original) {
                    const restore = new vscode.WorkspaceEdit();
                    restore.replace(document.uri, new vscode.Range(
                        document.positionAt(0), document.positionAt(document.getText().length)), original);
                    assert.strictEqual(await vscode.workspace.applyEdit(restore), true);
                    assert.strictEqual(await document.save(), true);
                }
            }
        }
    }
}
