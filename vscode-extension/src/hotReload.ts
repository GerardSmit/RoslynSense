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

/// Serialises applies: two overlapping ones would each diff against a baseline the other is
/// about to move.
let inFlight: Promise<void> = Promise.resolve();

/// Drives the debug toolbar button: true once an edit has landed that the running process has
/// not seen yet. Mirrored into a context key because `when` clauses are the only way a menu
/// contribution can read extension state.
let pending = false;

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

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.applyHotReload', async () => {
            const client = getClient();
            if (!client) {
                void vscode.window.showErrorMessage('RoslynSense is not running.');
                return;
            }
            const project = await resolveProject(client, true);
            if (project) {
                await apply(client, project, diagnostics, true);
            }
        }),

        vscode.commands.registerCommand('roslynSense.stopHotReload', async () => {
            const client = getClient();
            if (!client || !boundProject) {
                return;
            }
            await client.sendRequest<HotReloadResult>('roslynSense/hotReloadStop', {
                projectPath: boundProject,
            });
            diagnostics.clear();
            boundProject = undefined;
            setPending(false);
            void vscode.window.showInformationMessage('Hot reload session closed.');
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
            setPending(true);
        }),

        // The toolbar goes away with the last session, and its process took the applied state
        // with it; a stale button on the next F5 would offer to apply edits that are already
        // in the freshly built output.
        vscode.debug.onDidTerminateDebugSession(() => {
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

            const client = getClient();
            if (!client) {
                return;
            }
            const project = await resolveProject(client, false);
            if (!project) {
                return;
            }

            inFlight = inFlight
                .catch(() => undefined)
                .then(() => apply(client, project, diagnostics, false));
            await inFlight;
        })
    );
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
            return env;
        }

        // This path never reaches the server's launcher, so the append has to happen here: a
        // project whose launchSettings sets its own startup hook must keep it AND get the agent,
        // or F5 silently loses hot reload.
        const merged: Record<string, string> = { ...settings.variables, ...env };
        const agentHooks = settings.variables['DOTNET_STARTUP_HOOKS'];
        const callerHooks = env['DOTNET_STARTUP_HOOKS'];
        if (agentHooks && callerHooks && callerHooks !== agentHooks) {
            merged['DOTNET_STARTUP_HOOKS'] = `${callerHooks}${pathDelimiter()}${agentHooks}`;
        }

        // Open the edit session now rather than at the first apply: this is the moment the built
        // output matches the source, so the baseline predates the user's next edit. Failure is
        // non-fatal — the first apply retries.
        if (projectPath) {
            // Binding here rather than at the first apply is what lets an edit made straight
            // after F5 light the toolbar button: until a project is bound there is no session
            // to attribute the change to.
            boundProject = projectPath;
            setPending(false);
            void client
                .sendRequest<HotReloadResult>('roslynSense/hotReloadStart', { projectPath })
                .catch(() => undefined);
        }

        return merged;
    } catch {
        return env;
    }
}

function pathDelimiter(): string {
    return process.platform === 'win32' ? ';' : ':';
}

async function apply(
    client: LanguageClient,
    projectPath: string,
    diagnostics: vscode.DiagnosticCollection,
    explicit: boolean
): Promise<void> {
    let result: HotReloadResult;
    try {
        result = await client.sendRequest<HotReloadResult>('roslynSense/hotReloadApply', { projectPath });
    } catch (err) {
        void vscode.window.showErrorMessage(`Hot reload failed: ${String(err)}`);
        return;
    }

    publish(diagnostics, result);

    if (result.ok) {
        setPending(false);

        // A silent success on every save would be noise; an explicit invocation deserves an answer.
        if (explicit || result.appliedTo.length > 0) {
            void vscode.window.setStatusBarMessage(`$(zap) ${result.summary}`, 4000);
        }
        return;
    }

    const rude = result.diagnostics.find((d) => d.severity === 'error');
    const message = rude ? `${result.summary} ${rude.message}` : result.summary;

    const choice = await vscode.window.showWarningMessage(message, 'Restart', 'Show Problems');
    if (choice === 'Restart') {
        await vscode.commands.executeCommand('workbench.action.debug.restart');
    } else if (choice === 'Show Problems') {
        await vscode.commands.executeCommand('workbench.actions.view.problems');
    }
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
    return boundProject;
}
