import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { deferred, loadWithMocks } from './mockModule';

const project = 'C:/repo/App/App.csproj';
const success = { ok: true, summary: 'Changes applied', diagnostics: [], appliedTo: ['App'], errors: [] };

function setup() {
    const commands = new Map<string, () => Promise<void>>();
    const contexts: boolean[] = [];
    const errors: string[] = [];
    const requests: { client: string; method: string; params?: unknown }[] = [];
    let change!: (event: unknown) => void;
    let save!: (document: unknown) => Promise<void>;
    let start: () => Promise<unknown> = async () => success;
    let stop: () => Promise<unknown> = async () => success;
    let apply: () => Promise<unknown> = async () => success;
    const settings = { available: true, variables: {
        DOTNET_STARTUP_HOOKS: 'agent.dll', DOTNET_MODIFIABLE_ASSEMBLIES: 'debug', CUSTOM: 'agent',
    }, message: '' };
    let environmentError = false;
    let debugStarted: (session: vscode.DebugSession) => void = () => {};
    let debugEnded: (session: vscode.DebugSession) => void = () => {};
    let trackerFactory: vscode.DebugAdapterTrackerFactory | undefined;
    const makeClient = (name: string) => ({ sendRequest: async (method: string, params?: unknown) => {
        requests.push({ client: name, method, params });
        if (method === 'roslynSense/hotReloadEnvironment') {
            if (environmentError) { throw new Error('server disconnected'); }
            return settings;
        }
        if (method === 'roslynSense/hotReloadStart') { return start(); }
        if (method === 'roslynSense/hotReloadStop') { return stop(); }
        if (method === 'roslynSense/hotReloadApply') { return apply(); }
        return success;
    } });
    const original = makeClient('original');
    let active = original;
    const module = loadWithMocks<typeof import('../hotReload')>(require.resolve('../hotReload'), {
        vscode: {
            commands: {
                registerCommand: (name: string, callback: () => Promise<void>) => { commands.set(name, callback); },
                executeCommand: (_name: string, _key: string, value: boolean) => { contexts.push(value); },
            },
            workspace: {
                onDidChangeTextDocument: (callback: typeof change) => { change = callback; },
                onDidSaveTextDocument: (callback: typeof save) => { save = callback; },
                getConfiguration: () => ({ get: () => true }),
            },
            languages: { createDiagnosticCollection: () => ({ clear() {}, set() {} }) },
            debug: {
                onDidStartDebugSession: (callback: typeof debugStarted) => { debugStarted = callback; return { dispose() {} }; },
                onDidTerminateDebugSession: (callback: typeof debugEnded) => { debugEnded = callback; return { dispose() {} }; },
                registerDebugAdapterTrackerFactory: (_type: string, factory: vscode.DebugAdapterTrackerFactory) => {
                    trackerFactory = factory; return { dispose() {} };
                },
            },
            window: {
                showWarningMessage: (message: string) => { errors.push(message); },
                showErrorMessage: (message: string) => { errors.push(message); },
                setStatusBarMessage: () => {},
                showInformationMessage: () => {},
            },
        },
    });
    module.registerHotReload({ subscriptions: [] } as unknown as vscode.ExtensionContext,
        () => active as unknown as LanguageClient);
    const document = { languageId: 'csharp', uri: { scheme: 'file', fsPath: 'C:/repo/App/Program.cs' } };
    return { module, settings, requests, contexts, errors,
        bind: () => module.bindHotReloadSession(original as unknown as LanguageClient, project),
        bindOther: () => module.bindHotReloadSession(makeClient('other-bound') as unknown as LanguageClient,
            'C:/other/Other.csproj'),
        environment: (env: Record<string, string>) => module.withHotReloadEnvironment(
            original as unknown as LanguageClient, env, project),
        failEnvironment: () => { environmentError = true; },
        switchRoot: () => { active = makeClient('other-root'); },
        restart: (session: vscode.DebugSession) => module.restartDebugHotReload(original as unknown as LanguageClient, session),
        startWith: (handler: typeof start) => { start = handler; },
        stopWith: (handler: typeof stop) => { stop = handler; },
        applyWith: (handler: typeof apply) => { apply = handler; },
        edit: () => change({ document, contentChanges: [{ text: 'changed' }] }),
        save: () => save(document),
        apply: () => commands.get('roslynSense.applyHotReload')!(),
        debugStarted: (id: string, projectPath = project) => {
            const session = { id, configuration: { projectPath, roslynSenseHotReloadOwner: module.hotReloadOwnerFor(projectPath) } } as unknown as vscode.DebugSession;
            debugStarted(session);
            return session;
        },
        debugEnded: (session: vscode.DebugSession) => debugEnded(session),
        processEvent: async (session: vscode.DebugSession, pid: number) => {
            const tracker = await trackerFactory?.createDebugAdapterTracker(session);
            tracker?.onDidSendMessage?.({ type: 'event', event: 'process', body: { systemProcessId: pid } });
        },
        stop: () => commands.get('roslynSense.stopHotReload')!(),
    };
}

describe('hot reload lifecycle', () => {
    it('waits for the baseline before returning the launch environment', async () => {
        const state = setup();
        const start = deferred<unknown>();
        state.startWith(() => start.promise);
        let finished = false;
        const environment = state.environment({}).then(() => { finished = true; });
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(finished, false);
        start.resolve(success);
        await environment;
        assert.equal(finished, true);
    });

    it('reports failed baseline initialization and releases its owner', async () => {
        const state = setup();
        state.startWith(async () => ({ ...success, ok: false, summary: 'Missing portable PDB' }));
        await state.environment({});
        assert.equal(state.module.hotReloadOwnerFor(project), undefined);
        assert.match(state.errors[0], /Missing portable PDB/);
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/hotReloadStop').length, 1);
    });

    it('replaces the baseline on restart after any pending termination release completes', async () => {
        const state = setup();
        await state.bind();
        const session = state.debugStarted('restart');
        const firstOwner = state.module.hotReloadOwnerFor(project);
        const stopped = deferred<unknown>();
        state.stopWith(() => stopped.promise);
        state.debugEnded(session);
        const restarted = state.restart(session);
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/hotReloadStart').length, 1);
        stopped.resolve(success);
        await restarted;
        assert.notEqual(state.module.hotReloadOwnerFor(project), firstOwner);
        await state.processEvent(session, 456);
        await new Promise(resolve => setImmediate(resolve));
        const last = state.requests.filter(r => r.method === 'roslynSense/hotReloadStart').at(-1)!;
        assert.equal((last.params as { ownerId: string }).ownerId, state.module.hotReloadOwnerFor(project));
        const secondRelease = deferred<unknown>();
        state.stopWith(() => secondRelease.promise);
        state.debugEnded(session);
        const count = state.requests.filter(r => r.method === 'roslynSense/hotReloadStart').length;
        const secondRestart = state.restart(session);
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/hotReloadStart').length, count);
        secondRelease.resolve(success);
        await secondRestart;
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/hotReloadStart').length, count + 1);
        state.debugEnded(session);
    });

    it('deduplicates hooks and keeps required runtime settings when merging a reused environment', async () => {
        const state = setup();
        const separator = process.platform === 'win32' ? ';' : ':';
        const env = { DOTNET_STARTUP_HOOKS: `existing.dll${separator}agent.dll`, DOTNET_MODIFIABLE_ASSEMBLIES: 'off' };
        const merged = await state.environment(env);
        assert.equal(merged.DOTNET_STARTUP_HOOKS, env.DOTNET_STARTUP_HOOKS);
        assert.equal(merged.DOTNET_MODIFIABLE_ASSEMBLIES, 'debug');
    });

    it('releases only the terminated debug session owner', async () => {
        const state = setup();
        state.bind();
        const first = state.debugStarted('first');
        state.bindOther();
        const second = state.debugStarted('second', 'C:/other/Other.csproj');
        state.debugEnded(first);
        await new Promise(resolve => setImmediate(resolve));
        const stops = state.requests.filter(r => r.method === 'roslynSense/hotReloadStop');
        assert.equal(stops.length, 1);
        assert.equal((stops[0].params as { projectPath: string }).projectPath, project);
        assert.ok(state.module.hotReloadOwnerFor('C:/other/Other.csproj'));
        state.debugEnded(second);
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/hotReloadStop').length, 2);
    });

    it('associates the owner with the debug target process for disconnect cleanup', async () => {
        const state = setup();
        state.bind();
        const session = state.debugStarted('target');
        await state.processEvent(session, 12345);
        await new Promise(resolve => setImmediate(resolve));
        const starts = state.requests.filter(r => r.method === 'roslynSense/hotReloadStart');
        assert.equal(starts.length, 2);
        assert.equal((starts[1].params as { ownerPid: number }).ownerPid, 12345);
        assert.equal((starts[0].params as { ownerId: string }).ownerId, (starts[1].params as { ownerId: string }).ownerId);
        state.debugEnded(session);
    });

    it('appends the agent startup hook while preserving launch environment overrides', async () => {
        const state = setup();
        const env = { DOTNET_STARTUP_HOOKS: 'existing.dll', CUSTOM: 'user', OTHER: 'keep' };

        const merged = await state.environment(env);

        assert.equal(merged.DOTNET_STARTUP_HOOKS, `existing.dll${process.platform === 'win32' ? ';' : ':'}agent.dll`);
        assert.equal(merged.DOTNET_MODIFIABLE_ASSEMBLIES, 'debug');
        assert.equal(merged.CUSTOM, 'user');
        assert.equal(merged.OTHER, 'keep');
        assert.equal(env.DOTNET_STARTUP_HOOKS, 'existing.dll');
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/hotReloadStart').length, 1);
    });

    it('leaves the launch environment untouched when the agent is unavailable or its request fails', async () => {
        const state = setup();
        const env = { CUSTOM: 'user' };
        state.settings.available = false;
        assert.equal(await state.environment(env), env);
        state.failEnvironment();
        assert.equal(await state.environment(env), env);
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/hotReloadStart').length, 0);
    });

    it('serializes manual and save-triggered applies against the same baseline', async () => {
        const state = setup();
        state.bind();
        const replies = [deferred<unknown>(), deferred<unknown>()];
        const started = deferred<unknown>();
        let calls = 0;
        state.applyWith(() => { started.resolve(undefined); return replies[calls++].promise; });

        const manual = state.apply();
        await started.promise;
        const saved = state.save();
        // Let the save handler reach the serialization queue while the first request stays pending.
        for (let i = 0; i < 5; i++) { await Promise.resolve(); }
        const concurrent = calls;
        replies.forEach(reply => reply.resolve(success));
        await Promise.all([manual, saved]);

        assert.equal(concurrent, 1);
        assert.equal(calls, 2);
    });

    it('keeps newer edits pending when an older apply finishes', async () => {
        const state = setup();
        state.bind();
        state.edit();
        const reply = deferred<unknown>();
        const started = deferred<unknown>();
        state.applyWith(() => { started.resolve(undefined); return reply.promise; });
        const pending = state.apply();
        await started.promise;
        state.edit();
        reply.resolve(success);
        await pending;

        assert.equal(state.contexts.at(-1), true);
    });

    it('discards queued applies and late results when a different project is bound', async () => {
        const state = setup();
        state.bind();
        const reply = deferred<unknown>();
        const started = deferred<unknown>();
        let calls = 0;
        state.applyWith(() => {
            started.resolve(undefined);
            return calls++ === 0 ? reply.promise : Promise.resolve(success);
        });
        const first = state.apply();
        await started.promise;
        const queued = state.apply();
        await Promise.resolve();
        state.bindOther();
        state.edit();
        reply.resolve(success);
        await Promise.all([first, queued]);

        assert.equal(calls, 1);
        assert.equal(state.contexts.at(-1), true);
    });

    it('discards queued applies and late errors after the session is stopped', async () => {
        const state = setup();
        state.bind();
        state.edit();
        const reply = deferred<unknown>();
        const started = deferred<unknown>();
        let calls = 0;
        state.applyWith(() => {
            started.resolve(undefined);
            return calls++ === 0 ? reply.promise : Promise.resolve(success);
        });
        const first = state.apply();
        await started.promise;
        const queued = state.save();
        await Promise.resolve();
        await state.stop();
        reply.reject(new Error('old session stopped'));
        await Promise.all([first, queued]);

        assert.equal(calls, 1);
        assert.equal(state.contexts.at(-1), false);
        assert.equal(state.errors.length, 0);
    });

    it('uses the bound project client after the active editor switches to another root', async () => {
        const state = setup();
        state.bind();
        state.switchRoot();

        await state.apply();

        assert.equal(state.requests.find(r => r.method === 'roslynSense/hotReloadApply')!.client, 'original');
    });

    it('allows another apply after a failed request and leaves the failed edits pending', async () => {
        const state = setup();
        state.bind();
        state.edit();
        state.applyWith(async () => { throw new Error('connection closed'); });
        await state.save();
        assert.equal(state.contexts.at(-1), true);
        assert.match(state.errors[0], /connection closed/);

        state.applyWith(async () => success);
        await state.save();

        assert.equal(state.contexts.at(-1), false);
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/hotReloadApply').length, 2);
    });
});
