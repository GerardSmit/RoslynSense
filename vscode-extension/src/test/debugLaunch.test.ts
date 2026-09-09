import * as assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { deferred, loadWithMocks } from './mockModule';

class Cancellation {
    isCancellationRequested = false;
    private readonly callbacks = new Set<() => void>();
    onCancellationRequested(callback: () => void) {
        this.callbacks.add(callback);
        return { dispose: () => this.callbacks.delete(callback) };
    }
    cancel() { this.isCancellationRequested = true; this.callbacks.forEach(callback => callback()); }
}

function setup() {
    let provider!: vscode.DebugConfigurationProvider;
    let factory!: vscode.DebugAdapterDescriptorFactory;
    const commands = new Map<string, () => Promise<void>>();
    const requests: { method: string; params?: Record<string, unknown>; token?: Cancellation }[] = [];
    const errors: string[] = [];
    const statuses: string[] = [];
    let restartedHotReload = 0;
    let environmentRequests = 0;
    const warnings: string[] = [];
    const diagnostics: { uri: { fsPath: string }; entries: { message: string; range: unknown }[] }[] = [];
    const cancellation = new Cancellation();
    let cancellationDisposed = false;
    const target = {
        projectPath: 'C:/repo/App.csproj', projectName: 'App', runnable: true,
        isNetFramework: false, serverDebugAdapter: false, program: 'C:/repo/bin/App.dll',
        args: ['--default'], cwd: 'C:/repo', env: { DEFAULT: 'value', CUSTOM: 'profile' },
        url: 'http://localhost:5000;https://localhost:5001', browseUrl: 'http://localhost:5000/docs',
        launchBrowser: false, launchProfiles: [] as { name: string; commandName: string }[],
    };
    const build = { success: true, summary: 'Built', errors: [] as { file: string; line: number;
        column: number; code: string; message: string }[], warnings: [] };
    let buildReply: () => Promise<unknown> = async () => build;
    let connected = true;
    const client = { sendRequest: async (method: string, params?: Record<string, unknown>, token?: Cancellation) => {
        requests.push({ method, params, token });
        if (method === 'workspace/executeCommand') { return buildReply(); }
        if (method === 'roslynSense/targetForFile') { return target; }
        if (method === 'roslynSense/launchTargets') { return [target]; }
        if (method === 'roslynSense/debuggerPath') { return { path: 'netcoredbg', error: null }; }
        throw new Error(`Unexpected request ${method}`);
    } };
    const module = loadWithMocks<typeof import('../debugLaunch')>(require.resolve('../debugLaunch'), {
        vscode: {
            CancellationTokenSource: class {
                readonly token = new Cancellation();
                cancel() { this.token.cancel(); }
                dispose() { cancellationDisposed = true; }
            },
            ProgressLocation: { Notification: 1 },
            DiagnosticSeverity: { Error: 0, Warning: 1 },
            Range: class { constructor(readonly startLine: number, readonly startColumn: number,
                readonly endLine: number, readonly endColumn: number) {} },
            Diagnostic: class { constructor(readonly range: unknown, readonly message: string, readonly severity: number) {} },
            DebugAdapterInlineImplementation: class { constructor(readonly implementation: unknown) {} },
            DebugAdapterExecutable: class { constructor(readonly command: string, readonly args: string[]) {} },
            Uri: { file: (fsPath: string) => ({ fsPath }) },
            languages: { createDiagnosticCollection: () => ({
                clear: () => { diagnostics.length = 0; },
                set: (uri: typeof diagnostics[number]['uri'], entries: typeof diagnostics[number]['entries']) => {
                    diagnostics.push({ uri, entries });
                },
            }) },
            commands: {
                registerCommand: (name: string, callback: () => Promise<void>) => { commands.set(name, callback); },
                executeCommand: async () => {},
            },
            debug: {
                registerDebugConfigurationProvider: (_name: string, value: typeof provider) => { provider = value; },
                registerDebugAdapterDescriptorFactory: (_name: string, value: typeof factory) => { factory = value; },
            },
            workspace: { getConfiguration: () => ({ get: (_key: string, fallback: unknown) => fallback }) },
            window: {
                activeTextEditor: { document: { uri: { scheme: 'file', fsPath: 'C:/repo/Program.cs' } } },
                showQuickPick: async () => undefined,
                showInputBox: async () => 'http://localhost:5000',
                showErrorMessage: async (message: string) => { errors.push(message); },
                showInformationMessage: async () => {},
                showWarningMessage: async (message: string) => { warnings.push(message); },
                setStatusBarMessage: (message: string) => { statuses.push(message); },
                withProgress: (_options: unknown, callback: (progress: unknown, token: Cancellation) => unknown) =>
                    callback({ report() {} }, cancellation),
            },
        },
        './runAdapter': { RunAdapter: class {} },
        './hotReload': {
            hotReloadOwnerFor: () => "first-owner",
            bindHotReloadSession: () => {},
            restartDebugHotReload: async () => { restartedHotReload++; },
            withHotReloadEnvironment: async (_client: unknown, env: unknown) => { environmentRequests++; return env; },
        },
    });
    module.registerDebugLaunch({ subscriptions: [], workspaceState: { get() {}, update() {} } } as unknown as vscode.ExtensionContext,
        () => connected ? client as unknown as LanguageClient : undefined);
    return { target, build, requests, errors, statuses, diagnostics, cancellation, warnings,
        environmentRequests: () => environmentRequests,
        restartedHotReload: () => restartedHotReload,
        cancellationDisposed: () => cancellationDisposed,
        buildWith: (handler: typeof buildReply) => { buildReply = handler; },
        disconnect: () => { connected = false; },
        resolve: (config: vscode.DebugConfiguration) => provider.resolveDebugConfiguration!(undefined, config, {} as vscode.CancellationToken),
        launch: (config: vscode.DebugConfiguration) => provider.resolveDebugConfigurationWithSubstitutedVariables!(
            undefined, config, {} as vscode.CancellationToken),
        adapter: (config: vscode.DebugConfiguration) => factory.createDebugAdapterDescriptor!(
            { configuration: config } as vscode.DebugSession, undefined),
        pinUrl: () => commands.get('roslynSense.pinLaunchUrl')!(),
    };
}

describe('debug launch lifecycle', () => {
    it('explains the .NET debugger limitation instead of advertising runtime Hot Reload', async () => {
        const state = setup();
        const config = await state.launch({ type: 'roslynsense', request: 'launch', name: 'App',
            projectPath: state.target.projectPath });
        assert.ok(config);
        assert.equal(state.environmentRequests(), 0);
        assert.equal(config.roslynSenseHotReloadOwner, undefined);
        assert.match(state.warnings[0], /Edit and Continue/);
        assert.match(state.warnings[0], /Restart to rebuild/);
    });

    it('enables runtime Hot Reload for Run Without Debugging', async () => {
        const state = setup();
        assert.ok(await state.launch({ type: 'roslynsense', request: 'launch', name: 'App',
            projectPath: state.target.projectPath, noDebug: true }));
        assert.equal(state.environmentRequests(), 1);
        assert.equal(state.warnings.length, 0);
    });

    it('builds again when Restart reuses the resolved configuration, without double-building F5', async () => {
        const state = setup();
        const config = await state.launch({ type: 'roslynsense', request: 'launch', name: 'App',
            projectPath: state.target.projectPath, noDebug: true, roslynSenseHotReloadOwner: 'first-owner' });
        assert.ok(config);
        // VS Code serializes the configuration; object identity cannot identify a prepared launch.
        await state.adapter(JSON.parse(JSON.stringify(config)));
        assert.equal(state.requests.filter(r => r.method === 'workspace/executeCommand').length, 1);
        await state.adapter(JSON.parse(JSON.stringify(config)));
        assert.equal(state.requests.filter(r => r.method === 'workspace/executeCommand').length, 2);
        assert.equal(state.restartedHotReload(), 1);
        await state.adapter(config);
        assert.equal(state.requests.filter(r => r.method === 'workspace/executeCommand').length, 3);
        assert.equal(state.restartedHotReload(), 2);
    });

    it('rebuilds an ordinary .NET debug session on Restart without trying runtime Hot Reload', async () => {
        const state = setup();
        const config = await state.launch({ type: 'roslynsense', request: 'launch', name: 'App',
            projectPath: state.target.projectPath });
        assert.ok(config);
        await state.adapter(config);
        await state.adapter(config);
        assert.equal(state.requests.filter(r => r.method === 'workspace/executeCommand').length, 2);
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/debuggerPath').length, 2);
        assert.equal(state.restartedHotReload(), 0);
    });

    it('aborts Restart on build failure before creating an adapter or a new hot reload baseline', async () => {
        const state = setup();
        const config = await state.launch({ type: 'roslynsense', request: 'launch', name: 'App',
            projectPath: state.target.projectPath, noDebug: true, roslynSenseHotReloadOwner: 'first-owner' });
        assert.ok(config);
        await state.adapter(config);
        state.build.success = false;
        const debuggerRequests = state.requests.filter(r => r.method === 'roslynSense/debuggerPath').length;
        await assert.rejects(async () => state.adapter(config), /build did not succeed/);
        assert.equal(state.requests.filter(r => r.method === 'roslynSense/debuggerPath').length, debuggerRequests);
        assert.equal(state.restartedHotReload(), 0);
    });

    it('leaves attach configurations usable without building or requiring a server', async () => {
        const state = setup();
        state.disconnect();
        const config = { type: 'roslynsense', request: 'attach', name: 'Attach', processId: 123 };
        assert.equal(await state.resolve(config), config);
        assert.equal(await state.launch(config), config);
        assert.equal(state.requests.length, 0);
        assert.equal(state.errors.length, 0);
    });

    it('aborts F5 when the launch profile picker is dismissed', async () => {
        const state = setup();
        state.target.launchProfiles = [{ name: 'http', commandName: 'Project' }, { name: 'https', commandName: 'Project' }];

        assert.equal(await state.resolve({ type: 'roslynsense', request: 'launch', name: 'Launch' }), undefined);
        assert.equal(state.requests.some(r => r.method === 'workspace/executeCommand'), false);
    });

    it('does not change launch settings when profile selection for pinning a URL is cancelled', async () => {
        const state = setup();
        state.target.launchProfiles = [{ name: 'http', commandName: 'Project' }, { name: 'https', commandName: 'Project' }];

        await state.pinUrl();

        assert.equal(state.requests.some(r => r.method === 'workspace/executeCommand'), false);
    });

    it('propagates build cancellation and aborts before launch-target lookup', async () => {
        const state = setup();
        const reply = deferred<unknown>();
        state.buildWith(() => reply.promise);
        const pending = state.launch({ type: 'roslynsense', request: 'launch', name: 'App', projectPath: state.target.projectPath });
        state.cancellation.cancel();
        reply.reject(new Error('request cancelled'));

        assert.equal(await pending, undefined);
        assert.equal(state.requests[0].token!.isCancellationRequested, true);
        assert.equal(state.cancellationDisposed(), true);
        assert.equal(state.requests.length, 1);
        assert.equal(state.errors.length, 0);
        assert.match(state.statuses[0], /cancelled/);
    });

    it('reports a broken build connection as a failure instead of user cancellation', async () => {
        const state = setup();
        state.buildWith(async () => { throw new Error('connection closed'); });

        assert.equal(await state.launch({ type: 'roslynsense', request: 'launch', name: 'App',
            projectPath: state.target.projectPath }), undefined);

        assert.equal(state.errors.length, 1);
        assert.match(state.errors[0], /connection closed/);
        assert.equal(state.statuses.some(s => s.includes('cancelled')), false);
        assert.equal(state.cancellationDisposed(), true);
    });

    it('preserves explicit launch overrides while filling defaults from the built project', async () => {
        const state = setup();
        const config = { type: 'roslynsense', request: 'launch', name: 'App', projectPath: 'c:\\repo\\app.csproj',
            args: ['--custom'], env: { CUSTOM: 'user' }, hotReload: false };
        const launch = await state.launch(config);

        assert.ok(launch);
        assert.equal(launch.program, state.target.program);
        assert.equal(launch.cwd, state.target.cwd);
        assert.equal(launch.args, config.args);
        assert.equal(launch.env.CUSTOM, 'user');
        assert.equal(launch.env.DEFAULT, 'value');
        assert.equal(launch.appUrl, 'http://localhost:5000');
        assert.equal(launch.serverReadyAction, undefined);
    });

    it('publishes build errors at their source location and never launches a failed build', async () => {
        const state = setup();
        state.build.success = false;
        state.build.errors = [{ file: 'C:/repo/Program.cs', line: 4, column: 2, code: 'CS1002', message: '; expected' }];

        assert.equal(await state.launch({ type: 'roslynsense', request: 'launch', name: 'App',
            projectPath: state.target.projectPath }), undefined);

        assert.equal(state.diagnostics.length, 1);
        assert.equal(state.diagnostics[0].uri.fsPath, 'C:/repo/Program.cs');
        const range = state.diagnostics[0].entries[0].range as { startLine: number; startColumn: number };
        assert.equal(range.startLine, 3);
        assert.equal(range.startColumn, 1);
        assert.match(state.errors[0], /; expected/);
        assert.equal(state.requests.length, 1);
    });

    it('selects the Framework adapter without asking for a CoreCLR debugger', async () => {
        const state = setup();
        const adapter = await state.adapter({ type: 'roslynsense', request: 'attach', name: 'Framework', isNetFramework: true });
        assert.equal((adapter as vscode.DebugAdapterExecutable).command, 'roslyn-sense');
        assert.equal((adapter as vscode.DebugAdapterExecutable).args.join(' '), '--dap');
        assert.equal(state.requests.length, 0);
    });
});
