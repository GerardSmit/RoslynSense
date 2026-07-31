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
            void vscode.window.showInformationMessage('Hot reload session closed.');
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
    env: Record<string, string>
): Promise<Record<string, string>> {
    try {
        const settings = await client.sendRequest<HotReloadEnvironment>('roslynSense/hotReloadEnvironment');
        if (!settings.available) {
            return env;
        }
        // The caller's own values win: a project that already sets a startup hook keeps it, and
        // the server's value is appended by the launcher rather than replacing it here.
        return { ...settings.variables, ...env };
    } catch {
        return env;
    }
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
