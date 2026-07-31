import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

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

                const tasks: vscode.Task[] = [];
                for (const project of projects) {
                    tasks.push(
                        makeTask({ type: TASK_TYPE, task: 'build', project: project.projectPath },
                            `build ${project.projectName}`,
                            ['build', project.projectPath, '--nologo']),
                        makeTask({ type: TASK_TYPE, task: 'rebuild', project: project.projectPath },
                            `rebuild ${project.projectName}`,
                            ['build', project.projectPath, '--no-incremental', '--nologo']),
                        makeTask({ type: TASK_TYPE, task: 'clean', project: project.projectPath },
                            `clean ${project.projectName}`,
                            ['clean', project.projectPath, '--nologo'])
                    );
                }
                return tasks;
            },

            // Called for a task the user wrote in tasks.json: fill in the execution.
            resolveTask(task) {
                const definition = task.definition as TaskDefinition;
                if (!definition.task) {
                    return undefined;
                }
                const target = definition.project ?? '';
                const configuration = definition.configuration ?? 'Debug';

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
        })
    );
}

function makeTask(
    definition: TaskDefinition,
    name: string,
    args: string[]
): vscode.Task {
    const task = new vscode.Task(
        definition,
        vscode.TaskScope.Workspace,
        name,
        'RoslynSense',
        new vscode.ShellExecution('dotnet', args.map((arg) => ({ value: arg, quoting: vscode.ShellQuoting.Strong }))),
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
