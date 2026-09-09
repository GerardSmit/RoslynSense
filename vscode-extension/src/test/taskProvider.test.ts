import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { deferred, loadWithMocks } from './mockModule';

function setup(framework = false) {
    const commands = new Map<string, () => Promise<void>>();
    const requests: { method: string; params?: Record<string, unknown> }[] = [];
    const errors: string[] = [];
    const subscriptions: vscode.Disposable[] = [];
    let provider!: vscode.DebugConfigurationProvider;
    let started: (session: vscode.DebugSession) => void = () => {};
    let terminated: (session: vscode.DebugSession) => void = () => {};
    let trackerFactory!: vscode.DebugAdapterTrackerFactory;
    let session: vscode.DebugSession | undefined;
    let configuration: vscode.DebugConfiguration | undefined;
    let startCalls = 0;
    const disposable = () => ({ dispose() {} });
    const build = { success: true, summary: 'Built', errors: [], warnings: [] };
    let buildReply: () => Promise<unknown> = async () => build;
    const target = {
        projectPath: 'C:/repo/App.csproj', projectName: 'App', runnable: true,
        isNetFramework: framework, serverDebugAdapter: framework,
        program: framework ? 'C:/repo/bin/App.exe' : 'C:/repo/bin/App.dll',
        args: ['--profile-argument'], cwd: 'C:/repo', env: { PROFILE: 'kept' },
        url: null, launchBrowser: false, launchProfiles: [],
    };
    let targets = [target];
    const success = { ok: true, summary: 'Ready', diagnostics: [], appliedTo: [], errors: [] };
    const client = { sendRequest: async (method: string, params?: Record<string, unknown>) => {
        requests.push({ method, params });
        if (method === 'roslynSense/launchTargets') { return targets; }
        if (method === 'workspace/executeCommand') { return buildReply(); }
        if (method === 'roslynSense/hotReloadEnvironment') {
            return { available: true, variables: {
                DOTNET_STARTUP_HOOKS: 'agent.dll', DOTNET_MODIFIABLE_ASSEMBLIES: 'debug',
            }, message: '' };
        }
        if (method === 'roslynSense/hotReloadStart' || method === 'roslynSense/hotReloadStop') { return success; }
        throw new Error(`Unexpected request ${method}`);
    } };
    const vscodeMock = {
        CancellationTokenSource: class {
            readonly token = { isCancellationRequested: false, onCancellationRequested: disposable };
            cancel() { this.token.isCancellationRequested = true; }
            dispose() {}
        },
        ProgressLocation: { Notification: 1 },
        Uri: { file: (fsPath: string) => ({ fsPath }) },
        languages: { createDiagnosticCollection: () => ({ clear() {}, set() {}, dispose() {} }) },
        commands: {
            registerCommand: (name: string, callback: () => Promise<void>) => {
                commands.set(name, callback); return disposable();
            },
            executeCommand: async () => {},
        },
        workspace: {
            onDidChangeTextDocument: disposable,
            onDidSaveTextDocument: disposable,
            getConfiguration: () => ({ get: (_key: string, fallback: unknown) => fallback }),
        },
        tasks: { onDidEndTaskProcess: disposable, registerTaskProvider: disposable },
        debug: {
            registerDebugConfigurationProvider: (_name: string, value: typeof provider) => {
                provider = value; return disposable();
            },
            registerDebugAdapterDescriptorFactory: disposable,
            onDidStartDebugSession: (callback: typeof started) => { started = callback; return disposable(); },
            onDidTerminateDebugSession: (callback: typeof terminated) => { terminated = callback; return disposable(); },
            registerDebugAdapterTrackerFactory: (_name: string, factory: typeof trackerFactory) => {
                trackerFactory = factory; return disposable();
            },
            startDebugging: async (_folder: unknown, config: vscode.DebugConfiguration, options: vscode.DebugSessionOptions) => {
                startCalls++;
                // VS Code supplies noDebug to the configuration providers before launching.
                config.noDebug = options.noDebug;
                const resolved = await provider.resolveDebugConfiguration!(undefined, config, {} as vscode.CancellationToken);
                if (!resolved) { return false; }
                configuration = await provider.resolveDebugConfigurationWithSubstitutedVariables!(
                    undefined, resolved, {} as vscode.CancellationToken) ?? undefined;
                if (!configuration) { return false; }
                session = { id: 'hot-reload-command', configuration } as vscode.DebugSession;
                started(session);
                const tracker = await trackerFactory.createDebugAdapterTracker!(session);
                tracker?.onDidSendMessage?.({ type: 'event', event: 'process', body: { systemProcessId: 12345 } });
                return true;
            },
        },
        window: {
            showQuickPick: async () => undefined,
            showWarningMessage: async (message: string) => { errors.push(message); },
            showErrorMessage: async (message: string) => { errors.push(message); },
            showInformationMessage: async () => {},
            setStatusBarMessage: () => {},
            withProgress: (_options: unknown, callback: (progress: unknown, token: unknown) => unknown) =>
                callback({ report() {} }, { onCancellationRequested: disposable }),
        },
    };
    const context = { subscriptions, workspaceState: { get() {}, update() {} } } as unknown as vscode.ExtensionContext;
    const getClient = () => client as unknown as LanguageClient;
    const hotReload = loadWithMocks<typeof import('../hotReload')>(require.resolve('../hotReload'), { vscode: vscodeMock });
    hotReload.registerHotReload(context, getClient);
    const debugLaunch = loadWithMocks<typeof import('../debugLaunch')>(require.resolve('../debugLaunch'), {
        vscode: vscodeMock, './hotReload': hotReload, './runAdapter': { RunAdapter: class {} },
    });
    debugLaunch.registerDebugLaunch(context, getClient);
    const tasks = loadWithMocks<typeof import('../taskProvider')>(require.resolve('../taskProvider'), { vscode: vscodeMock });
    tasks.registerTaskProvider(context, getClient);
    return {
        requests, errors, build, target,
        run: () => commands.get('roslynSense.runWithHotReload')!(),
        configuration: () => configuration,
        startCalls: () => startCalls,
        owner: () => hotReload.hotReloadOwnerFor(target.projectPath),
        buildWith: (handler: typeof buildReply) => { buildReply = handler; },
        requirePicker: () => { targets = [target, { ...target, projectPath: 'C:/repo/Other.csproj' }]; },
        end: async () => {
            assert.ok(session);
            terminated(session);
            await new Promise(resolve => setImmediate(resolve));
        },
        dispose: async () => {
            for (const subscription of subscriptions) { subscription?.dispose(); }
            await new Promise(resolve => setImmediate(resolve));
        },
    };
}

describe('Run with Hot Reload command', () => {
    for (const framework of [false, true]) {
        it(`builds before the ${framework ? 'Framework debugger' : 'CoreCLR runtime'} baseline and releases its owner on stop`, async (t) => {
            const state = setup(framework);
            t.after(state.dispose);
            const build = deferred<unknown>();
            state.buildWith(() => build.promise);
            const running = state.run();
            await new Promise(resolve => setImmediate(resolve));
            assert.equal(state.requests.filter(request => request.method === 'workspace/executeCommand').length, 1);
            assert.equal(state.requests.filter(request => request.method === 'roslynSense/hotReloadStart').length, 0);
            build.resolve(state.build);
            await running;
            await new Promise(resolve => setImmediate(resolve));

            const config = state.configuration();
            assert.ok(config);
            assert.equal(config.noDebug, !framework);
            assert.equal(config.hotReload, true);
            assert.equal(config.console, framework ? undefined : 'integratedTerminal');
            assert.equal(config.program, state.target.program);
            assert.equal(config.args[0], '--profile-argument');
            assert.equal(config.env.PROFILE, 'kept');
            assert.equal(config.env.DOTNET_MODIFIABLE_ASSEMBLIES, framework ? undefined : 'debug');
            assert.equal(config.serverDebugAdapter, framework);
            assert.ok(config.roslynSenseHotReloadOwner);
            assert.equal(state.requests.filter(request => request.method === 'roslynSense/hotReloadEnvironment').length, framework ? 0 : 1);
            const starts = state.requests.filter(request => request.method === 'roslynSense/hotReloadStart');
            assert.equal(starts.length, 2);
            assert.equal(starts[1].params?.ownerPid, 12345);
            assert.equal(starts[1].params?.ownerId, config.roslynSenseHotReloadOwner);
            assert.equal(state.errors.length, 0);

            await state.end();
            const stops = state.requests.filter(request => request.method === 'roslynSense/hotReloadStop');
            assert.equal(stops.length, 1);
            assert.equal(stops[0].params?.ownerId, config.roslynSenseHotReloadOwner);
            assert.equal(state.owner(), undefined);
        });
    }

    it('aborts a failed build without opening a baseline or starting a target', async (t) => {
        const state = setup();
        t.after(state.dispose);
        state.build.success = false;
        state.build.summary = 'Compilation failed';
        await state.run();
        assert.equal(state.configuration(), undefined);
        assert.equal(state.owner(), undefined);
        assert.equal(state.requests.filter(request => request.method.startsWith('roslynSense/hotReload')).length, 0);
        assert.ok(state.errors.some(message => message.includes('Compilation failed')));
    });

    it('leaves the application untouched when the project picker is dismissed', async (t) => {
        const state = setup();
        t.after(state.dispose);
        state.requirePicker();
        await state.run();
        assert.equal(state.startCalls(), 0);
        assert.equal(state.requests.length, 1);
        assert.equal(state.owner(), undefined);
    });
});
