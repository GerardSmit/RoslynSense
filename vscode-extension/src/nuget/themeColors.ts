import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

import { withoutByteOrderMark } from '../jsonText';

/**
 * The active colour theme's token colours, for the README code blocks.
 *
 * VS Code exposes its colour *registry* to a webview as `--vscode-*` custom properties, and the
 * editor background and foreground come from there for free. Token colours are not in that
 * registry — they are TextMate rules living in the theme extension's own JSON — so a webview that
 * wants to look like the editor has to go and read them.
 *
 * That is what this does: find the extension contributing the selected theme, read its JSON,
 * follow any `include` chain, apply the user's own `editor.tokenColorCustomizations` on top, and
 * resolve a handful of scopes down to the seven classes the highlighter emits.
 *
 * Every step is allowed to fail. A theme distributed as a `.tmTheme` plist, a theme that ships no
 * rule for strings, a malformed file — each just leaves that entry out, and the stylesheet's own
 * fallback palette covers it. Nothing here is load-bearing enough to be worth throwing over.
 */

/** Token class suffix (`.tok-kw`) to the scopes that would colour it, most specific first. */
const Probes: Record<string, string[]> = {
    com: ['comment'],
    str: ['string.quoted.double', 'string'],
    kw: ['keyword.control', 'keyword', 'storage.type'],
    typ: ['entity.name.type', 'entity.name.class', 'support.type', 'support.class'],
    num: ['constant.numeric', 'constant'],
    tag: ['entity.name.tag', 'entity.name.tag.localname.xml'],
    attr: ['entity.other.attribute-name', 'variable.other', 'variable'],
};

interface Rule {
    scope?: string | string[];
    settings?: { foreground?: string; fontStyle?: string };
}

/**
 * Resolves the active theme's token colours.
 *
 * Returns an empty object rather than throwing when the theme cannot be read, which the caller
 * treats as "use the stylesheet's fallbacks".
 */
export function resolveTokenColors(): Record<string, string> {
    try {
        const rules = activeThemeRules();
        if (rules.length === 0) {
            return {};
        }

        const colors: Record<string, string> = {};
        for (const [token, probes] of Object.entries(Probes)) {
            const found = match(rules, probes);
            if (found) {
                colors[token] = found;
            }
        }
        return colors;
    } catch {
        return {};
    }
}

/**
 * The rules the theme applies, in precedence order (later wins).
 *
 * The user's `editor.tokenColorCustomizations` goes last because that is what it is for. Its
 * theme-scoped form — a `"[Theme Name]"` key holding the same shape — is honoured too, since
 * anyone customising one theme's tokens is almost certainly using that form.
 */
function activeThemeRules(): Rule[] {
    const name = vscode.workspace.getConfiguration('workbench').get<string>('colorTheme');
    const rules: Rule[] = name ? readTheme(name) : [];

    const customizations = vscode.workspace
        .getConfiguration('editor')
        .get<Record<string, unknown>>('tokenColorCustomizations');

    if (customizations) {
        rules.push(...textMateRules(customizations));
        if (name) {
            rules.push(...textMateRules(customizations[`[${name}]`]));
        }
    }
    return rules;
}

function textMateRules(value: unknown): Rule[] {
    const rules = (value as { textMateRules?: unknown } | undefined)?.textMateRules;
    return Array.isArray(rules) ? (rules as Rule[]) : [];
}

/** Finds the theme file the label names, across every installed extension. */
function readTheme(label: string): Rule[] {
    for (const extension of vscode.extensions.all) {
        const themes = extension.packageJSON?.contributes?.themes;
        if (!Array.isArray(themes)) {
            continue;
        }
        for (const theme of themes) {
            if (theme.label === label || theme.id === label) {
                return readThemeFile(path.join(extension.extensionPath, theme.path));
            }
        }
    }
    return [];
}

/**
 * Reads one theme file and everything it includes.
 *
 * `include` is how the built-in themes are built — Dark+ is a short file on top of `dark_vs.json`
 * — so a resolver that ignores it finds almost nothing for the default theme. Included rules come
 * first so the including file overrides them, which is the direction VS Code merges in.
 */
function readThemeFile(file: string, depth = 0): Rule[] {
    // An `include` cycle is a broken theme, not something to hang on.
    if (depth > 8 || !fs.existsSync(file)) {
        return [];
    }

    const theme = JSON.parse(
        stripJsonComments(withoutByteOrderMark(fs.readFileSync(file, 'utf8')))
    );
    const rules: Rule[] = [];

    if (typeof theme.include === 'string') {
        rules.push(...readThemeFile(path.join(path.dirname(file), theme.include), depth + 1));
    }

    // `tokenColors` is a path to a .tmTheme plist in some themes rather than an array. Parsing
    // plist XML to colour a README is not a trade worth making; the fallback palette handles it.
    if (Array.isArray(theme.tokenColors)) {
        rules.push(...(theme.tokenColors as Rule[]));
    }
    return rules;
}

/**
 * Comments and trailing commas are legal in a VS Code theme file, and `JSON.parse` rejects both.
 *
 * String-aware, because `"https://example.com"` contains what looks like a line comment and a theme
 * whose colours vanish because of a URL in its own metadata would be a puzzling bug to meet.
 */
function stripJsonComments(text: string): string {
    let out = '';
    let inString = false;
    let inLine = false;
    let inBlock = false;

    for (let i = 0; i < text.length; i++) {
        const char = text[i];
        const next = text[i + 1];

        if (inLine) {
            if (char === '\n') {
                inLine = false;
                out += char;
            }
            continue;
        }
        if (inBlock) {
            if (char === '*' && next === '/') {
                inBlock = false;
                i++;
            }
            continue;
        }
        if (inString) {
            out += char;
            if (char === '\\') {
                out += text[++i] ?? '';
            } else if (char === '"') {
                inString = false;
            }
            continue;
        }
        if (char === '"') {
            inString = true;
            out += char;
            continue;
        }
        if (char === '/' && next === '/') {
            inLine = true;
            i++;
            continue;
        }
        if (char === '/' && next === '*') {
            inBlock = true;
            i++;
            continue;
        }
        out += char;
    }

    return out.replace(/,(\s*[}\]])/g, '$1');
}

/**
 * The best colour any rule offers for one of the probe scopes.
 *
 * TextMate scopes nest by dots, and a theme may be either side of the probe: it might colour all of
 * `string`, or only `string.quoted.double`. Both should answer a question about strings, so both
 * directions count — an exact hit first, then a rule broader than the probe, then a narrower one.
 * Ties go to the later rule, which is how a theme overriding its own include is meant to resolve.
 */
function match(rules: Rule[], probes: string[]): string | null {
    let best: string | null = null;
    let bestScore = 0;

    for (const rule of rules) {
        const foreground = rule.settings?.foreground;
        if (!foreground || !rule.scope) {
            continue;
        }

        // A theme may write scopes as an array, or as one comma-separated string, or both.
        const scopes = (Array.isArray(rule.scope) ? rule.scope : [rule.scope])
            .flatMap((s) => String(s).split(','))
            .map((s) => s.trim())
            .filter(Boolean);

        for (const scope of scopes) {
            for (let i = 0; i < probes.length; i++) {
                const probe = probes[i];
                const relation =
                    scope === probe ? 3 : probe.startsWith(`${scope}.`) ? 2 : scope.startsWith(`${probe}.`) ? 1 : 0;
                if (relation === 0) {
                    continue;
                }

                // Earlier probes are the ones we would rather answer with, so they outrank a
                // better relation found further down the list.
                const score = (probes.length - i) * 10 + relation;
                if (score >= bestScore) {
                    bestScore = score;
                    best = foreground;
                }
            }
        }
    }

    return best;
}
