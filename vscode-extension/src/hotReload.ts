import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';

/// One assembly's worth of change, as the server reports it back.
interface HotReloadResult {
    ok: boolean;
    summary: string;
    diagnostics: {
        id: string;
        message: string;
        severity: string;
        filePath: string;
        line: number;
    }[];
    appliedTo: string[];
    errors: string[];
}

interface HotReloadEnvironment {
    available: boolean;
    variables: Record<string, string>;
    message: string;
}

interface LaunchTarget {
    projectPath: string;
    projectName: string;
    runnable: boolean;
    isNetFramework: boolean;
}

/// The project the last apply used, so repeated saves do not re-ask.
let boundProject: string | undefined;
let boundClient: LanguageClient | undefined;
let boundOwner: string | undefined;
interface ReloadOwner { client: LanguageClient; projectPath: string; ownerId: string; start: Promise<unknown>; timer?: ReturnType<typeof setTimeout> }
const reloadOwners = new Map<string, ReloadOwner>();
const debugOwners = new Map<string, string>();

const releasingOwners = new Map<string, Promise<void>>();
function releaseOwner(ownerId: string): Promise<void> {
    const pending = releasingOwners.get(ownerId);
    if (pending) { return pending; }
    const release = releaseOwnerCore(ownerId).finally(() => releasingOwners.delete(ownerId));
    releasingOwners.set(ownerId, release);
    return release;
}

async function releaseOwnerCore(ownerId: string): Promise<void> {
    const owner = reloadOwners.get(ownerId);
    if (!owner) { return; }
    reloadOwners.delete(ownerId);
    if (owner.timer) { clearTimeout(owner.timer); }
    if (boundOwner === ownerId) {
        boundOwner = undefined; boundProject = undefined; boundClient = undefined;
        sessionVersion++; setPending(false);
    }
    // A release overtaking initialization would leave a newly published baseline orphaned.
    await owner.start.catch(() => undefined);
    await owner.client.sendRequest('roslynSense/hotReloadStop', { projectPath: owner.projectPath, ownerId })
        .catch(() => undefined);
}


/// Serialises applies: two overlapping ones would each diff against a baseline the other is
/// about to move.
let inFlight: Promise<void> = Promise.resolve();

/// Drives the debug toolbar button: true once an edit has landed that the running process has
/// not seen yet. Mirrored into a context key because `when` clauses are the only way a menu
/// contribution can read extension state.
let pending = false;
let editVersion = 0;
let sessionVersion = 0;

function setPending(value: boolean): void {
    if (pending === value) {
        return;
    }
    pending = value;
    void vscode.commands.executeCommand('setContext', 'roslynSense.hotReload.pending', value);
}

export function registerHotReload(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const diagnostics = vscode.languages.createDiagnosticCollection('roslynSense.hotReload');
    context.subscriptions.push(diagnostics);
    const ownerForDebug = (session: vscode.DebugSession): ReloadOwner | undefined => {
        const explicit = session.configuration.roslynSenseHotReloadOwner as string | undefined;
        if (explicit) { return reloadOwners.get(explicit); }
        const projectPath = session.configuration.projectPath as string | undefined;
        return [...reloadOwners.values()].reverse().find(o => projectPath &&
            o.projectPath.toLowerCase() === projectPath.toLowerCase() && ![...debugOwners.values()].includes(o.ownerId));
    };
    if (vscode.debug.onDidStartDebugSession) {
        context.subscriptions.push(vscode.debug.onDidStartDebugSession(session => {
            const owner = reloadOwners.get(debugOwners.get(session.id) ?? '') ?? ownerForDebug(session);
            if (!owner) { return; }
            if (owner.timer) { clearTimeout(owner.timer); owner.timer = undefined; }
            debugOwners.set(session.id, owner.ownerId);
        }));
    }
    if (vscode.debug.registerDebugAdapterTrackerFactory) {
        context.subscriptions.push(vscode.debug.registerDebugAdapterTrackerFactory('*', {
            createDebugAdapterTracker(session) {
                return { onDidSendMessage(message) {
                    if (message.type !== 'event' || message.event !== 'process' || !message.body?.systemProcessId) { return; }
                    const owner = reloadOwners.get(debugOwners.get(session.id) ?? '') ?? ownerForDebug(session);
                    if (!owner) { return; }
                    debugOwners.set(session.id, owner.ownerId);
                    const ownerPid = message.body.systemProcessId as number;
                    owner.start = owner.start.then(() => owner.client.sendRequest('roslynSense/hotReloadStart', {
                        projectPath: owner.projectPath, ownerId: owner.ownerId, ownerPid,
                    }));
                    void owner.start.catch(() => undefined);
                } };
            },
        }));
    }
    context.subscriptions.push({ dispose: () => {
        for (const owner of reloadOwners.keys()) { void releaseOwner(owner); }
    } });

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.applyHotReload', async () => {
            const client = boundClient ?? getClient();
            if (!client) {
                void vscode.window.showErrorMessage('RoslynSense is not running.');
                return;
            }
            const project = await resolveProject(client, true);
            if (project) {
                await queueApply(client, project, diagnostics, true);
            }
        }),

        vscode.commands.registerCommand('roslynSense.stopHotReload', async () => {
            const client = boundClient ?? getClient();
            if (!client || !boundProject) {
                return;
            }
            const projectPath = boundProject;
            const ownerId = boundOwner;
            boundOwner = undefined;
            const version = ++sessionVersion;
            diagnostics.clear();
            boundProject = undefined;
            boundClient = undefined;
            setPending(false);
            if (ownerId) { await releaseOwner(ownerId); }
            else { await client.sendRequest<HotReloadResult>('roslynSense/hotReloadStop', { projectPath }); }
            if (version === sessionVersion) {
                void vscode.window.showInformationMessage('Hot reload session closed.');
            }
        }),

        // An edit is only interesting once a session exists to apply it to. The button appearing
        // is the whole notification, so nothing else announces the change.
        vscode.workspace.onDidChangeTextDocument((event) => {
            if (!boundProject || event.contentChanges.length === 0) {
                return;
            }
            if (event.document.languageId !== 'csharp' || event.document.uri.scheme !== 'file') {
                return;
            }
            editVersion++;
            setPending(true);
        }),

        // The toolbar goes away with the last session, and its process took the applied state
        // with it; a stale button on the next F5 would offer to apply edits that are already
        // in the freshly built output.
        vscode.debug.onDidTerminateDebugSession((session) => {
            const ownerId = session && debugOwners.get(session.id);
            if (ownerId) {
                // Keep the association until release finishes: a fast restart must await
                // this baseline's disposal, including on its second and later restart.
                void releaseOwner(ownerId).finally(() => {
                    if (debugOwners.get(session.id) === ownerId) { debugOwners.delete(session.id); }
                });
                if (boundOwner === ownerId) { boundOwner = undefined; boundProject = undefined; boundClient = undefined; }
            }
            if (!vscode.debug.activeDebugSession) {
                setPending(false);
            }
        }),

        // Apply-on-save is the whole point of the feature for the ASP.NET inner loop: edit, save,
        // refresh the page. It stays opt-in because an apply steps on a running process.
        vscode.workspace.onDidSaveTextDocument(async (document) => {
            if (document.languageId !== 'csharp') {
                return;
            }
            if (!vscode.workspace.getConfiguration('roslynSense').get<boolean>('hotReload.applyOnSave', false)) {
                return;
            }

            const client = boundClient ?? getClient();
            if (!client) {
                return;
            }
            const project = await resolveProject(client, false);
            if (!project) {
                return;
            }

            await queueApply(client, project, diagnostics, false);
        })
    );
}

/// Opens the daemon-side edit session and makes it the target of pending-edit tracking, which
/// is what lets the toolbar button light up. Core launches get this via the environment merge
/// below; .NET Framework launches call it directly, since their applies travel through the
/// debugger and need no environment.
export function hotReloadOwnerFor(projectPath: string): string | undefined {
    return boundProject?.toLowerCase() === projectPath.toLowerCase() ? boundOwner : undefined;
}

export async function bindHotReloadSession(client: LanguageClient, projectPath: string): Promise<void> {
    // Binding at launch rather than at the first apply is what lets an edit made straight
    // after F5 light the toolbar button: until a project is bound there is no session
    // to attribute the change to.
    boundProject = projectPath;
    boundClient = client;
    sessionVersion++;
    editVersion++;
    setPending(false);

    // Open the edit session now rather than at the first apply: this is the moment the built
    // output matches the source, so the baseline predates the user's next edit. Await it
    // before launch, and report failures without preventing ordinary debugging.
    const ownerId = `launch:${Date.now()}:${sessionVersion}`;
    boundOwner = ownerId;
    const start = client.sendRequest<HotReloadResult>('roslynSense/hotReloadStart', { projectPath, ownerId });
    const owner: ReloadOwner = { client, projectPath, ownerId, start };
    // A launch cancelled before a debug session starts still needs to release its baseline.
    owner.timer = setTimeout(() => { void releaseOwner(ownerId); }, 120_000);
    owner.timer.unref();
    reloadOwners.set(ownerId, owner);
    try {
        const result = await start;
        if (!result.ok) { throw new Error(result.summary); }
    } catch (error) {
        await releaseOwner(ownerId);
        void vscode.window.showWarningMessage(`RoslynSense: Hot Reload is unavailable: ${String(error)}`);
    }
}

// A restart can reuse the VS Code session ID and the original launch configuration.
// Replace its baseline after rebuilding, and route process events to the new owner.
export async function restartDebugHotReload(client: LanguageClient, session: vscode.DebugSession): Promise<void> {
    const previous = debugOwners.get(session.id) ?? session.configuration.roslynSenseHotReloadOwner;
    debugOwners.delete(session.id);
    if (previous) { await releaseOwner(previous); }
    await bindHotReloadSession(client, session.configuration.projectPath);
    const ownerId = hotReloadOwnerFor(session.configuration.projectPath);
    if (ownerId) { debugOwners.set(session.id, ownerId); }
}

/// Adds what a launch needs for its process to be reloadable later. Returns the merged
/// environment, or the original when the tool has no agent to inject.
export async function withHotReloadEnvironment(
    client: LanguageClient,
    env: Record<string, string>,
    projectPath?: string
): Promise<Record<string, string>> {
    try {
        const settings = await client.sendRequest<HotReloadEnvironment>('roslynSense/hotReloadEnvironment');
        if (!settings.available) {
            void vscode.window.showWarningMessage(`RoslynSense: Hot Reload is unavailable: ${settings.message || 'the startup agent was not found.'}`);
            return env;
        }

        // This path never reaches the server's launcher, so the append has to happen here: a
        // project whose launchSettings sets its own startup hook must keep it AND get the agent,
        // or F5 silently loses hot reload.
        const merged: Record<string, string> = { ...settings.variables, ...env };
        const agentHooks = settings.variables['DOTNET_STARTUP_HOOKS'];
        const callerHooks = env['DOTNET_STARTUP_HOOKS'];
        if (agentHooks) {
            merged['DOTNET_STARTUP_HOOKS'] = [...new Set(
                [callerHooks, agentHooks].filter(Boolean).flatMap(hooks => hooks!.split(pathDelimiter()))
            )].join(pathDelimiter());
        }
        // These describe the actual agent connection, and cannot be overridden by a
        // stale launch profile or the runtime will never register a reloadable target.
        for (const [key, value] of Object.entries(settings.variables)) {
            if (key === 'ROSLYNSENSE_HOTRELOAD_PIPE' || key === 'DOTNET_MODIFIABLE_ASSEMBLIES') {
                merged[key] = value;
            }
        }

        if (projectPath) {
            await bindHotReloadSession(client, projectPath);
        }

        return merged;
    } catch (error) {
        void vscode.window.showWarningMessage(`RoslynSense: Hot Reload setup failed: ${String(error)}`);
        return env;
    }
}

function pathDelimiter(): string {
    return process.platform === 'win32' ? ';' : ':';
}

function queueApply(
    client: LanguageClient,
    projectPath: string,
    diagnostics: vscode.DiagnosticCollection,
    explicit: boolean
): Promise<void> {
    const session = sessionVersion;
    inFlight = inFlight.catch(() => undefined)
        .then(() => {
            if (session === sessionVersion && boundClient === client && boundProject === projectPath) {
                return apply(client, projectPath, diagnostics, explicit, session);
            }
        });
    return inFlight;
}

async function apply(
    client: LanguageClient,
    projectPath: string,
    diagnostics: vscode.DiagnosticCollection,
    explicit: boolean,
    session: number
): Promise<void> {
    const version = editVersion;
    let result: HotReloadResult;
    try {
        result = await client.sendRequest<HotReloadResult>('roslynSense/hotReloadApply', { projectPath, ownerId: boundOwner });
    } catch (err) {
        if (session === sessionVersion) {
            void vscode.window.showErrorMessage(`Hot reload failed: ${String(err)}`);
        }
        return;
    }

    if (session !== sessionVersion) {
        return;
    }

    publish(diagnostics, result);

    if (result.ok) {
        // "Queued" means the delta exists but the running process has not taken it: it was idle,
        // with no thread of the user's stopped in the edited module, so the engine holds the edit
        // until the app next runs that code. Reported like an apply — a four-second status bar
        // message and the button going dark — it read as "hot reload did nothing", because the
        // page still served the old code and nothing on screen said why.
        const queued = result.appliedTo.some((target) => target.endsWith('(queued)'));

        // An edit arriving while the request was running still needs its own apply.
        if (version === editVersion && !queued) {
            setPending(false);
        }

        if (queued) {
            void vscode.window.showWarningMessage(result.summary);
            return;
        }

        // A silent success on every save would be noise; an explicit invocation deserves an answer.
        if (explicit || result.appliedTo.length > 0) {
            void vscode.window.setStatusBarMessage(`$(zap) ${result.summary}`, 4000);
        }
        return;
    }

    const rude = result.diagnostics.find((d) => d.severity === 'error');
    const message = rude ? `${result.summary} ${rude.message}` : result.summary;

    // The prompt is not awaited: the apply is over either way, and holding the command open
    // until someone clicks makes a failed apply indistinguishable from one that never returned.
    void vscode.window.showWarningMessage(message, 'Restart', 'Show Problems').then(async (choice) => {
        if (session !== sessionVersion) {
            return;
        }
        if (choice === 'Restart') {
            await vscode.commands.executeCommand('workbench.action.debug.restart');
        } else if (choice === 'Show Problems') {
            await vscode.commands.executeCommand('workbench.actions.view.problems');
        }
    });
}

/// Rude edits are reported as diagnostics rather than only as a popup, so the user can see which
/// line they have to undo.
function publish(collection: vscode.DiagnosticCollection, result: HotReloadResult): void {
    collection.clear();

    const byFile = new Map<string, vscode.Diagnostic[]>();
    for (const entry of result.diagnostics) {
        if (!entry.filePath) {
            continue;
        }
        const line = Math.max(0, entry.line - 1);
        const diagnostic = new vscode.Diagnostic(
            new vscode.Range(line, 0, line, Number.MAX_SAFE_INTEGER),
            entry.message,
            entry.severity === 'error'
                ? vscode.DiagnosticSeverity.Error
                : vscode.DiagnosticSeverity.Warning
        );
        diagnostic.source = 'hot reload';
        diagnostic.code = entry.id;

        const list = byFile.get(entry.filePath) ?? [];
        list.push(diagnostic);
        byFile.set(entry.filePath, list);
    }

    for (const [file, list] of byFile) {
        collection.set(vscode.Uri.file(file), list);
    }
}

async function resolveProject(
    client: LanguageClient,
    allowPrompt: boolean
): Promise<string | undefined> {
    if (boundProject) {
        return boundProject;
    }

    let targets: LaunchTarget[] = [];
    try {
        targets = (await client.sendRequest<LaunchTarget[]>('roslynSense/launchTargets', {
            configuration: null,
        })).filter((t) => t.runnable);
    } catch {
        targets = [];
    }

    if (targets.length === 0) {
        if (allowPrompt) {
            void vscode.window.showWarningMessage('No runnable project was found to hot reload.');
        }
        return undefined;
    }

    if (targets.length === 1) {
        boundProject = targets[0].projectPath;
        boundClient = client;
        sessionVersion++;
        return boundProject;
    }

    if (!allowPrompt) {
        return undefined; // never interrupt a save with a picker
    }

    const picked = await vscode.window.showQuickPick(
        targets.map((t) => ({ label: t.projectName, description: t.projectPath, target: t })),
        { title: 'Apply hot reload to' }
    );

    boundProject = picked?.target.projectPath;
    boundClient = boundProject ? client : undefined;
    sessionVersion++;
    return boundProject;
}
