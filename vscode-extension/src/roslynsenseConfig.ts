import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { applyEdits, modify, parse as parseJsonc, ParseError, printParseErrorCode } from 'jsonc-parser';

/**
 * Reading and writing `roslynsense.json`, in the same layers the server resolves it in.
 *
 * The server is the authority on what the layers mean — `RoslynMCP/Config/RoslynSenseConfigLoader.cs`
 * and `ConfigPaths.cs` — and this is the second implementation of that one rule, which is a cost
 * worth naming. It is here because both things that need it happen before the server can answer:
 * the document selector is fixed when the client is constructed, and the settings page has to show
 * and edit files for a solution whose server may not be running at all. The two must move together;
 * `ConfigLayerTests` and `ConfigPathsTests` are what pin the C# half.
 */

export type ConfigScope = 'global' | 'repo' | 'repoLocal' | 'personal';

/** One file considered while resolving the configuration, whether or not it exists. */
export interface ConfigLayer {
    readonly scope: ConfigScope;
    readonly filePath: string;
    /** Parsed contents, or undefined when the file does not exist or did not parse. */
    readonly json?: Record<string, unknown>;
    readonly parseError?: string;
}

export interface LayeredConfig {
    /** Every candidate, weakest first — including the ones that do not exist. */
    readonly layers: readonly ConfigLayer[];
    /** The layers merged, weakest first. */
    readonly merged: Record<string, unknown>;
}

export const CONFIG_FILE_NAME = 'roslynsense.json';
export const LOCAL_CONFIG_FILE_NAME = 'roslynsense.local.json';

/** Human-readable scope names, for the settings page and its messages. */
export const SCOPE_LABELS: Record<ConfigScope, string> = {
    global: 'Global',
    repo: 'Solution',
    repoLocal: 'Solution (personal)',
    personal: 'Personal',
};

/** `~/.roslynsense`, or wherever `ROSLYNSENSE_HOME` points. */
export function homeDirectory(): string {
    const overridden = process.env.ROSLYNSENSE_HOME;
    if (overridden) {
        return overridden;
    }
    return path.join(os.homedir(), '.roslynsense');
}

/**
 * A directory path flattened to one file-name-safe segment, exactly as `ConfigPaths.MangleDirectory`
 * does it: every non-alphanumeric character becomes a dash, then eight hex digits of the path so
 * that two checkouts whose flattened names collide still get separate directories.
 */
export function mangleDirectory(directory: string): string {
    const full = trimTrailingSeparator(path.resolve(directory));
    const readable = full.replace(/[^a-zA-Z0-9]/g, '-');
    const hash = crypto.createHash('sha256').update(full.toLowerCase(), 'utf8').digest('hex');
    return `${readable}-${hash.slice(0, 8)}`;
}

/** Where a scope's file lives for a given working directory. */
export function configFilePath(scope: ConfigScope, workingDirectory: string): string {
    switch (scope) {
        case 'global':
            return path.join(homeDirectory(), CONFIG_FILE_NAME);
        case 'repo':
            return path.join(workingDirectory, CONFIG_FILE_NAME);
        case 'repoLocal':
            return path.join(workingDirectory, LOCAL_CONFIG_FILE_NAME);
        case 'personal':
            return path.join(
                homeDirectory(),
                'projects',
                mangleDirectory(workingDirectory),
                CONFIG_FILE_NAME
            );
    }
}

/**
 * Every layer that applies to a working directory, weakest first: the global file, then each
 * repository file from the filesystem root down with its `.local.json` sibling after it, then the
 * personal file kept for this directory.
 */
export function loadLayers(workingDirectory: string): LayeredConfig {
    const layers: ConfigLayer[] = [readLayer('global', configFilePath('global', workingDirectory))];

    for (const directory of repositoryDirectories(workingDirectory)) {
        layers.push(readLayer('repo', path.join(directory, CONFIG_FILE_NAME)));
        layers.push(readLayer('repoLocal', path.join(directory, LOCAL_CONFIG_FILE_NAME)));
    }

    layers.push(readLayer('personal', configFilePath('personal', workingDirectory)));

    const merged: Record<string, unknown> = {};
    for (const layer of layers) {
        if (layer.json) {
            deepMerge(merged, layer.json);
        }
    }

    return { layers, merged };
}

/**
 * Which layer an effective value came from, or undefined when no layer sets it.
 *
 * The strongest one that names the path — the same walk the merge does, read backwards.
 */
export function originOf(layered: LayeredConfig, settingPath: readonly string[]): ConfigLayer | undefined {
    for (let i = layered.layers.length - 1; i >= 0; i--) {
        const layer = layered.layers[i];
        if (layer.json && valueAt(layer.json, settingPath) !== undefined) {
            return layer;
        }
    }
    return undefined;
}

/** The value at a dotted path, or undefined. */
export function valueAt(root: unknown, settingPath: readonly string[]): unknown {
    let node: unknown = root;
    for (const segment of settingPath) {
        if (typeof node !== 'object' || node === null) {
            return undefined;
        }
        node = (node as Record<string, unknown>)[segment];
    }
    return node;
}

/**
 * Writes one setting into one scope's file, creating it if needed.
 *
 * Surgical rather than a rewrite: `jsonc-parser` edits the text around the value, so comments, key
 * order and indentation in a file a team maintains by hand all survive being touched from a form.
 * Passing `undefined` removes the key, which is how the settings page unsets a value rather than
 * writing the default in as if someone had chosen it.
 */
export async function writeSetting(
    scope: ConfigScope,
    workingDirectory: string,
    settingPath: readonly string[],
    value: unknown
): Promise<string> {
    const filePath = configFilePath(scope, workingDirectory);
    await fs.promises.mkdir(path.dirname(filePath), { recursive: true });

    let text = '';
    try {
        text = await fs.promises.readFile(filePath, 'utf8');
    } catch {
        text = '{}\n';
    }
    if (text.trim().length === 0) {
        text = '{}\n';
    }

    const edits = modify(text, [...settingPath], value, {
        formattingOptions: { insertSpaces: true, tabSize: 4 },
    });

    await fs.promises.writeFile(filePath, applyEdits(text, edits), 'utf8');
    return filePath;
}

/**
 * The globs matching the file names `webConfig.additionalFiles` claims.
 *
 * Here rather than in `extension.ts` because it is the one piece of the selector that is worth
 * testing on its own: it decides whether a file the person named in a config file is served at
 * all, and it has to survive both the casing they used and the casing on disk.
 */
export function additionalConfigGlobs(merged: Record<string, unknown>): string[] {
    const declared = valueAt(merged, ['webConfig', 'additionalFiles']);
    if (!Array.isArray(declared)) {
        return [];
    }

    return declared
        .filter(
            (name): name is string =>
                typeof name === 'string' && name.length > 0 && !/[\\/*?]/.test(name)
        )
        .map((name) => `**/${anyCasing(name)}`);
}

/**
 * A glob matching one file name whatever its casing: `release.config` becomes `[rR][eE][lL]...`.
 *
 * VS Code matches globs case-sensitively, and the built-in `web.config` patterns spell the two
 * casings that actually occur by hand. A name someone typed into a config file has no such
 * convention, and `Release.config` not matching because the pattern said `release.config` would be
 * a bug reported as "the setting does nothing".
 */
export function anyCasing(fileName: string): string {
    return [...fileName]
        .map((character) => {
            const lower = character.toLowerCase();
            const upper = character.toUpperCase();
            return lower === upper ? character : `[${lower}${upper}]`;
        })
        .join('');
}

function readLayer(scope: ConfigScope, filePath: string): ConfigLayer {
    let text: string;
    try {
        text = fs.readFileSync(filePath, 'utf8');
    } catch {
        return { scope, filePath };
    }

    if (text.trim().length === 0) {
        return { scope, filePath, json: {} };
    }

    const errors: ParseError[] = [];
    const parsed = parseJsonc(text, errors, { allowTrailingComma: true });

    if (errors.length > 0) {
        return {
            scope,
            filePath,
            parseError: printParseErrorCode(errors[0].error),
        };
    }

    return typeof parsed === 'object' && parsed !== null
        ? { scope, filePath, json: parsed as Record<string, unknown> }
        : { scope, filePath, parseError: 'Expected an object.' };
}

/** The filesystem root down to the working directory — outermost first, so nearer files win. */
function repositoryDirectories(workingDirectory: string): string[] {
    const chain: string[] = [];
    let current = path.resolve(workingDirectory);

    for (;;) {
        chain.push(current);
        const parent = path.dirname(current);
        if (parent === current) {
            break;
        }
        current = parent;
    }

    return chain.reverse();
}

/** Objects merge key by key; everything else, arrays included, replaces. */
function deepMerge(target: Record<string, unknown>, overlay: Record<string, unknown>): void {
    for (const [key, value] of Object.entries(overlay)) {
        const existing = target[key];
        if (isPlainObject(value) && isPlainObject(existing)) {
            deepMerge(existing, value);
        } else {
            target[key] = value;
        }
    }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function trimTrailingSeparator(value: string): string {
    return value.length > 1 && (value.endsWith(path.sep) || value.endsWith('/'))
        ? value.slice(0, -1)
        : value;
}
