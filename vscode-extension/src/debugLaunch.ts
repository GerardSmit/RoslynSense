import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * Real debugging of the user's own app, without ms-dotnettools.csharp.
 *
 * The adapter is netcoredbg in its DAP mode (`--interpreter=vscode`), so VS Code talks to a
 * genuine debugger: watch, locals trees, conditional breakpoints, exception filters and
 * setVariable all come from netcoredbg rather than from adapter code we would have to write.
 *
 * This is distinct from the `roslynsense-ai` debug type, which only mirrors a session an AI
 * chat owns.
 */

export const DEBUG_TYPE = 'roslynsense';

interface LaunchTarget {
    projectPath: string;
    projectName: string;
    kind: string;
    targetFramework: string | null;
    isNetFramework: boolean;
    isTestProject: boolean;
    runnable: boolean;
    program: string | null;
    args: string[];
    cwd: string | null;
    env: Record<string, string>;
    url: string | null;
    error: string | null;
}

interface DebuggerPathResult {
    path: string | null;
    provisioned: boolean;
    error: string | null;
}

interface BuildMessage {
    file: string | null;
    line: number;
    column: number;
    code: string | null;
    message: string;
}

interface BuildResult {
    success: boolean;
    summary: string;
    errors: BuildMessage[];
    warnings: BuildMessage[];
}

interface AttachTarget {
    pid: number;
    name: string;
    projectName: string | null;
    url: string | null;
}

const LAST_TARGET_KEY = 'roslynSense.lastLaunchTarget';

export function registerDebugLaunch(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const buildDiagnostics = vscode.languages.createDiagnosticCollection('roslynSense.build');
    context.subscriptions.push(buildDiagnostics);

    // Referenced from attach configurations as "${command:roslynSense.pickProcess}".
    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.pickProcess', async () => {
            const client = getClient();
            if (!client) {
                return undefined;
            }
            const targets = await client.sendRequest<AttachTarget[]>('roslynSense/attachTargets');
            const picked = await vscode.window.showQuickPick(
                targets.map((target) => ({
                    label: target.projectName ?? target.name,
                    description: `pid ${target.pid}`,
                    detail: target.url ?? (target.projectName ? target.name : undefined),
                    pid: target.pid,
                })),
                { title: 'Select a .NET process to attach to', matchOnDescription: true }
            );
            return picked ? String(picked.pid) : undefined;
        })
    );

    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory(DEBUG_TYPE, {
            async createDebugAdapterDescriptor(session: vscode.DebugSession) {
                // .NET Framework needs ICorDebug; netcoredbg only speaks to CoreCLR. The server
                // ships that adapter itself, in --dap mode.
                if (session.configuration.isNetFramework === true ||
                    (await isFrameworkTarget(getClient(), session.configuration.projectPath))) {
                    return new vscode.DebugAdapterExecutable(
                        vscode.workspace.getConfiguration('roslynSense')
                            .get<string>('serverPath', 'roslyn-sense'),
                        ['--dap']);
                }

                const configured = vscode.workspace
                    .getConfiguration('roslynSense')
                    .get<string>('debuggerPath');
                if (configured) {
                    return new vscode.DebugAdapterExecutable(configured, ['--interpreter=vscode']);
                }

                const client = getClient();
                if (!client) {
                    throw new Error('RoslynSense is still starting. Try again in a moment.');
                }

                const result = await client.sendRequest<DebuggerPathResult>(
                    'roslynSense/debuggerPath'
                );
                if (!result.path) {
                    throw new Error(result.error ?? 'Could not locate the .NET debugger.');
                }
                return new vscode.DebugAdapterExecutable(result.path, ['--interpreter=vscode']);
            },
        })
    );

    context.subscriptions.push(
        vscode.debug.registerDebugConfigurationProvider(DEBUG_TYPE, {
            async provideDebugConfigurations() {
                const targets = (await fetchTargets(getClient())).filter((t) => t.runnable);
                if (targets.length === 0) {
                    return [
                        {
                            type: DEBUG_TYPE,
                            request: 'launch',
                            name: 'C#: Launch',
                            projectPath: '${workspaceFolder}/YourProject.csproj',
                        },
                    ];
                }
                return targets.map((target) => ({
                    type: DEBUG_TYPE,
                    request: 'launch',
                    name: `C#: ${target.projectName}`,
                    projectPath: target.projectPath,
                    stopAtEntry: false,
                }));
            },

            // F5 with no launch.json arrives here with an essentially empty config.
            async resolveDebugConfiguration(folder, config) {
                if (config.request === 'attach') {
                    return config;
                }

                if (!config.type) {
                    config.type = DEBUG_TYPE;
                    config.request = 'launch';
                    config.name = 'C#: Launch';
                }

                if (!config.projectPath) {
                    const target = await pickTarget(context, getClient());
                    if (!target) {
                        // Undefined aborts silently; the picker was already dismissed by the user.
                        return undefined;
                    }
                    config.projectPath = target.projectPath;
                }
                return config;
            },

            // Runs after variables like ${workspaceFolder} are substituted, which is the right
            // moment to build and fill in the concrete program path.
            async resolveDebugConfigurationWithSubstitutedVariables(folder, config) {
                if (config.request === 'attach') {
                    return config;
                }

                const client = getClient();
                if (!client) {
                    void vscode.window.showErrorMessage('RoslynSense is not running.');
                    return undefined;
                }

                const configuration = config.configuration ?? 'Debug';
                const build = await vscode.window.withProgress(
                    { location: vscode.ProgressLocation.Window, title: 'Building…' },
                    () =>
                        client.sendRequest<BuildResult>('workspace/executeCommand', {
                            command: 'roslynSense.build',
                            arguments: [config.projectPath, configuration],
                        })
                );

                publishBuildDiagnostics(buildDiagnostics, build);
                if (!build.success) {
                    const firstError = build.errors[0];
                    void vscode.window
                        .showErrorMessage(
                            firstError
                                ? `Build failed: ${firstError.message}`
                                : build.summary,
                            'Show Problems'
                        )
                        .then((choice) => {
                            if (choice === 'Show Problems') {
                                void vscode.commands.executeCommand('workbench.actions.view.problems');
                            }
                        });
                    return undefined;
                }

                const target = (await fetchTargets(client, configuration)).find(
                    (t) => samePath(t.projectPath, config.projectPath)
                );
                if (!target) {
                    void vscode.window.showErrorMessage(
                        `'${config.projectPath}' is not a project in the loaded solution.`
                    );
                    return undefined;
                }
                if (!target.runnable || !target.program) {
                    void vscode.window.showErrorMessage(
                        target.error ?? `${target.projectName} cannot be launched.`
                    );
                    return undefined;
                }

                config.program = config.program ?? target.program;
                config.args = config.args ?? target.args;
                config.cwd = config.cwd ?? target.cwd;
                config.env = { ...target.env, ...(config.env ?? {}) };

                // Web apps: open the browser once Kestrel announces its address, matching what
                // the standard C# extension does.
                if (target.url && !config.serverReadyAction) {
                    config.serverReadyAction = {
                        pattern: 'Now listening on:\\s+(https?://\\S+)',
                        uriFormat: '%s',
                        action: 'openExternally',
                    };
                }
                return config;
            },
        })
    );
}

/** Whether a project targets .NET Framework, which decides the adapter. */
async function isFrameworkTarget(
    client: LanguageClient | undefined,
    projectPath: string | undefined
): Promise<boolean> {
    if (!client || !projectPath) {
        return false;
    }
    const targets = await fetchTargets(client);
    return targets.some((t) => samePath(t.projectPath, projectPath) && t.isNetFramework);
}

async function fetchTargets(
    client: LanguageClient | undefined,
    configuration?: string
): Promise<LaunchTarget[]> {
    if (!client) {
        return [];
    }
    try {
        return await client.sendRequest<LaunchTarget[]>('roslynSense/launchTargets', {
            configuration: configuration ?? null,
        });
    } catch {
        return [];
    }
}

async function pickTarget(
    context: vscode.ExtensionContext,
    client: LanguageClient | undefined
): Promise<LaunchTarget | undefined> {
    const targets = await fetchTargets(client);
    const runnable = targets.filter((t) => t.runnable);

    if (runnable.length === 0) {
        const blocked = targets.find((t) => t.error);
        void vscode.window.showErrorMessage(
            blocked?.error ?? 'No launchable project found in this solution.'
        );
        return undefined;
    }
    if (runnable.length === 1) {
        return runnable[0];
    }

    // Offer last choice first: repeated F5 on the same project should not re-ask every time.
    const last = context.workspaceState.get<string>(LAST_TARGET_KEY);
    const ordered = [...runnable].sort((a, b) =>
        samePath(a.projectPath, last) ? -1 : samePath(b.projectPath, last) ? 1 : 0
    );

    const picked = await vscode.window.showQuickPick(
        ordered.map((target) => ({
            label: target.projectName,
            description: target.kind + (target.targetFramework ? ` · ${target.targetFramework}` : ''),
            detail: target.projectPath,
            target,
        })),
        { title: 'Select a project to debug', matchOnDetail: true }
    );
    if (!picked) {
        return undefined;
    }
    void context.workspaceState.update(LAST_TARGET_KEY, picked.target.projectPath);
    return picked.target;
}

function publishBuildDiagnostics(
    collection: vscode.DiagnosticCollection,
    build: BuildResult
): void {
    collection.clear();

    const byFile = new Map<string, vscode.Diagnostic[]>();
    const add = (message: BuildMessage, severity: vscode.DiagnosticSeverity) => {
        if (!message.file) {
            return;
        }
        const line = Math.max(0, message.line - 1);
        const column = Math.max(0, message.column - 1);
        const diagnostic = new vscode.Diagnostic(
            new vscode.Range(line, column, line, column + 1),
            message.message,
            severity
        );
        diagnostic.source = 'build';
        diagnostic.code = message.code ?? undefined;
        const existing = byFile.get(message.file);
        if (existing) {
            existing.push(diagnostic);
        } else {
            byFile.set(message.file, [diagnostic]);
        }
    };

    // Build errors reach files that are not open, which is exactly where the language server's
    // open-file diagnostics cannot help.
    build.errors.forEach((m) => add(m, vscode.DiagnosticSeverity.Error));
    build.warnings.forEach((m) => add(m, vscode.DiagnosticSeverity.Warning));

    for (const [file, diagnostics] of byFile) {
        collection.set(vscode.Uri.file(file), diagnostics);
    }
}

function samePath(a: string | undefined | null, b: string | undefined | null): boolean {
    if (!a || !b) {
        return false;
    }
    return a.replace(/\\/g, '/').toLowerCase() === b.replace(/\\/g, '/').toLowerCase();
}
