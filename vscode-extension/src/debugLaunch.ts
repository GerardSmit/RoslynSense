import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { withHotReloadEnvironment } from './hotReload';

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

interface LaunchProfileDescriptor {
    name: string;
    commandName: string;
    applicationUrl: string | null;
    commandLineArgs: string | null;
    launchBrowser: boolean;
    launchUrl: string | null;
    environmentVariables: Record<string, string>;
}

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
    launchProfiles: LaunchProfileDescriptor[];
    launchProfile: string | null;
    browseUrl: string | null;
    launchBrowser: boolean | null;
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

    // Turns the address a project happens to run on into one it states, so it survives a restart
    // and everyone on the team gets the same one.
    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.pinLaunchUrl', async () => {
            const client = getClient();
            if (!client) {
                void vscode.window.showErrorMessage('RoslynSense is not running.');
                return;
            }

            const target = await pickTarget(context, client);
            if (!target) {
                return;
            }

            const url = await vscode.window.showInputBox({
                title: `Launch URL for ${target.projectName}`,
                value: target.url ?? 'http://localhost:5000',
                prompt: 'Written to launchSettings.json as the profile\'s applicationUrl.',
                validateInput: (value) =>
                    /^https?:\/\/\S+$/.test(value.split(';')[0] ?? '')
                        ? undefined
                        : 'Enter an absolute http(s) URL.',
            });
            if (!url) {
                return;
            }

            const profile =
                target.launchProfiles.length > 0 ? await pickProfile(target) : target.projectName;

            const result = await client.sendRequest<string>('workspace/executeCommand', {
                command: 'roslynSense.setLaunchUrl',
                arguments: profile
                    ? [target.projectPath, url, profile]
                    : [target.projectPath, url],
            });
            void vscode.window.showInformationMessage(`RoslynSense: ${result}`);
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
                // One configuration per launch profile, the way Rider imports them: a project with
                // profiles has already said how it wants to be run, more than once.
                return targets.flatMap((target) =>
                    target.launchProfiles.length > 0
                        ? target.launchProfiles.map((profile) => ({
                              type: DEBUG_TYPE,
                              request: 'launch',
                              name: `C#: ${target.projectName} (${profile.name})`,
                              projectPath: target.projectPath,
                              launchProfile: profile.name,
                              stopAtEntry: false,
                          }))
                        : [
                              {
                                  type: DEBUG_TYPE,
                                  request: 'launch',
                                  name: `C#: ${target.projectName}`,
                                  projectPath: target.projectPath,
                                  stopAtEntry: false,
                              },
                          ]
                );
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
                    // The file in front of the user answers "which project" nearly every time,
                    // so asking is the fallback rather than the first move.
                    const active = await targetForActiveFile(getClient(), config.configuration);
                    if (active?.runnable) {
                        config.projectPath = active.projectPath;
                        config.name = `C#: ${active.projectName}`;
                        config.launchProfile = config.launchProfile ?? (await pickProfile(active));
                        return config;
                    }

                    const target = await pickTarget(context, getClient());
                    if (!target) {
                        // Undefined aborts silently; the picker was already dismissed by the user.
                        return undefined;
                    }
                    config.projectPath = target.projectPath;
                    config.launchProfile = config.launchProfile ?? (await pickProfile(target));
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
                const projectName = basename(config.projectPath);

                // A notification rather than the status bar, and cancellable: a build with no
                // visible sign of life reads as a hung editor, and the way out of a long one
                // should not be killing the window.
                const cancellation = new vscode.CancellationTokenSource();
                let build: BuildResult;
                try {
                    build = await vscode.window.withProgress(
                        {
                            location: vscode.ProgressLocation.Notification,
                            title: `Building ${projectName}`,
                            cancellable: true,
                        },
                        (progress, token) => {
                            token.onCancellationRequested(() => cancellation.cancel());
                            progress.report({ message: configuration });
                            return client.sendRequest<BuildResult>(
                                'workspace/executeCommand',
                                {
                                    // The last argument turns off the server's own progress: this
                                    // notification is already showing the same build.
                                    command: 'roslynSense.build',
                                    arguments: [config.projectPath, configuration, 'build', false],
                                },
                                cancellation.token
                            );
                        }
                    );
                } catch {
                    // The only way this rejects is the cancellation above; the server kills the
                    // build with it, so there is nothing to clean up here.
                    void vscode.window.setStatusBarMessage(
                        `RoslynSense: build of ${projectName} cancelled.`,
                        5000
                    );
                    return undefined;
                } finally {
                    cancellation.dispose();
                }

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

                const target = (
                    await fetchTargets(client, configuration, config.launchProfile)
                ).find((t) => samePath(t.projectPath, config.projectPath));
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

                // Not used by the adapter: it travels on the configuration so that, once the
                // process reports its pid, the launch can be registered with its address — which
                // is what makes "Open URL" work for the user's own launches too.
                config.appUrl = target.url?.split(';')[0] ?? null;

                config.program = config.program ?? target.program;
                config.args = config.args ?? target.args;
                config.cwd = config.cwd ?? target.cwd;
                config.env = { ...target.env, ...(config.env ?? {}) };

                // Hot reload has to be decided before the process starts, and it costs nothing
                // when unused, so it is on unless the configuration turns it off. Not for .NET
                // Framework: there the edit goes through the debugger, not a startup hook.
                if (config.hotReload !== false && !target.isNetFramework) {
                    config.env = await withHotReloadEnvironment(client, config.env, target.projectPath);
                }

                // Web apps: open the browser once Kestrel announces its address, matching what
                // the standard C# extension does. A profile that says launchBrowser: false is
                // asking for the opposite, and gets it.
                // Between the build finishing and the debugger's own UI appearing there is a gap
                // with nothing in it, which is the part that reads as "nothing is happening".
                void vscode.window.setStatusBarMessage(
                    `Starting ${projectName}${target.url ? ` on ${target.url.split(';')[0]}` : ''}…`,
                    8000
                );

                if (target.url && !config.serverReadyAction && target.launchBrowser !== false) {
                    config.serverReadyAction = {
                        pattern: 'Now listening on:\\s+(https?://\\S+)',
                        // The profile's launchUrl is a path under whichever address the app
                        // actually announced, so it is appended rather than substituted.
                        uriFormat: `%s${launchPath(target)}`,
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

/**
 * The launch target for the project owning the active editor's file.
 *
 * Resolved by the server rather than by comparing paths here: it knows about linked files, and
 * about projects the workspace has not loaded, neither of which a directory prefix gets right.
 */
async function targetForActiveFile(
    client: LanguageClient | undefined,
    configuration?: string
): Promise<LaunchTarget | undefined> {
    const document = vscode.window.activeTextEditor?.document;
    if (!client || document?.uri.scheme !== 'file') {
        return undefined;
    }

    try {
        const target = await client.sendRequest<LaunchTarget | null>('roslynSense/targetForFile', {
            filePath: document.uri.fsPath,
            configuration: configuration ?? null,
        });
        return target ?? undefined;
    } catch {
        return undefined;
    }
}

async function fetchTargets(
    client: LanguageClient | undefined,
    configuration?: string,
    launchProfile?: string
): Promise<LaunchTarget[]> {
    if (!client) {
        return [];
    }
    try {
        return await client.sendRequest<LaunchTarget[]>('roslynSense/launchTargets', {
            configuration: configuration ?? null,
            launchProfile: launchProfile ?? null,
        });
    } catch {
        return [];
    }
}

/**
 * The profile to launch with, when the project offers more than one and the configuration did
 * not name one. A single profile is not worth a question.
 */
async function pickProfile(target: LaunchTarget): Promise<string | undefined> {
    if (target.launchProfiles.length < 2) {
        return target.launchProfiles[0]?.name;
    }

    const picked = await vscode.window.showQuickPick(
        target.launchProfiles.map((profile) => ({
            label: profile.name,
            description: profile.commandName,
            detail: profile.applicationUrl ?? profile.commandLineArgs ?? undefined,
            profile,
        })),
        { title: `Launch profile for ${target.projectName}` }
    );
    return picked?.profile.name;
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

function basename(filePath: string | undefined): string {
    if (!filePath) {
        return 'project';
    }
    const name = filePath.replace(/\\/g, '/').split('/').pop() ?? filePath;
    return name.replace(/\.[^.]+$/, '');
}

/**
 * The path part of the profile's launchUrl, as a suffix to append to the address the running app
 * reports. Empty when the profile browses the root, or names an absolute URL of its own.
 */
function launchPath(target: LaunchTarget): string {
    const base = target.url?.split(';')[0]?.replace(/\/$/, '');
    if (!base || !target.browseUrl || !target.browseUrl.startsWith(base)) {
        return '';
    }
    return target.browseUrl.slice(base.length);
}

function samePath(a: string | undefined | null, b: string | undefined | null): boolean {
    if (!a || !b) {
        return false;
    }
    return a.replace(/\\/g, '/').toLowerCase() === b.replace(/\\/g, '/').toLowerCase();
}
