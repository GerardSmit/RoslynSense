import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { deferred, loadWithMocks } from './mockModule';

class Items {
    private readonly values = new Map<string, Item>();
    get size() { return this.values.size; }
    get(id: string) { return this.values.get(id); }
    add(item: Item) { this.values.set(item.id, item); }
    delete(id: string) { this.values.delete(id); }
    replace(items: Item[]) { this.values.clear(); items.forEach(item => this.add(item)); }
    forEach(callback: (item: Item) => void) { this.values.forEach(callback); }
}

class Item {
    readonly children = new Items();
    canResolveChildren = false;
    busy = false;
    constructor(readonly id: string, readonly label: string, readonly uri?: unknown) {}
}

class Cancellation {
    isCancellationRequested = false;
    readonly listeners = new Set<() => void>();
    onCancellationRequested(callback: () => void) {
        this.listeners.add(callback);
        return { dispose: () => this.listeners.delete(callback) };
    }
    cancel() { this.isCancellationRequested = true; this.listeners.forEach(callback => callback()); }
}

const firstProject = 'C:/repo/First/First.csproj';
const secondProject = 'C:/repo/Second/Second.csproj';
const test = (name: string, projectPath = firstProject, className = 'Cases') => ({
    id: `Tests.${className}.${name}`, fullyQualifiedName: `Tests.${className}.${name}`, displayName: name,
    className, namespace: 'Tests', framework: 'xunit', filePath: `${projectPath}/Tests.cs`,
    startLine: 4, endLine: 8, projectPath,
});

function setup() {
    type Request = { include?: Item[]; exclude?: Item[] };
    type Handler = (request: Request, token: Cancellation) => Promise<void>;
    type RunEvent = { kind: string; id?: string; message?: string; duration?: number };
    const ready: ((client: unknown) => void)[] = [];
    const profiles = new Map<string, Handler>();
    const requests: { client: string; method: string; params: Record<string, unknown> }[] = [];
    const notifications: { method: string; params: Record<string, unknown> }[] = [];
    const events = new Map<string, (event: unknown) => void>();
    const responses = new Map<string, (params: Record<string, unknown>) => unknown>();
    const discovered = new Map<string, ReturnType<typeof test>[]>([[firstProject, [test('One'), test('Two')]]]);
    const state = { projects: [{ projectPath: firstProject, projectName: 'First' }], connected: true };
    const errors: string[] = [];
    const runs: { events: RunEvent[]; ended: boolean }[] = [];
    const controller = {
        items: new Items(),
        resolveHandler: undefined as ((item?: Item) => Promise<void>) | undefined,
        refreshHandler: undefined as (() => Promise<void>) | undefined,
        createTestItem: (id: string, label: string, uri: unknown) => new Item(id, label, uri),
        createRunProfile: (label: string, _kind: unknown, handler: Handler) => {
            profiles.set(label, handler);
            return { runHandler: handler };
        },
        createTestRun: () => {
            const run = { events: [] as RunEvent[], ended: false };
            runs.push(run);
            const report = (kind: string) => (item: Item, message?: { message: string } | number, duration?: number) => {
                run.events.push({ kind, id: item.id, message: typeof message === 'object' ? message.message : undefined,
                    duration: typeof message === 'number' ? message : duration });
            };
            return { enqueued: report('enqueued'), started: report('started'), passed: report('passed'),
                failed: report('failed'), skipped: report('skipped'), errored: report('errored'),
                appendOutput: (message: string) => run.events.push({ kind: 'output', message }),
                addCoverage: () => {}, end: () => { run.ended = true; } };
        },
    };
    const makeClient = (name: string) => ({
        sendRequest: async (method: string, params: Record<string, unknown> = {}) => {
            requests.push({ client: name, method, params });
            if (responses.has(method)) { return responses.get(method)!(params); }
            if (method === 'roslynSense/testProjects') { return state.projects; }
            if (method === 'roslynSense/testDiscover') { return discovered.get(String(params.projectPath)) ?? []; }
            if (method === 'roslynSense/testRun') { return { results: [], error: null }; }
            if (method === 'roslynSense/testCoverage') { return []; }
            throw new Error(`Unexpected request: ${method}`);
        },
        sendNotification: async (method: string, params: Record<string, unknown>) => { notifications.push({ method, params }); },
        onNotification: (method: string, callback: (event: unknown) => void) => {
            events.set(method, callback);
            return { dispose: () => events.delete(method) };
        },
    });
    const client = makeClient('original');
    let activeClient = client;
    let attach = true;
    const attached: unknown[] = [];
    const module = loadWithMocks<typeof import('../testController')>(require.resolve('../testController'), {
        vscode: {
            tests: { createTestController: () => controller },
            TestRunProfileKind: { Run: 1, Debug: 2, Coverage: 3 },
            TestRunRequest: class { constructor(readonly include: Item[], readonly exclude: Item[]) {} },
            TestMessage: class { constructor(readonly message: string) {} },
            Uri: { file: (fsPath: string) => ({ fsPath }) },
            Range: class { constructor(..._args: number[]) {} },
            Location: class { constructor(readonly uri: unknown, readonly range: unknown) {} },
            workspace: { onDidSaveTextDocument: () => ({ dispose() {} }) },
            window: { showErrorMessage: (message: string) => { errors.push(message); } },
            commands: { executeCommand: async () => {} },
            debug: { startDebugging: async (_folder: unknown, config: unknown) => { attached.push(config); return attach; } },
        },
        './clientReady': { onClientReady: (_context: unknown, _getClient: unknown, callback: typeof ready[number]) => {
            ready.push(callback);
        } },
        './debugLaunch': { DEBUG_TYPE: 'roslynsense' },
    });
    module.registerTestController({ subscriptions: [] } as unknown as vscode.ExtensionContext,
        () => state.connected ? activeClient as unknown as LanguageClient : undefined);
    // Subscribe to streaming events without starting the separate eager project-discovery callback.
    ready[0](client);
    return { module, state, controller, requests, notifications, responses, discovered, errors, runs, attached,
        rejectAttach: () => { attach = false; },
        connect: () => ready[1](client),
        switchClient: () => { activeClient = makeClient('replacement'); ready.forEach(callback => callback(activeClient)); },
        discover: () => controller.refreshHandler!(),
        run: (request: Request = {}, token = new Cancellation(), profile = 'Run') => profiles.get(profile)!(request, token),
        emit: (event: unknown) => events.get('roslynSense/testRunEvent')!(event),
    };
}

describe('Test Explorer lifecycle', () => {
    it('waits for project discovery when Run All is invoked during initial connection', async () => {
        const state = setup();
        const projects = deferred<unknown>();
        state.responses.set('roslynSense/testProjects', () => projects.promise);
        state.connect();

        const pending = state.run();
        projects.resolve(state.state.projects);
        await pending;

        assert.equal(state.requests.filter(r => r.method === 'roslynSense/testRun').length, 1);
        assert.equal(state.runs[0].events.filter(e => e.kind === 'enqueued').length, 2);
        assert.equal(state.runs[0].ended, true);
    });

    it('removes obsolete projects from subsequent discovery after a solution change', async () => {
        const state = setup();
        await state.discover();
        state.state.projects = [{ projectPath: secondProject, projectName: 'Second' }];
        state.discovered.set(secondProject, [test('New', secondProject)]);
        state.requests.length = 0;

        await state.discover();

        assert.equal(state.controller.items.get(`project:${firstProject}`), undefined);
        assert.deepEqual(state.requests.filter(r => r.method === 'roslynSense/testDiscover')
            .map(r => r.params.projectPath), [secondProject]);
    });

    it('does not let delayed project discovery from a replaced client overwrite the new solution', async () => {
        const state = setup();
        const oldProjects = deferred<unknown>();
        const first = state.state.projects;
        let requests = 0;
        state.responses.set('roslynSense/testProjects', () => requests++ === 0 ? oldProjects.promise : state.state.projects);
        state.connect();
        const pending = state.run();
        state.state.projects = [{ projectPath: secondProject, projectName: 'Second' }];
        state.discovered.set(secondProject, [test('New', secondProject)]);
        state.switchClient();

        oldProjects.resolve(first);
        await pending;

        assert.equal(state.controller.items.get(`project:${firstProject}`), undefined);
        assert.ok(state.controller.items.get(`project:${secondProject}`));
        assert.deepEqual(state.requests.filter(r => r.method === 'roslynSense/testRun').map(r => r.params.projectPath), [secondProject]);
        assert.equal(state.requests.find(r => r.method === 'roslynSense/testRun')!.client, 'replacement');
    });

    it('runs overlapping selections only once and respects excluded class subtrees', async () => {
        const state = setup();
        state.discovered.set(firstProject, [test('One'), test('Two'), test('Excluded', firstProject, 'Other')]);
        await state.discover();
        const project = state.controller.items.get(`project:${firstProject}`)!;
        const cases = project.children.get(`class:${firstProject}:Tests.Cases`)!;
        const excluded = project.children.get(`class:${firstProject}:Tests.Other`)!;

        await state.run({ include: [project, cases.children.get('Tests.Cases.One')!], exclude: [excluded] });

        const names = state.requests.find(r => r.method === 'roslynSense/testRun')!.params.fullyQualifiedNames as string[];
        assert.equal(names.length, 2);
        assert.equal(new Set(names).size, 2);
        assert.ok(!names.includes('Tests.Other.Excluded'));
    });

    it('routes live outcomes by run ID, preserves them on cancellation, and unregisters finished runs', async () => {
        const state = setup();
        await state.discover();
        const response = deferred<unknown>();
        const started = deferred<Record<string, unknown>>();
        state.responses.set('roslynSense/testRun', params => { started.resolve(params); return response.promise; });
        const token = new Cancellation();
        const pending = state.run({}, token);
        const params = await started.promise;
        const emit = (runId: unknown, name: string) => state.emit({ runId, kind: 'passed',
            fullyQualifiedName: name, message: null, durationMs: 12 });
        emit('other-run', 'Tests.Cases.Two');
        emit(params.runId, 'Tests.Cases.One(value: 42)');
        state.emit({ runId: params.runId, kind: 'output', message: 'Running tests' });
        token.cancel();
        response.resolve({ results: [], error: 'Cancelled' });
        await pending;

        const run = state.runs[0];
        assert.equal(state.notifications.length, 1);
        assert.equal(state.notifications[0].method, 'roslynSense/testCancel');
        assert.equal(state.notifications[0].params.runId, params.runId);
        assert.equal(run.events.filter(e => e.kind === 'passed').length, 1);
        assert.equal(run.events.find(e => e.kind === 'passed')!.id, 'Tests.Cases.One');
        assert.equal(run.events.find(e => e.kind === 'output')!.message, 'Running tests\r\n');
        assert.equal(run.events.find(e => e.kind === 'errored')!.id, 'Tests.Cases.Two');
        assert.equal(token.listeners.size, 0);
        const count = run.events.length;
        emit(params.runId, 'Tests.Cases.Two');
        assert.equal(run.events.length, count);
        assert.equal(run.ended, true);
    });

    it('reports a failed test request on the affected items and always ends the run', async () => {
        const state = setup();
        await state.discover();
        state.responses.set('roslynSense/testRun', () => { throw new Error('connection closed'); });
        const token = new Cancellation();

        await state.run({}, token);

        assert.equal(state.runs[0].events.filter(e => e.kind === 'errored').length, 2);
        assert.ok(state.runs[0].events.filter(e => e.kind === 'errored').every(e => e.message?.includes('connection closed')));
        assert.equal(state.runs[0].ended, true);
        assert.equal(token.listeners.size, 0);
    });

    it('keeps a successful test outcome when optional coverage retrieval fails', async () => {
        const state = setup();
        await state.discover();
        state.responses.set('roslynSense/testRun', () => ({ results: [{ fullyQualifiedName: 'Tests.Cases.One',
            outcome: 'Passed', durationMs: 13, errorMessage: null, stackTrace: null }], error: null }));
        state.responses.set('roslynSense/testCoverage', () => { throw new Error('no coverage'); });

        await state.run({}, new Cancellation(), 'Coverage');

        assert.equal(state.requests.find(r => r.method === 'roslynSense/testRun')!.params.collectCoverage, true);
        assert.equal(state.runs[0].events.find(e => e.kind === 'passed')!.duration, 13);
        assert.equal(state.errors.length, 0);
        assert.equal(state.runs[0].ended, true);
    });

    it('does not start a debugger for a failed host and reports debugger attach failures', async () => {
        const state = setup();
        await state.discover();
        state.responses.set('roslynSense/testDebug', () => ({ processId: 0, error: 'Build failed' }));
        await state.run({}, new Cancellation(), 'Debug');
        assert.equal(state.attached.length, 0);
        assert.ok(state.runs[0].events.filter(e => e.kind === 'errored').every(e => e.message === 'Build failed'));

        state.responses.set('roslynSense/testDebug', () => ({ processId: 1234, error: null }));
        state.rejectAttach();
        await state.run({}, new Cancellation(), 'Debug');
        assert.equal(state.attached.length, 1);
        assert.equal((state.attached[0] as { processId: number }).processId, 1234);
        assert.equal(state.runs[1].events.filter(e => e.kind === 'errored').length, 2);
        assert.equal(state.runs[1].ended, true);
    });
});
