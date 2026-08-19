import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { after, before, describe, it } from 'node:test';
import { parse as parseJsonc } from 'jsonc-parser';

import { ConfigScope, configFilePath, loadLayers, mangleDirectory } from '../roslynsenseConfig';

/**
 * The other half of `RoslynMCP.Tests/Fixtures/ConfigLayering/parity.json`.
 *
 * The layering is implemented twice — the server resolves it for itself, and this extension
 * resolves it before there is a server to ask — so it can drift. The fixture is the one description
 * both answer to: `ConfigLayeringParityTests.cs` runs these same cases against the C# loader, and a
 * change made in one language fails in the other.
 */

interface MangledCase {
    readonly why: string;
    readonly directory: string;
    readonly expected: string;
}

interface MergeCase {
    readonly name: string;
    readonly files: Record<string, unknown>;
    readonly expected: Record<string, unknown>;
    readonly expectLoadError?: boolean;
}

interface Fixture {
    readonly mangledDirectories: Record<string, MangledCase[]>;
    readonly mergeCases: MergeCase[];
}

const fixture = readFixture();

const root = path.join(os.tmpdir(), `roslynsense-ts-parity-${process.pid}`);
const home = path.join(root, 'home');

let previousHome: string | undefined;

before(() => {
    previousHome = process.env.ROSLYNSENSE_HOME;
    process.env.ROSLYNSENSE_HOME = home;
});

after(() => {
    if (previousHome === undefined) {
        delete process.env.ROSLYNSENSE_HOME;
    } else {
        process.env.ROSLYNSENSE_HOME = previousHome;
    }
    fs.rmSync(root, { recursive: true, force: true });
});

describe('mangled directories match the shared fixture', () => {
    const cases = fixture.mangledDirectories[process.platform === 'win32' ? 'windows' : 'posix'];

    for (const testCase of cases) {
        it(`${testCase.directory} — ${testCase.why}`, () => {
            assert.strictEqual(mangleDirectory(testCase.directory), testCase.expected);
        });
    }
});

describe('merge cases match the shared fixture', () => {
    for (const [index, testCase] of fixture.mergeCases.entries()) {
        it(testCase.name, () => {
            const caseRoot = path.join(root, `case-${index}`);
            const checkout = path.join(caseRoot, 'checkout');
            const app = path.join(checkout, 'app');

            fs.rmSync(caseRoot, { recursive: true, force: true });
            fs.rmSync(home, { recursive: true, force: true });
            fs.mkdirSync(app, { recursive: true });
            fs.mkdirSync(home, { recursive: true });

            for (const [scope, contents] of Object.entries(testCase.files)) {
                write(filePathFor(scope, checkout, app), contents);
            }

            const layered = loadLayers(app);

            assert.deepStrictEqual(layered.merged, testCase.expected);
            assert.strictEqual(
                layered.layers.some((layer) => layer.parseError !== undefined),
                testCase.expectLoadError === true
            );
        });
    }
});

function filePathFor(scope: string, checkout: string, app: string): string {
    switch (scope) {
        case 'global':
        case 'personal':
        case 'repo':
        case 'repoLocal':
            return configFilePath(scope as ConfigScope, app);
        case 'parent':
            return path.join(checkout, 'roslynsense.json');
        case 'parentLocal':
            return path.join(checkout, 'roslynsense.local.json');
        default:
            throw new Error(`The fixture names an unknown scope: ${scope}`);
    }
}

/** A string in the fixture is the file's text verbatim; anything else is written as JSON. */
function write(filePath: string, contents: unknown): void {
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(
        filePath,
        typeof contents === 'string' ? contents : JSON.stringify(contents, undefined, 4),
        'utf8'
    );
}

function readFixture(): Fixture {
    let directory = __dirname;

    for (;;) {
        const candidate = path.join(
            directory,
            'RoslynMCP.Tests',
            'Fixtures',
            'ConfigLayering',
            'parity.json'
        );
        if (fs.existsSync(candidate)) {
            return parseJsonc(fs.readFileSync(candidate, 'utf8')) as Fixture;
        }

        const parent = path.dirname(directory);
        if (parent === directory) {
            throw new Error(
                'Could not find RoslynMCP.Tests/Fixtures/ConfigLayering/parity.json above ' +
                    __dirname +
                    '. The parity suite only runs from a checkout.'
            );
        }
        directory = parent;
    }
}
