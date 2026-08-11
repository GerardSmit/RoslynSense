import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { runTestsByName } from './testController';

/**
 * Test impact: run only what the working copy's changes can affect, and see per-method which
 * tests exercise the code in front of you.
 *
 * Both read the same per-test coverage map on the server — the thing an ordinary coverage report
 * cannot give, because it merges every test's hits into one number per line.
 */

interface CoveringTest {
    fullyQualifiedName: string;
    displayName: string;
    className: string;
    projectPath: string;
    filePath: string | null;
    line: number;
}

interface ImpactedTest {
    fullyQualifiedName: string;
    className: string;
    projectPath: string;
    reason: string;
    because: string | null;
}

interface ImpactedTestsResult {
    tests: ImpactedTest[];
    changedFiles: string[];
    uncoveredFiles: string[];
    description: string;
    mapWasEmpty: boolean;
    error: string | null;
}

interface BuildCoverageMapResult {
    classesRun: number;
    classesReused: number;
    testsMapped: number;
    failures: string[];
    error: string | null;
}

const REASON_LABELS: Record<string, string> = {
    CoveredChangedLines: 'covers the changed lines',
    CoveredChangedFile: 'covers the changed file',
    TestChanged: 'the test itself changed',
    ReferencesChangedCode: 'references the changed code',
};

export function registerImpactedTests(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    context.subscriptions.push(
        // The "N tests" CodeLens: list what covers this member, and offer to run the lot.
        vscode.commands.registerCommand(
            'roslynSense.showTestsAt',
            (uri: string, line: number, character: number) =>
                showTestsAt(getClient(), uri, line, character)
        ),
        vscode.commands.registerCommand('roslynSense.runImpactedTests', () =>
            runImpactedTests(getClient())
        ),
        vscode.commands.registerCommand('roslynSense.buildCoverageMap', (force?: boolean) =>
            buildCoverageMap(getClient(), force === true)
        )
    );
}

async function showTestsAt(
    client: LanguageClient | undefined,
    uri: string,
    line: number,
    character: number
): Promise<void> {
    if (!client) {
        return;
    }

    let tests: CoveringTest[];
    try {
        tests = await client.sendRequest<CoveringTest[]>('roslynSense/testsCovering', {
            uri,
            line,
            character,
        });
    } catch (error) {
        void vscode.window.showErrorMessage(`RoslynSense: ${describe(error)}`);
        return;
    }

    if (tests.length === 0) {
        void vscode.window.showInformationMessage(
            'RoslynSense: no tests are known to cover this member.'
        );
        return;
    }

    // Running them all is the reason most people click the lens, so it is the first item rather
    // than something to find behind a second gesture.
    const runAll = {
        label: `$(play) Run all ${tests.length} test${tests.length === 1 ? '' : 's'}`,
        test: undefined as CoveringTest | undefined,
    };
    const items = [
        runAll,
        ...tests.map((test) => ({
            label: `$(beaker) ${test.displayName}`,
            description: test.className,
            detail: test.filePath ?? undefined,
            test,
        })),
    ];

    const picked = await vscode.window.showQuickPick(items, {
        placeHolder: `${tests.length} test${tests.length === 1 ? '' : 's'} cover this member`,
        matchOnDescription: true,
    });
    if (!picked) {
        return;
    }

    if (!picked.test) {
        await runSelected(
            tests.map((t) => t.fullyQualifiedName),
            'the tests covering this member'
        );
        return;
    }

    if (picked.test.filePath) {
        await vscode.commands.executeCommand(
            'roslynSense.openLocation',
            vscode.Uri.file(picked.test.filePath).toString(),
            Math.max(0, picked.test.line - 1),
            0
        );
    }
}

async function runImpactedTests(client: LanguageClient | undefined): Promise<void> {
    if (!client) {
        void vscode.window.showErrorMessage('RoslynSense is not running.');
        return;
    }

    const scope = await vscode.window.showQuickPick(
        [
            {
                label: 'Uncommitted changes',
                detail: 'Staged and unstaged work against HEAD',
                scope: 'uncommitted',
            },
            {
                label: 'This branch',
                detail: 'Everything since the merge base with the main branch',
                scope: 'branch',
            },
            {
                label: 'Against a revision…',
                detail: 'A branch, tag, or commit you name',
                scope: 'ref',
            },
        ],
        { placeHolder: 'Which changes should decide what runs?' }
    );
    if (!scope) {
        return;
    }

    let gitRef: string | undefined;
    if (scope.scope === 'ref') {
        gitRef = await vscode.window.showInputBox({
            prompt: 'Compare the working tree against which revision?',
            placeHolder: 'origin/main',
        });
        if (!gitRef) {
            return;
        }
    }

    const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: 'RoslynSense: finding impacted tests' },
        async () => {
            try {
                return await client.sendRequest<ImpactedTestsResult>('roslynSense/impactedTests', {
                    scope: scope.scope,
                    gitRef: gitRef ?? null,
                    anchorPath: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? null,
                });
            } catch (error) {
                void vscode.window.showErrorMessage(`RoslynSense: ${describe(error)}`);
                return undefined;
            }
        }
    );

    if (!result) {
        return;
    }
    if (result.error) {
        void vscode.window.showErrorMessage(`RoslynSense: ${result.error}`);
        return;
    }

    if (result.tests.length === 0) {
        void vscode.window.showInformationMessage(
            result.changedFiles.length === 0
                ? 'RoslynSense: nothing has changed.'
                : `RoslynSense: no tests reach the ${result.changedFiles.length} changed file(s).`
        );
        return;
    }

    // Without a map the selection is reference-walking alone, which cannot see a call made
    // through a container or reflection. Say so once, with the fix attached.
    if (result.mapWasEmpty) {
        const build = 'Build coverage map';
        void vscode.window
            .showWarningMessage(
                'RoslynSense: no per-test coverage map yet, so impacted tests were found by ' +
                    'following references only.',
                build
            )
            .then((choice) => {
                if (choice === build) {
                    void vscode.commands.executeCommand('roslynSense.buildCoverageMap');
                }
            });
    }

    const summary = summarize(result);
    await runSelected(
        result.tests.map((t) => t.fullyQualifiedName),
        summary
    );
}

async function runSelected(names: string[], what: string): Promise<void> {
    const outcome = await runTestsByName(names);

    if (!outcome) {
        void vscode.window.showErrorMessage(
            'RoslynSense: the Test Explorer is not ready yet. Open the Testing view and try again.'
        );
        return;
    }

    if (outcome.ran === 0) {
        void vscode.window.showWarningMessage(
            `RoslynSense: none of the ${names.length} selected test(s) are in the Test Explorer. ` +
                'Refresh the Testing view — the coverage map may name tests that have since been renamed.'
        );
        return;
    }

    if (outcome.missing.length > 0) {
        void vscode.window.showWarningMessage(
            `RoslynSense: running ${outcome.ran} of ${names.length} selected tests; ` +
                `${outcome.missing.length} could not be found in the Test Explorer.`
        );
        return;
    }

    void vscode.window.setStatusBarMessage(`RoslynSense: running ${outcome.ran} tests — ${what}`, 5000);
}

async function buildCoverageMap(
    client: LanguageClient | undefined,
    force: boolean
): Promise<void> {
    if (!client) {
        void vscode.window.showErrorMessage('RoslynSense is not running.');
        return;
    }

    const confirm = await vscode.window.showInformationMessage(
        'Building the coverage map runs the test suite once per test class. This can take a ' +
            'while the first time; later builds only re-run classes you have edited.',
        { modal: true },
        'Build'
    );
    if (confirm !== 'Build') {
        return;
    }

    const result = await vscode.window.withProgress(
        {
            location: vscode.ProgressLocation.Notification,
            title: 'RoslynSense: building test coverage map',
            cancellable: false,
        },
        async () => {
            try {
                return await client.sendRequest<BuildCoverageMapResult>(
                    'roslynSense/buildCoverageMap',
                    { projectPath: null, force }
                );
            } catch (error) {
                void vscode.window.showErrorMessage(`RoslynSense: ${describe(error)}`);
                return undefined;
            }
        }
    );

    if (!result) {
        return;
    }
    if (result.error) {
        void vscode.window.showErrorMessage(`RoslynSense: ${result.error}`);
        return;
    }

    void vscode.window.showInformationMessage(
        `RoslynSense: mapped ${result.testsMapped} test(s) — ` +
            `${result.classesRun} class(es) run, ${result.classesReused} reused` +
            (result.failures.length > 0 ? `, ${result.failures.length} failed` : '') +
            '.'
    );
}

function summarize(result: ImpactedTestsResult): string {
    const counts = new Map<string, number>();
    for (const test of result.tests) {
        counts.set(test.reason, (counts.get(test.reason) ?? 0) + 1);
    }
    const parts = [...counts].map(
        ([reason, count]) => `${count} ${REASON_LABELS[reason] ?? reason.toLowerCase()}`
    );
    return `${result.description}: ${parts.join(', ')}`;
}

function describe(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}
