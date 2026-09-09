import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { deferred, loadWithMocks } from './mockModule';

function setup(vscodeExtras: Record<string, unknown> = {}) {
    const module = loadWithMocks<typeof import('../runAdapter')>(require.resolve('../runAdapter'), {
        vscode: { EventEmitter: class {
            private callbacks = new Set<(message: any) => void>();
            event = (callback: (message: any) => void) => {
                this.callbacks.add(callback);
                return { dispose: () => this.callbacks.delete(callback) };
            };
            fire(message: any) { this.callbacks.forEach(callback => callback(message)); }
            dispose() { this.callbacks.clear(); }
        }, ...vscodeExtras },
    });
    const adapter = new module.RunAdapter();
    const messages: any[] = [];
    adapter.onDidSendMessage(message => messages.push(message));
    const wait = (predicate: (message: any) => boolean): Promise<any> => {
        const existing = messages.find(predicate);
        if (existing) { return Promise.resolve(existing); }
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { subscription.dispose(); reject(new Error('No adapter response')); }, 10000);
            const subscription = adapter.onDidSendMessage(message => {
                if (predicate(message)) { clearTimeout(timer); subscription.dispose(); resolve(message); }
            });
        });
    };
    let sequence = 0;
    const request = async (command: string, args = {}) => {
        const seq = ++sequence;
        adapter.handleMessage({ type: 'request', seq, command, arguments: args } as any);
        return wait(message => message.type === 'response' && message.request_seq === seq);
    };
    return { adapter, messages, wait, request };
}

function terminalHost(delayedCreation = false) {
    const callbacks = new Map<string, Set<(event: any) => void>>();
    const created = deferred<any>();
    let execution: any;
    let terminations = 0;
    const listen = (kind: string) => (callback: (event: any) => void) => {
        const listeners = callbacks.get(kind) ?? new Set();
        callbacks.set(kind, listeners);
        listeners.add(callback);
        return { dispose: () => listeners.delete(callback) };
    };
    const emit = (kind: string, event: any) => callbacks.get(kind)?.forEach(callback => callback(event));
    return {
        vscode: {
            TaskScope: { Workspace: 2 }, TaskRevealKind: { Always: 1 }, TaskPanelKind: { Dedicated: 2 },
            ProcessExecution: class {
                constructor(readonly process: string, readonly args: string[], readonly options: any) {}
            },
            Task: class {
                constructor(readonly definition: any, _scope: unknown, readonly name: string,
                    _source: string, readonly execution: any) {}
            },
            tasks: {
                onDidStartTaskProcess: listen('start'), onDidEndTaskProcess: listen('processEnd'), onDidEndTask: listen('end'),
                executeTask: (task: any) => {
                    execution = { task, terminate: () => { terminations++; } };
                    return delayedCreation ? created.promise : Promise.resolve(execution);
                },
            },
        },
        task: () => execution.task,
        terminations: () => terminations,
        resolveCreation: () => created.resolve(execution),
        rejectCreation: () => created.reject(new Error('Terminal could not be created')),
        start: (processId: number) => emit('start', { execution, processId }),
        end: (exitCode: number) => {
            emit('processEnd', { execution, exitCode });
            emit('end', { execution });
        },
        endWithoutProcess: () => emit('end', { execution }),
        unrelatedStart: () => emit('start', { execution: { task: { definition: {
            type: 'roslynsense-run', runId: 'another-run',
        } } }, processId: 99999 }),
        listenerCount: () => [...callbacks.values()].reduce((sum, listeners) => sum + listeners.size, 0),
    };
}

describe('Run Without Debugging adapter', () => {
    it('runs the prepared program in a terminal with its PID and waits for termination before replying', async () => {
        const terminal = terminalHost();
        const state = setup(terminal.vscode);
        try {
            const launched = state.request('launch', { program: 'C:/repo/App.dll',
                console: 'integratedTerminal', args: ['argument with spaces', '$literal;argument'],
                env: { DOTNET_STARTUP_HOOKS: 'C:/agent/hook.dll', DOTNET_MODIFIABLE_ASSEMBLIES: 'debug' },
                cwd: 'C:/repo' });
            const task = terminal.task();
            assert.equal(task.execution.process, 'dotnet');
            assert.deepEqual(Array.from(task.execution.args), ['C:/repo/App.dll', 'argument with spaces', '$literal;argument']);
            assert.equal(task.execution.options.cwd, 'C:/repo');
            assert.equal(task.execution.options.env.DOTNET_STARTUP_HOOKS, 'C:/agent/hook.dll');
            assert.equal(task.execution.options.env.DOTNET_MODIFIABLE_ASSEMBLIES, 'debug');
            assert.equal(task.presentationOptions.focus, true);
            terminal.unrelatedStart();
            assert.equal(state.messages.length, 0);
            terminal.start(12345);
            assert.equal((await launched).success, true);
            assert.equal((await state.wait(message => message.event === 'process')).body.systemProcessId, 12345);

            let stopped = false;
            const stopping = state.request('terminate').then(response => { stopped = true; return response; });
            await new Promise(resolve => setImmediate(resolve));
            assert.equal(terminal.terminations(), 1);
            assert.equal(stopped, false);
            terminal.end(7);
            assert.equal((await stopping).success, true);
            assert.equal((await state.wait(message => message.event === 'exited')).body.exitCode, 7);
            assert.equal(state.messages.filter(message => message.event === 'terminated').length, 1);
            assert.equal(terminal.listenerCount(), 0);
        } finally { state.adapter.dispose(); }
    });

    it('terminates a terminal allocated after disconnect and never reports a successful launch', async () => {
        const terminal = terminalHost(true);
        const state = setup(terminal.vscode);
        try {
            const launching = state.request('launch', { program: 'app.exe', console: 'integratedTerminal' });
            const stopping = state.request('disconnect');
            assert.equal(terminal.terminations(), 0);
            terminal.resolveCreation();
            await new Promise(resolve => setImmediate(resolve));
            assert.equal(terminal.terminations(), 1);
            terminal.start(12345);
            terminal.end(0);
            assert.equal((await launching).success, false);
            assert.equal((await stopping).success, true);
            assert.equal(state.messages.filter(message => message.event === 'terminated').length, 1);
            assert.equal(terminal.listenerCount(), 0);
        } finally { state.adapter.dispose(); }
    });

    it('reports a terminal task that ends before process startup as a failed launch', async () => {
        const terminal = terminalHost();
        const state = setup(terminal.vscode);
        try {
            const launching = state.request('launch', { program: 'missing.exe', console: 'integratedTerminal' });
            terminal.endWithoutProcess();
            assert.equal((await launching).success, false);
            assert.equal(state.messages.filter(message => message.event === 'terminated').length, 1);
            assert.equal(terminal.listenerCount(), 0);
        } finally { state.adapter.dispose(); }
    });

    it('releases terminal listeners and pending disconnect when task creation fails', async () => {
        const terminal = terminalHost(true);
        const state = setup(terminal.vscode);
        try {
            const launching = state.request('launch', { program: 'app.exe', console: 'integratedTerminal' });
            const stopping = state.request('disconnect');
            terminal.rejectCreation();
            assert.equal((await launching).success, false);
            assert.equal((await stopping).success, true);
            assert.equal(terminal.terminations(), 0);
            assert.equal(terminal.listenerCount(), 0);
        } finally { state.adapter.dispose(); }
    });

    it('runs a real process with arguments and environment, forwards output, and stops it before replying', async () => {
        const state = setup();
        try {
            const initialized = await state.request('initialize');
            assert.notEqual(initialized.body.supportsRestartRequest, true);
            const launched = await state.request('launch', { program: process.execPath,
                args: ['-e', 'process.stdout.write(process.env.ROSLYNSENSE_RUN_TEST + ":" + process.argv[1]); setInterval(() => {}, 1000);', '--', 'argument with spaces'],
                env: { ROSLYNSENSE_RUN_TEST: 'injected' }, cwd: process.cwd() });
            assert.equal(launched.success, true);
            const target = await state.wait(message => message.event === 'process');
            assert.ok(target.body.systemProcessId > 0);
            const output = await state.wait(message => message.event === 'output');
            assert.equal(output.body.output, 'injected:argument with spaces');
            assert.equal((await state.request('terminate')).success, true);
            assert.throws(() => process.kill(target.body.systemProcessId, 0));
            assert.equal(state.messages.filter(message => message.event === 'terminated').length, 1);
        } finally { state.adapter.dispose(); }
    });

    it('reports a missing executable as a failed launch and ends the run once', async () => {
        const state = setup();
        try {
            const result = await state.request('launch', { program: 'roslynsense-nonexistent-test-executable' });
            assert.equal(result.success, false);
            await state.wait(message => message.event === 'terminated');
            assert.equal(state.messages.filter(message => message.event === 'terminated').length, 1);
        } finally { state.adapter.dispose(); }
    });

    it('forwards stderr and the real exit code on natural completion', async () => {
        const state = setup();
        try {
            await state.request('launch', { program: process.execPath,
                args: ['-e', 'process.stderr.write("failure"); process.exitCode = 7;'] });
            assert.equal((await state.wait(message => message.event === 'exited')).body.exitCode, 7);
            assert.equal(state.messages.find(message => message.event === 'output').body.category, 'stderr');
        } finally { state.adapter.dispose(); }
    });
});
