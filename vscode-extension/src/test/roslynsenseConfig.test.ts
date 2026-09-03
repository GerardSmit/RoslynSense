import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { after, before, beforeEach, describe, it } from 'node:test';

import {
    additionalConfigGlobs,
    anyCasing,
    configFilePath,
    loadLayers,
    mangleDirectory,
    originOf,
    valueAt,
    writeSetting,
} from '../roslynsenseConfig';

/**
 * The extension's own half of the configuration layering.
 *
 * Plain `node --test` rather than `@vscode/test-electron`: nothing here touches the `vscode` API,
 * and a suite that needs a downloaded editor to run is a suite that stops being run. The shape of
 * the rule is pinned against the server's answers in `parity.test.ts`; this file is about the
 * things only this side has — reading, writing, and the globs the document selector is built from.
 */

const root = path.join(os.tmpdir(), `roslynsense-ts-config-${process.pid}`);
const home = path.join(root, 'home');
const checkout = path.join(root, 'checkout');
const app = path.join(checkout, 'app');

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

beforeEach(() => {
    fs.rmSync(root, { recursive: true, force: true });
    fs.mkdirSync(home, { recursive: true });
    fs.mkdirSync(app, { recursive: true });
});

function write(filePath: string, contents: unknown): void {
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(
        filePath,
        typeof contents === 'string' ? contents : JSON.stringify(contents, undefined, 4),
        'utf8'
    );
}

describe('configFilePath', () => {
    it('puts the global file in the home directory', () => {
        assert.strictEqual(configFilePath('global', app), path.join(home, 'roslynsense.json'));
    });

    it('puts both repository files beside the working directory', () => {
        assert.strictEqual(configFilePath('repo', app), path.join(app, 'roslynsense.json'));
        assert.strictEqual(
            configFilePath('repoLocal', app),
            path.join(app, 'roslynsense.local.json')
        );
    });

    it('files the personal one under the mangled path of the checkout', () => {
        assert.strictEqual(
            configFilePath('personal', app),
            path.join(home, 'projects', mangleDirectory(app), 'roslynsense.json')
        );
    });
});

describe('loadLayers', () => {
    it('finds nothing when there is nothing to find', () => {
        const layered = loadLayers(app);

        assert.deepStrictEqual(layered.merged, {});
        assert.ok(layered.layers.every((layer) => !layer.json));
    });

    it('lists every candidate whether or not it exists', () => {
        write(configFilePath('repo', app), { tableFormat: 'toon' });

        const scopes = new Set(loadLayers(app).layers.map((layer) => layer.scope));

        assert.deepStrictEqual([...scopes].sort(), ['global', 'personal', 'repo', 'repoLocal']);
    });

    it('lets a nearer file override only the fields it names', () => {
        write(path.join(checkout, 'roslynsense.json'), {
            tools: { webForms: false },
            tableFormat: 'toon',
        });
        write(path.join(app, 'roslynsense.json'), { tableFormat: 'markdown' });

        const { merged } = loadLayers(app);

        assert.strictEqual(valueAt(merged, ['tools', 'webForms']), false);
        assert.strictEqual(merged.tableFormat, 'markdown');
    });

    it('orders the layers global, repository, local, personal', () => {
        write(configFilePath('global', app), { tableFormat: 'json' });
        write(configFilePath('repo', app), { tableFormat: 'markdown' });
        write(configFilePath('repoLocal', app), { tableFormat: 'toon' });

        assert.strictEqual(loadLayers(app).merged.tableFormat, 'toon');

        write(configFilePath('personal', app), { tableFormat: 'json' });

        assert.strictEqual(loadLayers(app).merged.tableFormat, 'json');
    });

    it('replaces arrays rather than appending to them', () => {
        write(configFilePath('global', app), { preload: ['global.sln'] });
        write(configFilePath('repo', app), { preload: ['repo.sln'] });

        assert.deepStrictEqual(loadLayers(app).merged.preload, ['repo.sln']);
    });

    it('skips a file that does not parse and reports why', () => {
        write(configFilePath('global', app), { tableFormat: 'toon' });
        write(configFilePath('repo', app), '{ not json');

        const layered = loadLayers(app);
        const broken = layered.layers.find((layer) => layer.parseError);

        assert.strictEqual(layered.merged.tableFormat, 'toon');
        assert.strictEqual(broken?.filePath, configFilePath('repo', app));
    });

    it('says where a file stopped parsing, not which branch the parser took', () => {
        write(configFilePath('repo', app), '{\n    "tableFormat": "markdown"\n    "preload": []\n}');

        const broken = loadLayers(app).layers.find((layer) => layer.parseError);

        // `printParseErrorCode` would answer `CommaExpected` and leave the line to be hunted for.
        assert.match(broken?.parseError ?? '', /line 3, column 5/);
    });

    it('reads a file that a Windows editor left a byte-order mark on', () => {
        // Visual Studio, Notepad and `Set-Content` all write one, and .NET strips it on the way
        // in — so a file the server was happily reading used to be reported here as broken.
        write(configFilePath('repo', app), '\uFEFF{ "tableFormat": "markdown" }');

        const layered = loadLayers(app);

        assert.strictEqual(layered.merged.tableFormat, 'markdown');
        assert.strictEqual(
            layered.layers.some((layer) => layer.parseError !== undefined),
            false
        );
    });

    it('leaves the byte-order mark where it found it', async () => {
        const filePath = configFilePath('repo', app);
        write(filePath, '\uFEFF{\n    "tableFormat": "markdown"\n}\n');

        await writeSetting('repo', app, ['tableFormat'], 'json');

        const written = fs.readFileSync(filePath, 'utf8');

        assert.strictEqual(written.startsWith('\uFEFF'), true);
        assert.strictEqual(loadLayers(app).merged.tableFormat, 'json');
    });

    it('reads comments and trailing commas, because people write them', () => {
        write(configFilePath('repo', app), '{\n  // ours\n  "tableFormat": "markdown",\n}');

        assert.strictEqual(loadLayers(app).merged.tableFormat, 'markdown');
    });

    it('treats an empty file as a file that says nothing', () => {
        write(configFilePath('global', app), { tableFormat: 'toon' });
        write(configFilePath('repo', app), '');

        assert.strictEqual(loadLayers(app).merged.tableFormat, 'toon');
    });
});

describe('originOf', () => {
    it('names the strongest layer that mentions the setting', () => {
        write(configFilePath('global', app), { tableFormat: 'json', maxWorkspaces: 8 });
        write(configFilePath('repoLocal', app), { tableFormat: 'toon' });

        const layered = loadLayers(app);

        assert.strictEqual(originOf(layered, ['tableFormat'])?.scope, 'repoLocal');
        assert.strictEqual(originOf(layered, ['maxWorkspaces'])?.scope, 'global');
        assert.strictEqual(originOf(layered, ['tools', 'razor']), undefined);
    });

    /**
     * A layer that sets a sibling key does not own the whole object, or the settings page would
     * blame the wrong file for a value it never mentioned.
     */
    it('looks inside objects rather than at their top key', () => {
        write(configFilePath('global', app), { tools: { razor: false } });
        write(configFilePath('repo', app), { tools: { webForms: false } });

        const layered = loadLayers(app);

        assert.strictEqual(originOf(layered, ['tools', 'razor'])?.scope, 'global');
        assert.strictEqual(originOf(layered, ['tools', 'webForms'])?.scope, 'repo');
    });
});

describe('writeSetting', () => {
    it('creates the file, and the directories above it', async () => {
        const written = await writeSetting('personal', app, ['tableFormat'], 'toon');

        assert.strictEqual(written, configFilePath('personal', app));
        assert.strictEqual(loadLayers(app).merged.tableFormat, 'toon');
    });

    it('writes nested settings without flattening what is beside them', async () => {
        write(configFilePath('repo', app), { tools: { razor: false }, tableFormat: 'toon' });

        await writeSetting('repo', app, ['tools', 'webForms'], false);

        const { merged } = loadLayers(app);
        assert.strictEqual(valueAt(merged, ['tools', 'razor']), false);
        assert.strictEqual(valueAt(merged, ['tools', 'webForms']), false);
        assert.strictEqual(merged.tableFormat, 'toon');
    });

    /**
     * The reason this goes through `jsonc-parser` rather than `JSON.stringify`: the file is one a
     * team keeps by hand, and a form that ate the comments would only ever be used once.
     */
    it('leaves the comments and the key order alone', async () => {
        const filePath = configFilePath('repo', app);
        write(
            filePath,
            [
                '{',
                '    // Why the team turned this off.',
                '    "tools": { "webForms": false },',
                '    "tableFormat": "toon"',
                '}',
                '',
            ].join('\n')
        );

        await writeSetting('repo', app, ['tableFormat'], 'markdown');
        const text = fs.readFileSync(filePath, 'utf8');

        assert.ok(text.includes('// Why the team turned this off.'), text);
        assert.ok(text.indexOf('"tools"') < text.indexOf('"tableFormat"'), text);
        assert.strictEqual(loadLayers(app).merged.tableFormat, 'markdown');
    });

    /** Unsetting removes the key; writing the default in would be a different statement. */
    it('removes the key when the value is undefined', async () => {
        write(configFilePath('global', app), { tableFormat: 'json' });
        write(configFilePath('repo', app), { tableFormat: 'toon' });

        await writeSetting('repo', app, ['tableFormat'], undefined);

        const layered = loadLayers(app);
        assert.strictEqual(layered.merged.tableFormat, 'json');
        assert.strictEqual(originOf(layered, ['tableFormat'])?.scope, 'global');
    });
});

describe('additionalConfigGlobs', () => {
    it('matches the name in any casing', () => {
        assert.deepStrictEqual(additionalConfigGlobs({ webConfig: { additionalFiles: ['release.config'] } }), [
            '**/[rR][eE][lL][eE][aA][sS][eE].[cC][oO][nN][fF][iI][gG]',
        ]);
    });

    it('says nothing when the setting is absent or not a list', () => {
        assert.deepStrictEqual(additionalConfigGlobs({}), []);
        assert.deepStrictEqual(additionalConfigGlobs({ webConfig: {} }), []);
        assert.deepStrictEqual(additionalConfigGlobs({ webConfig: { additionalFiles: 'release.config' } }), []);
    });

    /** The server warns about these once; a second warning for the same line would be noise. */
    it('drops paths, globs and anything that is not a name', () => {
        const globs = additionalConfigGlobs({
            webConfig: {
                additionalFiles: [
                    'sub/dir.config',
                    'sub\\dir.config',
                    '*.config',
                    'a?.config',
                    '',
                    42,
                    null,
                    'release.config',
                ],
            },
        });

        assert.deepStrictEqual(globs, ['**/[rR][eE][lL][eE][aA][sS][eE].[cC][oO][nN][fF][iI][gG]']);
    });
});

describe('anyCasing', () => {
    it('leaves characters that have no case alone', () => {
        assert.strictEqual(anyCasing('a.1-b'), '[aA].1-[bB]');
    });
});
