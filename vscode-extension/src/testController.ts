import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { onClientReady } from './clientReady';
import { DEBUG_TYPE } from './debugLaunch';
import { isUnder } from './paths';

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

/** Results plus, when nothing ran, the reason — which is not the same as everything being skipped. */
interface TestRunResponse {
    results: TestResult[];
    error: string | null;
}

interface TestProject {
    projectPath: string;
    projectName: string;
}

interface LineCoverage {
    line: number;
    hits: number;
    coveredBranches: number;
    totalBranches: number;
}

/** A test finishing, or a line of console output, while the run is still going. */
interface TestRunEvent {
    runId: string;
    kind: 'output' | 'passed' | 'failed' | 'skipped';
    fullyQualifiedName: string | null;
    message: string | null;
    durationMs: number;
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

/**
 * Runs a named set of tests as one Test Explorer run — what "run the tests my changes affect"
 * needs, since the selection is a list of fully-qualified names spread across projects rather
 * than a subtree the user clicked.
 *
 * Returns the names it could not find in the tree; a caller that changed a test's name since
 * the coverage map was built will see them, and they are worth saying out loud rather than
 * silently running fewer tests than were selected.
 */
export async function runTestsByName(
    fullyQualifiedNames: string[],
    mode: 'run' | 'debug' = 'run'
): Promise<{ ran: number; missing: string[] } | undefined> {
    if (!activeController || !activeGetClient?.()) {
        return undefined;
    }

    await activeEnsureDiscovered?.();

    const items: vscode.TestItem[] = [];
    const missing: string[] = [];
    for (const name of fullyQualifiedNames) {
        const item = findTestItem(activeController, name);
        if (item) {
            items.push(item);
        } else {
            missing.push(name);
        }
    }

    if (items.length === 0) {
        return { ran: 0, missing };
    }

    const profile = mode === 'debug' ? activeDebugProfile : activeRunProfile;
    if (!profile) {
        return undefined;
    }

    const request = new vscode.TestRunRequest(items, undefined, profile);
    await profile.runHandler(request, new vscode.CancellationTokenSource().token);
    return { ran: items.length, missing };
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
    registerRunEvents(context, getClient);

    // projectPath for project-level items, DiscoveredTest for leaves.
    const projectItems = new Map<string, vscode.TestItem>();
    const testData = activeTestData;
    activeController = controller;
    activeGetClient = getClient;

    // Discovery is driven by the client becoming available, not only by the view being opened.
    // resolveHandler runs once when the Testing view first appears, which on a cold start is
    // before the server has connected — it returned empty and was never asked again, so the
    // panel sat on "No tests have been found in this workspace yet" forever.
    // Down to the tests, not just the projects. "Run Tests" runs whatever the tree holds, and a
    // tree of unexpanded project nodes holds nothing — so a run before the first manual refresh
    // reported "no tests" against a panel that was visibly listing test projects.
    const discoverAll = async (client: LanguageClient) => {
        await discoverProjects(client, controller, projectItems);
        for (const [projectPath, item] of projectItems) {
            await discoverTests(client, controller, item, projectPath, testData).catch(() => undefined);
        }
    };

    // Projects only, and that is the whole of the eager work. Listing them comes off the .sln,
    // but discovering the tests inside one pulls that project into the Roslyn workspace — which
    // is serialized behind a single load gate on the server and runs a `dotnet restore` for any
    // project whose obj/ is cold. On a solution with forty test projects the loop below therefore
    // ran forty restores back to back before anything else in the editor could be answered, and
    // the Solution Explorer sat empty behind them. The tests themselves are filled in when a
    // project node is expanded, and before a run by activeEnsureDiscovered.
    onClientReady(context, getClient, (client) => {
        void discoverProjects(client, controller, projectItems).catch(() => undefined);
    });

    // Also consulted by a run, so starting one before discovery has finished waits for it rather
    // than running an empty tree.
    activeEnsureDiscovered = async () => {
        const client = getClient();
        const unresolved = [...enumerate(controller.items)].some(
            ([, item]) => item.canResolveChildren && item.children.size === 0
        );
        if (client && unresolved) {
            await discoverAll(client).catch(() => undefined);
        }
    };
    context.subscriptions.push({ dispose: () => (activeEnsureDiscovered = undefined) });

    // The refresh button in the Testing view, which previously did nothing.
    controller.refreshHandler = async () => {
        const client = getClient();
        if (client) {
            await discoverAll(client);
        }
    };

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
    //
    // The saved files pile up in a set that the timer drains, rather than the timer closing over
    // one document. The timer exists to coalesce a "Save All" into a single pass, and a captured
    // document is whichever file VS Code happened to fire last — so every other project in that
    // save kept its stale test list, with nothing to say so.
    const savedPaths = new Set<string>();
    let rediscoverTimer: NodeJS.Timeout | undefined;
    context.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument((document) => {
            if (document.languageId !== 'csharp') {
                return;
            }
            savedPaths.add(document.uri.fsPath);
            clearTimeout(rediscoverTimer);
            rediscoverTimer = setTimeout(() => {
                // Drained whether or not there is a client: with no server nothing has been
                // discovered yet, so there is no stale list for these saves to correct, and
                // holding on to them would only grow the set for the rest of the session.
                const saved = [...savedPaths];
                savedPaths.clear();
                const client = getClient();
                if (client) {
                    void rediscoverSaved(client, controller, projectItems, saved, testData);
                }
            }, 500);
        })
    );
}

/**
 * Rediscovers each test project that owns at least one of the saved files, once per project
 * rather than once per file.
 *
 * Sequential, the way discoverAll is, and deliberately not a fan-out of `void discoverTests(...)`:
 * every discovery pulls its project through the single load gate on the server, so starting them
 * together does not finish them any sooner and does park every unrelated request behind the whole
 * batch.
 *
 * The ownership test is `isUnder` and not a prefix test on the raw strings, for two reasons that
 * both cost more than they look. `document.uri.fsPath` comes back from VS Code with a lower-cased
 * drive letter while `projectPath` carries MSBuild's casing, so a case-sensitive comparison
 * matches nothing at all and on-save rediscovery quietly never runs. And without a separator
 * boundary `Foo.Tests` claims every save inside its sibling `Foo.Tests.Integration` — which is not
 * merely a wasted redraw, because discovering an unexpanded project drags it through that same
 * load gate and a cold `dotnet restore` for a file it does not contain.
 */
async function rediscoverSaved(
    client: LanguageClient,
    controller: vscode.TestController,
    projectItems: Map<string, vscode.TestItem>,
    savedPaths: string[],
    testData: Map<string, DiscoveredTest>
): Promise<void> {
    for (const [projectPath, item] of projectItems) {
        const projectDirectory = dirname(projectPath);
        if (!savedPaths.some((saved) => isUnder(saved, projectDirectory))) {
            continue;
        }
        await discoverTests(client, controller, item, projectPath, testData).catch(() => undefined);
    }
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

    // Run All against an unexpanded tree used to collect the project nodes themselves, find no
    // test behind them, and mark each one skipped — which read as "these tests were skipped"
    // rather than "nothing has been discovered yet".
    await activeEnsureDiscovered?.();

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

            // The run id is how progress events and cancellation find their way back to this
            // run: the request itself does not return until every test has finished.
            const runId = `${Date.now()}-${runCounter++}`;
            const live = trackRun(runId, run, items, testData);
            const cancelled = token.onCancellationRequested(() => {
                void client.sendNotification('roslynSense/testCancel', { runId });
            });

            try {
                const response = await client.sendRequest<TestRunResponse>('roslynSense/testRun', {
                    projectPath,
                    fullyQualifiedNames: names,
                    collectCoverage: mode === 'coverage',
                    runId,
                });

                applyResults(run, items, testData, response.results, live.reported, response.error);
            } finally {
                live.dispose();
                cancelled.dispose();
            }

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

let runCounter = 0;

/** Resolves any project whose tests have not been discovered yet. Set while the view is alive. */
let activeEnsureDiscovered: (() => Promise<void>) | undefined;

/** Live listeners keyed by run id, so one run's events never land in another's. */
const liveRuns = new Map<string, (event: TestRunEvent) => void>();

/**
 * Routes `roslynSense/testRunEvent` into a run: console output goes to the Test Results
 * terminal, and each outcome marks its item as soon as it is known.
 *
 * Marking early matters for a long run — without it every test sits spinning until the last one
 * finishes, which is indistinguishable from the run being stuck. The final results still arrive
 * over the request and still win; `reported` records what was already shown so they are not
 * written twice.
 */
function trackRun(
    runId: string,
    run: vscode.TestRun,
    items: vscode.TestItem[],
    testData: Map<string, DiscoveredTest>
): { reported: Set<string>; dispose: () => void } {
    const reported = new Set<string>();
    const byName = new Map<string, vscode.TestItem>();
    for (const item of items) {
        const test = testData.get(item.id);
        if (test) {
            byName.set(test.fullyQualifiedName, item);
        }
    }

    liveRuns.set(runId, (event) => {
        if (event.kind === 'output') {
            run.appendOutput((event.message ?? '') + '\r\n');
            return;
        }

        const name = event.fullyQualifiedName;
        // vstest prints the display name, which for a [Theory] carries its arguments; the
        // item is keyed by the method it came from.
        const item = name
            ? byName.get(name) ?? byName.get(name.split('(')[0])
            : undefined;
        if (!item || reported.has(item.id)) {
            return;
        }

        reported.add(item.id);
        if (event.kind === 'passed') {
            run.passed(item, event.durationMs);
        } else if (event.kind === 'failed') {
            // The assertion text is only in the TRX; this marks it failed now and the final
            // pass replaces the message with the real one.
            run.failed(item, new vscode.TestMessage('Failed'), event.durationMs);
        } else {
            run.skipped(item);
        }
    });

    return { reported, dispose: () => liveRuns.delete(runId) };
}

/** Wires the server's run events into whichever run they belong to. */
function registerRunEvents(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    onClientReady(context, getClient, (client) => {
        context.subscriptions.push(
            client.onNotification('roslynSense/testRunEvent', (event: TestRunEvent) => {
                liveRuns.get(event.runId)?.(event);
            })
        );
    });
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
    results: TestResult[],
    liveReported?: Set<string>,
    error?: string | null
): void {
    const byName = new Map(results.map((result) => [result.fullyQualifiedName, result]));

    for (const item of items) {
        const test = testData.get(item.id);
        const result = test ? byName.get(test.fullyQualifiedName) : undefined;
        if (!result) {
            if (liveReported?.has(item.id)) {
                // A cancelled run has real outcomes for the tests that got as far as running.
                continue;
            }

            // "Skipped" is what a test framework says about a test it chose not to run. A run
            // that never started — no MSBuild, a failed build, a timeout — is a different thing,
            // and reporting it as skipped hid the reason behind an innocuous grey icon.
            if (error) {
                run.errored(item, new vscode.TestMessage(error));
            } else {
                run.skipped(item);
            }
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
            let totalBranches = 0;
            let coveredBranches = 0;

            const detailed = file.lines.map((line) => {
                const position = new vscode.Position(Math.max(0, line.line - 1), 0);
                if (line.totalBranches <= 0) {
                    return new vscode.StatementCoverage(line.hits, position);
                }

                totalBranches += line.totalBranches;
                coveredBranches += line.coveredBranches;

                // One BranchCoverage per condition. Cobertura reports how many of a line's
                // branches were taken but not which, so they are reported as the first N
                // covered — the count is what paints the "1 of 2 branches" gutter, and
                // claiming to know which arm ran would be inventing detail.
                const branches = Array.from(
                    { length: line.totalBranches },
                    (_, index) =>
                        new vscode.BranchCoverage(index < line.coveredBranches, position)
                );
                return new vscode.StatementCoverage(line.hits, position, branches);
            });

            const covered = detailed.filter((statement) => Number(statement.executed) > 0).length;
            const coverage = new vscode.FileCoverage(
                uri,
                new vscode.TestCoverageCount(covered, detailed.length),
                totalBranches > 0
                    ? new vscode.TestCoverageCount(coveredBranches, totalBranches)
                    : undefined
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
