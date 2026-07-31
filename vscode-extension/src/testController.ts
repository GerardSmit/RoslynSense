import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { DEBUG_TYPE } from './debugLaunch';

/**
 * Test Explorer backed by the shared solution: discovery is a Roslyn walk in the daemon (not a
 * separate `dotnet test --list-tests` process), runs go through `dotnet test` with a TRX
 * logger, and coverage reuses the Cobertura data the coverage tools already parse.
 */

interface DiscoveredTest {
    id: string;
    fullyQualifiedName: string;
    displayName: string;
    className: string;
    namespace: string | null;
    framework: string;
    filePath: string | null;
    startLine: number;
    endLine: number;
    projectPath: string;
}

interface TestResult {
    fullyQualifiedName: string;
    outcome: string;
    durationMs: number;
    errorMessage: string | null;
    stackTrace: string | null;
}

interface TestProject {
    projectPath: string;
    projectName: string;
}

interface LineCoverage {
    line: number;
    hits: number;
}

interface FileCoverage {
    filePath: string;
    lines: LineCoverage[];
}

/**
 * Runs a single test by fully-qualified name, discovering its project first if the Test
 * Explorer has not been expanded yet. Returns false when there is no controller or the test
 * cannot be found, so the caller can fall back.
 */
export async function runTestById(
    fullyQualifiedName: string,
    projectPath: string,
    mode: 'run' | 'debug'
): Promise<boolean> {
    if (!activeController || !activeGetClient) {
        return false;
    }

    const client = activeGetClient();
    if (!client) {
        return false;
    }

    let item = findTestItem(activeController, fullyQualifiedName);
    if (!item && projectPath) {
        const projectItem = activeController.items.get(`project:${projectPath}`);
        if (projectItem) {
            await discoverTests(client, activeController, projectItem, projectPath, activeTestData);
            item = findTestItem(activeController, fullyQualifiedName);
        }
    }
    if (!item) {
        return false;
    }

    const profile = mode === 'debug' ? activeDebugProfile : activeRunProfile;
    if (!profile) {
        return false;
    }

    await vscode.commands.executeCommand('vscode.revealTestInExplorer', item);
    const request = new vscode.TestRunRequest([item], undefined, profile);
    await profile.runHandler(request, new vscode.CancellationTokenSource().token);
    return true;
}

let activeController: vscode.TestController | undefined;
let activeGetClient: (() => LanguageClient | undefined) | undefined;
let activeRunProfile: vscode.TestRunProfile | undefined;
let activeDebugProfile: vscode.TestRunProfile | undefined;
const activeTestData = new Map<string, DiscoveredTest>();

function findTestItem(
    controller: vscode.TestController,
    id: string
): vscode.TestItem | undefined {
    let found: vscode.TestItem | undefined;
    const visit = (item: vscode.TestItem) => {
        if (found) {
            return;
        }
        if (item.id === id) {
            found = item;
            return;
        }
        item.children.forEach(visit);
    };
    controller.items.forEach(visit);
    return found;
}

export function registerTestController(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    const controller = vscode.tests.createTestController('roslynSense', 'C# Tests');
    context.subscriptions.push(controller);

    // projectPath for project-level items, DiscoveredTest for leaves.
    const projectItems = new Map<string, vscode.TestItem>();
    const testData = activeTestData;
    activeController = controller;
    activeGetClient = getClient;

    controller.resolveHandler = async (item) => {
        const client = getClient();
        if (!client) {
            return;
        }
        try {
            if (!item) {
                await discoverProjects(client, controller, projectItems);
                return;
            }
            const projectPath = item.id.startsWith('project:') ? item.id.slice('project:'.length) : undefined;
            if (projectPath) {
                await discoverTests(client, controller, item, projectPath, testData);
            }
        } catch (error) {
            item ??= undefined;
            void vscode.window.showErrorMessage(`Test discovery failed: ${describe(error)}`);
        } finally {
            if (item) {
                item.busy = false;
            }
        }
    };

    activeRunProfile = controller.createRunProfile(
        'Run',
        vscode.TestRunProfileKind.Run,
        (request, token) => runTests(controller, getClient, testData, request, token, 'run'),
        true
    );
    activeDebugProfile = controller.createRunProfile(
        'Debug',
        vscode.TestRunProfileKind.Debug,
        (request, token) => runTests(controller, getClient, testData, request, token, 'debug'),
        true
    );
    controller.createRunProfile(
        'Coverage',
        vscode.TestRunProfileKind.Coverage,
        (request, token) => runTests(controller, getClient, testData, request, token, 'coverage'),
        true
    );

    // Saving a test file can add or remove tests; rediscovering the owning project is cheap
    // because the daemon already holds the compilation.
    let rediscoverTimer: NodeJS.Timeout | undefined;
    context.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument((document) => {
            if (document.languageId !== 'csharp') {
                return;
            }
            clearTimeout(rediscoverTimer);
            rediscoverTimer = setTimeout(() => {
                const client = getClient();
                if (!client) {
                    return;
                }
                for (const [projectPath, item] of projectItems) {
                    if (!document.uri.fsPath.startsWith(dirname(projectPath))) {
                        continue;
                    }
                    void discoverTests(client, controller, item, projectPath, testData).catch(() => undefined);
                }
            }, 500);
        })
    );
}

async function discoverProjects(
    client: LanguageClient,
    controller: vscode.TestController,
    projectItems: Map<string, vscode.TestItem>
): Promise<void> {
    const projects = await client.sendRequest<TestProject[]>('roslynSense/testProjects');

    const seen = new Set<string>();
    for (const project of projects) {
        const id = `project:${project.projectPath}`;
        seen.add(id);

        let item = controller.items.get(id);
        if (!item) {
            item = controller.createTestItem(id, project.projectName, vscode.Uri.file(project.projectPath));
            item.canResolveChildren = true;
            controller.items.add(item);
        }
        projectItems.set(project.projectPath, item);
    }

    for (const [id] of [...enumerate(controller.items)]) {
        if (!seen.has(id)) {
            controller.items.delete(id);
        }
    }
}

async function discoverTests(
    client: LanguageClient,
    controller: vscode.TestController,
    projectItem: vscode.TestItem,
    projectPath: string,
    testData: Map<string, DiscoveredTest>
): Promise<void> {
    projectItem.busy = true;
    try {
        const tests = await client.sendRequest<DiscoveredTest[]>('roslynSense/testDiscover', {
            projectPath,
        });

        // Rebuilt wholesale: a class rename would otherwise leave an orphan branch behind.
        projectItem.children.replace([]);

        for (const test of tests) {
            const classId = `class:${projectPath}:${test.namespace ?? ''}.${test.className}`;
            let classItem = projectItem.children.get(classId);
            if (!classItem) {
                classItem = controller.createTestItem(
                    classId,
                    test.className,
                    test.filePath ? vscode.Uri.file(test.filePath) : undefined
                );
                classItem.description = test.namespace ?? undefined;
                projectItem.children.add(classItem);
            }

            const item = controller.createTestItem(
                test.id,
                test.displayName,
                test.filePath ? vscode.Uri.file(test.filePath) : undefined
            );
            if (test.filePath && test.startLine > 0) {
                item.range = new vscode.Range(
                    Math.max(0, test.startLine - 1),
                    0,
                    Math.max(0, (test.endLine || test.startLine) - 1),
                    0
                );
            }
            classItem.children.add(item);
            testData.set(test.id, test);
        }
    } finally {
        projectItem.busy = false;
    }
}

async function runTests(
    controller: vscode.TestController,
    getClient: () => LanguageClient | undefined,
    testData: Map<string, DiscoveredTest>,
    request: vscode.TestRunRequest,
    token: vscode.CancellationToken,
    mode: 'run' | 'debug' | 'coverage'
): Promise<void> {
    const client = getClient();
    if (!client) {
        void vscode.window.showErrorMessage('RoslynSense is not running.');
        return;
    }

    const run = controller.createTestRun(request);
    const queue = collectLeaves(controller, request);

    try {
        // Group by project: one `dotnet test` invocation per project, not per test.
        const byProject = new Map<string, vscode.TestItem[]>();
        for (const item of queue) {
            const test = testData.get(item.id);
            if (!test) {
                run.skipped(item);
                continue;
            }
            const existing = byProject.get(test.projectPath);
            if (existing) {
                existing.push(item);
            } else {
                byProject.set(test.projectPath, [item]);
            }
            run.enqueued(item);
        }

        for (const [projectPath, items] of byProject) {
            if (token.isCancellationRequested) {
                break;
            }

            const names = items
                .map((item) => testData.get(item.id)?.fullyQualifiedName)
                .filter((name): name is string => Boolean(name));

            if (mode === 'debug') {
                await debugTests(client, projectPath, names, run, items);
                continue;
            }

            items.forEach((item) => run.started(item));

            const results = await client.sendRequest<TestResult[]>('roslynSense/testRun', {
                projectPath,
                fullyQualifiedNames: names,
                collectCoverage: mode === 'coverage',
            });

            applyResults(run, items, testData, results);

            if (mode === 'coverage') {
                await applyCoverage(client, run, projectPath);
            }
        }
    } catch (error) {
        void vscode.window.showErrorMessage(`Test run failed: ${describe(error)}`);
    } finally {
        run.end();
    }
}

/**
 * Debugging a test reuses the same mechanism the AI-side debugger uses: `dotnet test` with
 * VSTEST_HOST_DEBUG=1 suspends the test host and reports its PID, and we attach to that.
 * Attaching before the host resumes is what makes a breakpoint in the first test reliable.
 */
async function debugTests(
    client: LanguageClient,
    projectPath: string,
    names: string[],
    run: vscode.TestRun,
    items: vscode.TestItem[]
): Promise<void> {
    items.forEach((item) => run.started(item));

    const { processId, error } = await client.sendRequest<{
        processId: number;
        error: string | null;
    }>('roslynSense/testDebug', { projectPath, fullyQualifiedNames: names });

    if (!processId) {
        items.forEach((item) =>
            run.errored(item, new vscode.TestMessage(error ?? 'Could not start the test host.'))
        );
        return;
    }

    const started = await vscode.debug.startDebugging(undefined, {
        type: DEBUG_TYPE,
        request: 'attach',
        name: 'C#: Debug Test',
        processId,
    });

    if (!started) {
        items.forEach((item) =>
            run.errored(item, new vscode.TestMessage('Could not attach the debugger to the test host.'))
        );
        return;
    }

    // Outcomes are owned by the debug session from here; the run just stops tracking them.
    items.forEach((item) => run.skipped(item));
}

function applyResults(
    run: vscode.TestRun,
    items: vscode.TestItem[],
    testData: Map<string, DiscoveredTest>,
    results: TestResult[]
): void {
    const byName = new Map(results.map((result) => [result.fullyQualifiedName, result]));

    for (const item of items) {
        const test = testData.get(item.id);
        const result = test ? byName.get(test.fullyQualifiedName) : undefined;
        if (!result) {
            run.skipped(item);
            continue;
        }

        switch (result.outcome.toLowerCase()) {
            case 'passed':
                run.passed(item, result.durationMs);
                break;
            case 'failed':
                run.failed(item, buildMessage(result, item), result.durationMs);
                break;
            default:
                run.skipped(item);
                break;
        }
    }
}

/** xUnit/NUnit assertion text carries expected/actual; surfacing them lights up the diff view. */
function buildMessage(result: TestResult, item: vscode.TestItem): vscode.TestMessage {
    const text = [result.errorMessage, result.stackTrace].filter(Boolean).join('\n');
    const expected = /Expected:\s*(.+)/i.exec(result.errorMessage ?? '');
    const actual = /Actual:\s*(.+)/i.exec(result.errorMessage ?? '');

    const message =
        expected && actual
            ? vscode.TestMessage.diff(
                  result.errorMessage ?? 'Assertion failed',
                  expected[1].trim(),
                  actual[1].trim()
              )
            : new vscode.TestMessage(text || 'Test failed');

    if (item.uri && item.range) {
        message.location = new vscode.Location(item.uri, item.range);
    }
    return message;
}

async function applyCoverage(
    client: LanguageClient,
    run: vscode.TestRun,
    projectPath: string
): Promise<void> {
    try {
        const files = await client.sendRequest<FileCoverage[]>('roslynSense/testCoverage', {
            projectPath,
        });
        for (const file of files) {
            const uri = vscode.Uri.file(file.filePath);
            const detailed = file.lines.map(
                (line) =>
                    new vscode.StatementCoverage(
                        line.hits,
                        new vscode.Position(Math.max(0, line.line - 1), 0)
                    )
            );
            const covered = detailed.filter((statement) => Number(statement.executed) > 0).length;
            const coverage = new vscode.FileCoverage(
                uri,
                new vscode.TestCoverageCount(covered, detailed.length)
            );
            (coverage as { detailedCoverage?: vscode.StatementCoverage[] }).detailedCoverage = detailed;
            run.addCoverage(coverage);
        }
    } catch {
        // Coverage is a bonus on top of the run; a failure here must not fail the run.
    }
}

function collectLeaves(
    controller: vscode.TestController,
    request: vscode.TestRunRequest
): vscode.TestItem[] {
    const leaves: vscode.TestItem[] = [];
    const excluded = new Set(request.exclude?.map((item) => item.id) ?? []);

    const visit = (item: vscode.TestItem) => {
        if (excluded.has(item.id)) {
            return;
        }
        if (item.children.size === 0) {
            leaves.push(item);
            return;
        }
        item.children.forEach(visit);
    };

    if (request.include) {
        request.include.forEach(visit);
    } else {
        controller.items.forEach(visit);
    }
    return leaves;
}

function* enumerate(collection: vscode.TestItemCollection): Generator<[string, vscode.TestItem]> {
    const entries: [string, vscode.TestItem][] = [];
    collection.forEach((item) => entries.push([item.id, item]));
    yield* entries;
}

function dirname(filePath: string): string {
    const index = Math.max(filePath.lastIndexOf('/'), filePath.lastIndexOf('\\'));
    return index < 0 ? filePath : filePath.slice(0, index);
}

function describe(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}
