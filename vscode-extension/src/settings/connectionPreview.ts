import * as fs from 'fs';
import * as path from 'path';
import { parse as parseJsonc, ParseError } from 'jsonc-parser';

import { withoutByteOrderMark } from '../jsonText';

/**
 * The panel's mirror of the server's ConnectionStringResolver: enough of its semantics to show
 * what a `json:`/`xml:` connection reference will resolve to, without asking the server.
 *
 * Deliberately pure (fs and path only, no vscode import), so it can be exercised outside the
 * extension host. Where the server does more than a preview honestly can — `${…}` placeholders,
 * probing solution and git roots for relative paths, arbitrary XPath — the preview says so
 * instead of guessing.
 */

/** The provider aliases DbProviderFactory accepts, keyed lowercase. */
const PROVIDER_ALIASES = new Set([
    'psql',
    'postgres',
    'postgresql',
    'mssql',
    'sqlserver',
    'sql',
    'sqlite',
]);

export interface ConnectionPreview {
    resolved?: string;
    error?: string;
}

/** `mssql:json:app.json#X` → { head: 'mssql:', ref: 'json:app.json#X' }. No provider → no head. */
export function splitProvider(value: string): { head: string; ref: string } {
    const colon = value.indexOf(':');
    if (colon > 0 && PROVIDER_ALIASES.has(value.slice(0, colon).toLowerCase())) {
        return { head: value.slice(0, colon + 1), ref: value.slice(colon + 1) };
    }
    return { head: '', ref: value };
}

/**
 * What the reference resolves to right now. Undefined for raw connection strings — echoing the
 * person's own text back at them is not a preview.
 */
export function previewConnection(
    value: string,
    workingDirectory: string
): ConnectionPreview | undefined {
    const { ref } = splitProvider(value);

    let kind: 'json' | 'xml';
    if (/^json:/i.test(ref)) {
        kind = 'json';
    } else if (/^xml:/i.test(ref)) {
        kind = 'xml';
    } else {
        return undefined;
    }

    const body = ref.slice(kind.length + 1);
    const hash = body.indexOf('#');
    if (hash <= 0) {
        return { error: `Reference needs '#<name>' after the file path.` };
    }

    const filePart = body.slice(0, hash);
    const query = body.slice(hash + 1);
    if (query === '') {
        return { error: `Reference needs a name after '#'.` };
    }

    if (filePart.includes('${')) {
        return { error: 'Placeholders are expanded by the server; no preview.' };
    }

    const filePath = path.isAbsolute(filePart)
        ? filePart
        : path.resolve(workingDirectory, filePart);
    if (!fs.existsSync(filePath)) {
        return { error: `File not found: ${filePath}` };
    }

    try {
        return kind === 'json' ? previewJson(filePath, query) : previewXml(filePath, query);
    } catch (error) {
        return { error: error instanceof Error ? error.message : String(error) };
    }
}

function previewJson(filePath: string, query: string): ConnectionPreview {
    const root = parseJsonFile(filePath);

    // The server's forms: `#name` is `$.ConnectionStrings.name`; `#$.a.b` is a dotted path.
    const segments = query.startsWith('$.')
        ? query.slice(2).split('.')
        : query === '$'
          ? []
          : ['ConnectionStrings', query];

    let node: unknown = root;
    for (const segment of segments) {
        if (typeof node !== 'object' || node === null || Array.isArray(node)) {
            return { error: `'${query}' did not resolve (at '${segment}').` };
        }
        node = (node as Record<string, unknown>)[segment];
        if (node === undefined) {
            return { error: `'${query}' did not resolve (at '${segment}').` };
        }
    }

    if (typeof node !== 'string') {
        return { error: `'${query}' is not a string value.` };
    }
    return { resolved: maskSecrets(node) };
}

function previewXml(filePath: string, query: string): ConnectionPreview {
    if (query.startsWith('/')) {
        return { error: 'XPath queries are resolved by the server; no preview.' };
    }

    for (const attributes of xmlAddElements(fs.readFileSync(filePath, 'utf8'))) {
        if (attributes.get('name') === query) {
            const connectionString = attributes.get('connectionString');
            if (connectionString === undefined || connectionString === '') {
                return { error: `<add name="${query}"> has no connectionString attribute.` };
            }
            return { resolved: maskSecrets(connectionString) };
        }
    }
    return { error: `No <add name="${query}"> in the file.` };
}

/** The names a `#` completion can offer for a config file, in the file's own order. */
export function listReferenceNames(filePath: string, kind: 'json' | 'xml'): string[] {
    try {
        if (kind === 'xml') {
            const names: string[] = [];
            for (const attributes of xmlAddElements(fs.readFileSync(filePath, 'utf8'))) {
                const name = attributes.get('name');
                if (name && attributes.has('connectionString')) {
                    names.push(name);
                }
            }
            return names;
        }

        const root = parseJsonFile(filePath);
        if (typeof root !== 'object' || root === null) {
            return [];
        }
        const connectionStrings = (root as Record<string, unknown>)['ConnectionStrings'];
        if (typeof connectionStrings !== 'object' || connectionStrings === null) {
            return [];
        }
        return Object.entries(connectionStrings)
            .filter(([, value]) => typeof value === 'string')
            .map(([name]) => name);
    } catch {
        return [];
    }
}

function parseJsonFile(filePath: string): unknown {
    const errors: ParseError[] = [];
    const text = withoutByteOrderMark(fs.readFileSync(filePath, 'utf8'));
    const root: unknown = parseJsonc(text, errors, {
        allowTrailingComma: true,
    });
    if (errors.length > 0) {
        throw new Error(`The file did not parse as JSON.`);
    }
    return root;
}

/**
 * Every `<add …>` element's attributes. A regex, not a parser — config files' connectionStrings
 * sections are flat and regular, and the arbitrary-XPath cases this cannot handle are already
 * declined above.
 */
function* xmlAddElements(xml: string): Generator<Map<string, string>> {
    for (const element of xml.matchAll(/<add\b([^>]*?)\/?>/gi)) {
        const attributes = new Map<string, string>();
        for (const attribute of element[1].matchAll(/([\w:.-]+)\s*=\s*"([^"]*)"/g)) {
            attributes.set(attribute[1], decodeEntities(attribute[2]));
        }
        yield attributes;
    }
}

function decodeEntities(value: string): string {
    return value
        .replace(/&quot;/g, '"')
        .replace(/&apos;/g, "'")
        .replace(/&lt;/g, '<')
        .replace(/&gt;/g, '>')
        .replace(/&amp;/g, '&');
}

/** A preview is for the eyes of whoever walks past the screen too. */
function maskSecrets(connectionString: string): string {
    return connectionString.replace(/\b(password|pwd)(\s*=\s*)[^;]*/gi, '$1$2•••');
}
