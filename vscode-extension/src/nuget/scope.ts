import * as vscode from 'vscode';

/**
 * Which projects a package operation applies to.
 *
 * An empty scope means "every project that already references the package" — which is the set
 * Update and Uninstall were always going to touch, and which used to have to be hand-picked one
 * project at a time on a forty-project solution.
 *
 * Install is the exception and stays disabled while nothing is chosen. It is the one action with
 * no existing set to infer from, and the previous panel's "all projects" default meant pressing
 * Install from the Browse tab wrote a PackageReference into every project in the solution — a
 * change nobody asked for and nobody notices until the next build.
 */
const SCOPE_KEY = 'roslynSense.nuget.scope';

export function savedScope(
    context: vscode.ExtensionContext,
    projects: NuGetMsg.ProjectRef[]
): string[] {
    const known = new Set(projects.map((p) => p.projectPath.toLowerCase()));

    const mode = vscode.workspace
        .getConfiguration('roslynSense')
        .get<'lastUsed' | 'openedProject' | 'none'>('nuget.defaultScope', 'lastUsed');

    // One project in the solution has exactly one sensible answer.
    if (projects.length === 1) {
        return [projects[0].projectPath];
    }

    if (mode !== 'lastUsed') {
        return [];
    }

    // Projects come and go; a remembered path that no longer exists must not silently widen or
    // narrow what the buttons claim.
    return context.workspaceState
        .get<string[]>(SCOPE_KEY, [])
        .filter((path) => known.has(path.toLowerCase()));
}

export function rememberScope(context: vscode.ExtensionContext, scope: string[]): void {
    void context.workspaceState.update(SCOPE_KEY, scope);
}

/**
 * Picks projects through VS Code's own multi-select, rather than a hand-written popover in the
 * webview: it is filterable, keyboard- and screen-reader-correct for free, and matches how the
 * rest of this extension asks the same question.
 */
export async function pickScope(
    projects: NuGetMsg.ProjectRef[],
    preselected: string[]
): Promise<string[] | undefined> {
    if (projects.length === 0) {
        void vscode.window.showWarningMessage('No projects are loaded.');
        return undefined;
    }

    const selected = new Set(preselected.map((p) => p.toLowerCase()));

    const picks = await vscode.window.showQuickPick(
        projects.map((project) => ({
            label: project.projectName,
            description: project.targetFrameworks.join(', '),
            detail: project.projectPath,
            picked: selected.has(project.projectPath.toLowerCase()),
        })),
        {
            canPickMany: true,
            title: 'Projects the panel acts on',
            placeHolder: 'Select projects, or none for all of them',
        }
    );

    return picks?.map((pick) => pick.detail);
}
