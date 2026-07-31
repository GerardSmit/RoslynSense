import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { withHotReloadEnvironment } from './hotReload';

/**
 * Build/rebuild/clean/test/watch tasks per project, so `preLaunchTask`, `Ctrl+Shift+B`, and
 * the Tasks menu all work without a hand-written tasks.json.
 *
 * Errors are matched with the built-in `$msCompile` matcher — MSBuild's diagnostic format is
 * exactly what it was written for, so a custom matcher would only be a worse copy.
 */

const TASK_TYPE = 'roslynsense';

interface TaskDefinition extends vscode.TaskDefinition {
    task: 'build' | 'rebuild' | 'clean' | 'test' | 'watch';
    project?: string;
    configuration?: string;
}

interface LaunchTarget {
    projectPath: string;
    projectName: string;
    runnable: boolean;
    isNetFramework: boolean;
}

interface ToolchainInfo {
    msbuildPath: string;
    hasDesktopClr: boolean;
    iisExpressPath: string | null;
}

export function registerTaskProvider(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    context.subscriptions.push(
        vscode.tasks.registerTaskProvider(TASK_TYPE, {
            async provideTasks() {
                const client = getClient();
                if (!client) {
                    return [];
                }

                let projects: LaunchTarget[] = [];
                try {
                    projects = await client.sendRequest<LaunchTarget[]>(
                        'roslynSense/launchTargets', { configuration: null });
                } catch {
                    return [];
                }

                const msbuild = projects.some((p) => p.isNetFramework)
                    ? (await fetchToolchain(client))?.msbuildPath
                    : undefined;

                const tasks: vscode.Task[] = [];
                for (const project of projects) {
                    // The dotnet CLI cannot build a non-SDK project; those need Visual Studio's
                    // MSBuild, which the server locates.
                    const legacy = project.isNetFramework && msbuild ? msbuild : undefined;

                    tasks.push(
                        makeTask({ type: TASK_TYPE, task: 'build', project: project.projectPath },
                            `build ${project.projectName}`,
                            legacy
                                ? [project.projectPath, '/nologo', '/v:minimal']
                                : ['build', project.projectPath, '--nologo'],
                            legacy),
                        makeTask({ type: TASK_TYPE, task: 'rebuild', project: project.projectPath },
                            `rebuild ${project.projectName}`,
                            legacy
                                ? [project.projectPath, '/nologo', '/v:minimal', '/t:Rebuild']
                                : ['build', project.projectPath, '--no-incremental', '--nologo'],
                            legacy),
                        makeTask({ type: TASK_TYPE, task: 'clean', project: project.projectPath },
                            `clean ${project.projectName}`,
                            legacy
                                ? [project.projectPath, '/nologo', '/t:Clean']
                                : ['clean', project.projectPath, '--nologo'],
                            legacy)
                    );

                    // Hot reload for the ASP.NET inner loop. Real Edit-and-Continue is not on
                    // offer: netcoredbg has no EnC support at all, so `dotnet watch` is the
                    // honest version of this feature.
                    if (project.runnable) {
                        tasks.push(makeTask(
                            { type: TASK_TYPE, task: 'watch', project: project.projectPath },
                            `watch ${project.projectName}`,
                            ['watch', '--project', project.projectPath, 'run']));
                    }
                }
                return tasks;
            },

            // Called for a task the user wrote in tasks.json: fill in the execution.
            async resolveTask(task) {
                const definition = task.definition as TaskDefinition;
                if (!definition.task) {
                    return undefined;
                }
                const target = definition.project ?? '';
                const configuration = definition.configuration ?? 'Debug';

                const client = getClient();
                const legacy = client && target
                    ? await msbuildFor(client, target)
                    : undefined;

                if (legacy) {
                    // MSBuild has no `watch`; the rest map onto targets.
                    const msbuildArgs: Record<TaskDefinition['task'], string[] | null> = {
                        build: [target, '/nologo', '/v:minimal', `/p:Configuration=${configuration}`],
                        rebuild: [target, '/nologo', '/v:minimal', '/t:Rebuild', `/p:Configuration=${configuration}`],
                        clean: [target, '/nologo', '/t:Clean', `/p:Configuration=${configuration}`],
                        test: null,
                        watch: null,
                    };
                    const args = msbuildArgs[definition.task];
                    if (args) {
                        return makeTask(definition, task.name, args, legacy);
                    }
                }

                const args: Record<TaskDefinition['task'], string[]> = {
                    build: ['build', target, '-c', configuration, '--nologo'],
                    rebuild: ['build', target, '-c', configuration, '--no-incremental', '--nologo'],
                    clean: ['clean', target, '-c', configuration, '--nologo'],
                    test: ['test', target, '-c', configuration, '--nologo'],
                    watch: ['watch', '--project', target, 'run'],
                };

                return makeTask(
                    definition,
                    task.name,
                    args[definition.task].filter((arg) => arg.length > 0)
                );
            },
        }),

        vscode.commands.registerCommand('roslynSense.runWithHotReload', async () => {
            const client = getClient();
            if (!client) {
                return;
            }

            let projects: LaunchTarget[] = [];
            try {
                projects = (await client.sendRequest<LaunchTarget[]>(
                    'roslynSense/launchTargets', { configuration: null })).filter((p) => p.runnable);
            } catch {
                projects = [];
            }
            if (projects.length === 0) {
                void vscode.window.showWarningMessage('No runnable project was found in the solution.');
                return;
            }

            const picked = projects.length === 1
                ? projects[0]
                : (await vscode.window.showQuickPick(
                    projects.map((p) => ({ label: p.projectName, description: p.projectPath, project: p })),
                    { title: 'Run with Hot Reload' }))?.project;

            if (!picked) {
                return;
            }

            // `dotnet run` rather than `dotnet watch`: the deltas come from Roslyn through
            // roslynSense/hotReloadApply, so the process only has to be started in a state that
            // accepts them. Watch would rebuild and restart, which is the thing hot reload avoids.
            const task = makeTask(
                { type: TASK_TYPE, task: 'watch', project: picked.projectPath },
                `run ${picked.projectName} (hot reload)`,
                ['run', '--project', picked.projectPath]);

            task.execution = new vscode.ShellExecution(
                'dotnet',
                ['run', '--project', picked.projectPath].map(
                    (arg) => ({ value: arg, quoting: vscode.ShellQuoting.Strong })),
                { env: await withHotReloadEnvironment(client, {}) });

            await vscode.tasks.executeTask(task);
        })
    );
}

/// Visual Studio's MSBuild when the project needs it, otherwise undefined.
async function msbuildFor(client: LanguageClient, projectPath: string): Promise<string | undefined> {
    try {
        const targets = await client.sendRequest<LaunchTarget[]>(
            'roslynSense/launchTargets', { configuration: null });
        const match = targets.find(
            (t) => t.projectPath.toLowerCase() === projectPath.toLowerCase());
        if (!match?.isNetFramework) {
            return undefined;
        }
        return (await fetchToolchain(client))?.msbuildPath || undefined;
    } catch {
        return undefined;
    }
}

async function fetchToolchain(client: LanguageClient): Promise<ToolchainInfo | undefined> {
    try {
        return await client.sendRequest<ToolchainInfo>('roslynSense/toolchain');
    } catch {
        return undefined;
    }
}

function makeTask(
    definition: TaskDefinition,
    name: string,
    args: string[],
    executable?: string
): vscode.Task {
    const task = new vscode.Task(
        definition,
        vscode.TaskScope.Workspace,
        name,
        'RoslynSense',
        new vscode.ShellExecution(
            executable ?? 'dotnet',
            args.map((arg) => ({ value: arg, quoting: vscode.ShellQuoting.Strong }))),
        ['$msCompile']
    );

    task.group =
        definition.task === 'build' || definition.task === 'rebuild'
            ? vscode.TaskGroup.Build
            : definition.task === 'clean'
              ? vscode.TaskGroup.Clean
              : definition.task === 'test'
                ? vscode.TaskGroup.Test
                : undefined;

    task.presentationOptions = {
        reveal: vscode.TaskRevealKind.Silent,
        clear: true,
        // A build that fails should surface; one that succeeds should not steal focus.
        showReuseMessage: false,
    };
    return task;
}
