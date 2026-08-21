import * as fs from 'fs/promises';
import * as path from 'path';
import * as vscode from 'vscode';
import * as oniguruma from 'vscode-oniguruma';
import * as vsctm from 'vscode-textmate';

import { withoutByteOrderMark } from '../jsonText';

/**
 * Real syntax highlighting for the preview pane: the same TextMate engine and the same theme
 * rules the editor uses, run in the extension host.
 *
 * Grammars come from whatever is installed — the built-in C#, JSON and XML grammars, this
 * extension's own webforms/msbuild/resx/proto ones, anything else the user has added. Colors
 * come from the active color theme's `tokenColors`, resolved through `Registry.setTheme` +
 * `tokenizeLine2`, which is how VS Code's own markdown preview colors fenced code blocks. The
 * webview receives finished `{text, color, fontStyle}` spans and renders them verbatim.
 */

export interface PreviewToken {
    text: string;
    /** A concrete theme color, or null for the editor's default foreground. */
    color: string | null;
    /** Bit set: 1 italic, 2 bold, 4 underline, 8 strikethrough. */
    fontStyle: number;
}

/** vscode-textmate's metadata layout (MetadataConsts). */
const FONT_STYLE_MASK = 0b00000000000000000111100000000000;
const FONT_STYLE_OFFSET = 11;
const FOREGROUND_MASK = 0b00000000111111111000000000000000;
const FOREGROUND_OFFSET = 15;

/** Beyond this many lines from the top, threading tokenizer state costs more than it is worth. */
const MAX_TOKENIZED_LINES = 3000;

interface GrammarSource {
    grammarPath: string;
    /** Scopes this grammar injects itself into (`injectTo`), for e.g. MSBuild expressions. */
    injectTo: string[];
}

let registryPromise: Promise<vsctm.Registry | null> | undefined;
let themedFor: string | undefined;
const grammarsByScope = new Map<string, GrammarSource>();
const scopeByLanguage = new Map<string, string>();

/**
 * Tokenizes a document's lines `[startLine, endLine)` with the grammar registered for its
 * language, colored by the active theme. Returns null when no grammar exists or the engine
 * cannot start — the caller falls back to its own approximation.
 */
export async function tokenizePreview(
    document: vscode.TextDocument,
    startLine: number,
    endLine: number
): Promise<PreviewToken[][] | null> {
    if (endLine > MAX_TOKENIZED_LINES) {
        return null;
    }

    const registry = await getRegistry();
    const scope = scopeByLanguage.get(document.languageId);
    if (!registry || !scope) {
        return null;
    }

    try {
        await applyCurrentTheme(registry);
        const grammar = await registry.loadGrammar(scope);
        if (!grammar) {
            return null;
        }

        const colorMap = registry.getColorMap();
        const result: PreviewToken[][] = [];

        // Tokenize from the top so that state (block comments, verbatim strings, regions)
        // is correct by the time the window starts — the window alone would lie.
        let ruleStack = vsctm.INITIAL;
        for (let i = 0; i < endLine; i++) {
            const line = document.lineAt(i).text;
            const { tokens, ruleStack: next } = grammar.tokenizeLine2(line, ruleStack);
            ruleStack = next;

            if (i >= startLine) {
                const spans: PreviewToken[] = [];
                for (let t = 0; t < tokens.length; t += 2) {
                    const start = tokens[t];
                    const end = t + 2 < tokens.length ? tokens[t + 2] : line.length;
                    const metadata = tokens[t + 1];

                    const foreground = (metadata & FOREGROUND_MASK) >>> FOREGROUND_OFFSET;
                    spans.push({
                        text: line.slice(start, end),
                        // Color id 1 is the theme's default foreground; the webview's CSS
                        // default (the editor foreground variable) already is that color.
                        color: foreground > 1 ? (colorMap[foreground] ?? null) : null,
                        fontStyle: (metadata & FONT_STYLE_MASK) >>> FONT_STYLE_OFFSET,
                    });
                }
                result.push(spans);
            }
        }

        return result;
    } catch {
        return null;
    }
}

// ---- Registry ---------------------------------------------------------------------------

function getRegistry(): Promise<vsctm.Registry | null> {
    registryPromise ??= createRegistry();
    return registryPromise;
}

async function createRegistry(): Promise<vsctm.Registry | null> {
    try {
        collectGrammars();

        const wasmPath = require.resolve('vscode-oniguruma/release/onig.wasm');
        const wasm = await fs.readFile(wasmPath);
        await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));

        return new vsctm.Registry({
            onigLib: Promise.resolve({
                createOnigScanner: (sources) => new oniguruma.OnigScanner(sources),
                createOnigString: (str) => new oniguruma.OnigString(str),
            }),
            loadGrammar: async (scopeName) => {
                const source = grammarsByScope.get(scopeName);
                if (!source) {
                    return null;
                }
                const content = withoutByteOrderMark(
                    await fs.readFile(source.grammarPath, 'utf8')
                );
                return vsctm.parseRawGrammar(content, source.grammarPath);
            },
            getInjections: (scopeName) =>
                [...grammarsByScope.entries()]
                    .filter(([, source]) => source.injectTo.includes(scopeName))
                    .map(([injectedScope]) => injectedScope),
        });
    } catch {
        return null;
    }
}

interface ContributedGrammar {
    language?: string;
    scopeName?: string;
    path?: string;
    injectTo?: string[];
}

/** Every grammar any installed extension contributes, built-ins included. */
function collectGrammars(): void {
    for (const extension of vscode.extensions.all) {
        for (const grammar of (extension.packageJSON?.contributes?.grammars ?? []) as ContributedGrammar[]) {
            if (!grammar.scopeName || !grammar.path) {
                continue;
            }
            const grammarPath = path.join(extension.extensionPath, grammar.path);
            if (!grammarsByScope.has(grammar.scopeName)) {
                grammarsByScope.set(grammar.scopeName, {
                    grammarPath,
                    injectTo: grammar.injectTo ?? [],
                });
            }
            if (grammar.language && !scopeByLanguage.has(grammar.language)) {
                scopeByLanguage.set(grammar.language, grammar.scopeName);
            }
        }
    }
}

// ---- Theme ------------------------------------------------------------------------------

/** Re-reads the theme only when the user has switched to a different one. */
async function applyCurrentTheme(registry: vsctm.Registry): Promise<void> {
    const themeName =
        vscode.workspace.getConfiguration('workbench').get<string>('colorTheme') ?? '';
    if (themeName === themedFor) {
        return;
    }

    registry.setTheme({ name: themeName, settings: await loadThemeSettings(themeName) });
    themedFor = themeName;
}

interface ContributedTheme {
    id?: string;
    label?: string;
    path?: string;
}

/** vscode-textmate's IRawThemeSetting, which its typings declare but do not export. */
interface ThemeSetting {
    name?: string;
    scope?: string | string[];
    settings: {
        fontStyle?: string;
        foreground?: string;
        background?: string;
    };
}

interface ThemeFile {
    include?: string;
    tokenColors?: ThemeSetting[] | string;
    settings?: ThemeSetting[];
}

async function loadThemeSettings(themeName: string): Promise<ThemeSetting[]> {
    for (const extension of vscode.extensions.all) {
        for (const theme of (extension.packageJSON?.contributes?.themes ?? []) as ContributedTheme[]) {
            if ((theme.id ?? theme.label) !== themeName && theme.label !== themeName) {
                continue;
            }
            if (!theme.path) {
                continue;
            }
            return await readThemeChain(path.join(extension.extensionPath, theme.path));
        }
    }
    return [];
}

/** A theme may `include` a base theme; the base's rules come first so the theme's win. */
async function readThemeChain(themePath: string, depth = 0): Promise<ThemeSetting[]> {
    if (depth > 4) {
        return [];
    }

    const theme = parseJsonWithComments(
        withoutByteOrderMark(await fs.readFile(themePath, 'utf8'))
    ) as ThemeFile | null;
    if (theme === null) {
        return [];
    }

    const settings: ThemeSetting[] = [];
    if (theme.include) {
        settings.push(...(await readThemeChain(path.join(path.dirname(themePath), theme.include), depth + 1)));
    }

    // Old-style themes use `settings`; `tokenColors` may also be a path to a .tmTheme, which
    // nothing ships anymore — ignored rather than parsed.
    if (Array.isArray(theme.tokenColors)) {
        settings.push(...theme.tokenColors);
    } else if (Array.isArray(theme.settings)) {
        settings.push(...theme.settings);
    }

    return settings;
}

/** Theme files are JSONC: comments and trailing commas are expected, not errors. */
function parseJsonWithComments(text: string): unknown {
    let stripped = '';
    let i = 0;

    while (i < text.length) {
        const ch = text[i];

        if (ch === '"') {
            const start = i;
            i++;
            while (i < text.length && text[i] !== '"') {
                i += text[i] === '\\' ? 2 : 1;
            }
            i++;
            stripped += text.slice(start, i);
            continue;
        }

        if (ch === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') i++;
            continue;
        }

        if (ch === '/' && text[i + 1] === '*') {
            const end = text.indexOf('*/', i + 2);
            i = end < 0 ? text.length : end + 2;
            continue;
        }

        stripped += ch;
        i++;
    }

    stripped = stripped.replace(/,\s*([}\]])/g, '$1');

    try {
        return JSON.parse(stripped);
    } catch {
        return null;
    }
}
