/**
 * Turning a WebForms page into the HTML it would be with the server code taken out, and finding the
 * JavaScript and CSS in the result.
 *
 * Text in, text out — no editor. That is deliberate: this is the half with the parsing in it, and it
 * is checked by `scripts/checkEmbedded.mjs` under plain node. See `embeddedLanguages.ts` for what
 * the projection is then handed to.
 */

/**
 * What a `type` on a `<script>` may say for the body to still be JavaScript. An absent type is
 * JavaScript; anything else — `text/template`, `application/json`, an Angular-era `text/ng-template`
 * — is a payload the page carries rather than code, and completing JavaScript into it is noise.
 */
const JAVASCRIPT_TYPES = new Set([
    'text/javascript',
    'application/javascript',
    'application/ecmascript',
    'text/ecmascript',
    'module',
    'text/babel',
]);

export type EmbeddedKind = 'javascript' | 'css';

export interface Region {
    /** Offset of the first character of the body, just past the open tag. */
    start: number;
    /** Offset of the `<` that closes it, or the end of the file. */
    end: number;
    kind: EmbeddedKind;
}

/**
 * The page as HTML: every server construct replaced by spaces, everything else left where it is.
 *
 * Line breaks survive blanking so that a position is the same position in both texts. What is left
 * where a `<%= Url %>` stood is whitespace, which is valid wherever the expression was — inside a
 * string literal, an attribute value or a statement — and that is the point: the JavaScript around
 * it still parses.
 */
export function project(text: string): string {
    const out = text.split('');

    const blank = (start: number, end: number) => {
        for (let i = Math.max(0, start); i < Math.min(end, out.length); i++) {
            if (out[i] !== '\n' && out[i] !== '\r') {
                out[i] = ' ';
            }
        }
    };

    // `<%@ %>` directives, `<% %>` blocks, `<%= %>` and `<%# %>` expressions, `<%$ %>` builders and
    // `<%-- --%>` comments are all one shape once the terminator is picked.
    for (let i = text.indexOf('<%'); i >= 0; i = text.indexOf('<%', i)) {
        const comment = text.startsWith('<%--', i);
        const terminator = comment ? '--%>' : '%>';
        const found = text.indexOf(terminator, i + (comment ? 4 : 2));
        const end = found < 0 ? text.length : found + terminator.length;

        blank(i, end);
        i = end;
    }

    // Server scripts are looked for in the text as it stands *after* the islands went, never in the
    // original. A `<script runat="server">` written inside a `<%-- --%>` comment, or quoted in a
    // code block, is not an element — and pairing that phantom open tag with the next real
    // `</script>` blanks the live JavaScript between them and takes the whole region with it.
    const withoutIslands = out.join('');

    // The tags around a server script are what say it is C#, so the whole element goes; leaving
    // them would have the HTML service read the class inside as JavaScript.
    for (const element of serverScripts(withoutIslands)) {
        blank(element.start, element.end);
    }

    return out.join('');
}

/**
 * Every client-side `<script>` and `<style>` body in a page.
 *
 * Scanned over the projection rather than the page, which is what keeps a `<script>` written inside
 * a `<%-- --%>` comment — or quoted in a server code block — from being read as one. By the time
 * the scan runs those are spaces.
 */
export function regions(text: string): Region[] {
    return scan(project(text));
}

/** The same, for a caller that already has the projection and should not pay for a second one. */
export function scan(projected: string): Region[] {
    const found: Region[] = [];

    for (const name of ['script', 'style'] as const) {
        for (const tag of openTags(projected, name)) {
            const attributes = projected.slice(tag.start, tag.bodyStart);
            if (name === 'script' && !isClientScript(attributes)) {
                continue;
            }

            found.push({
                start: tag.bodyStart,
                end: closeOf(projected, name, tag.bodyStart).bodyEnd,
                kind: name === 'style' ? 'css' : 'javascript',
            });
        }
    }

    return found;
}

/** The `<script runat="server">` elements, tags included. */
function serverScripts(text: string): Array<{ start: number; end: number }> {
    const found: Array<{ start: number; end: number }> = [];

    for (const tag of openTags(text, 'script')) {
        if (isClientScript(text.slice(tag.start, tag.bodyStart))) {
            continue;
        }

        found.push({ start: tag.start, end: closeOf(text, 'script', tag.bodyStart).end });
    }

    return found;
}

/**
 * Whether a `<script>`'s open tag says its body is JavaScript. `runat="server"` makes it C#;
 * a `type` naming anything but a script makes it data.
 */
function isClientScript(openTag: string): boolean {
    if (/\brunat\s*=\s*("|')?server\1?/i.test(openTag)) {
        return false;
    }

    const type = /\btype\s*=\s*("([^"]*)"|'([^']*)'|([^\s>]+))/i.exec(openTag);
    if (!type) {
        return true;
    }

    const value = (type[2] ?? type[3] ?? type[4] ?? '').trim().toLowerCase();
    return value.length === 0 || JAVASCRIPT_TYPES.has(value);
}

/** Every open tag with the given name, and where its body starts. */
function openTags(text: string, name: string): Array<{ start: number; bodyStart: number }> {
    const found: Array<{ start: number; bodyStart: number }> = [];
    const opening = new RegExp(`<${name}\\b`, 'gi');

    let match: RegExpExecArray | null;
    while ((match = opening.exec(text)) !== null) {
        const bodyStart = endOfOpenTag(text, match.index + match[0].length);
        if (bodyStart < 0) {
            break;
        }

        // A self-closing `<script />` has no body worth entering, and skipping past the tag keeps
        // the scan from pairing it with somebody else's closer.
        if (text[bodyStart - 2] !== '/') {
            found.push({ start: match.index, bodyStart });
        }

        opening.lastIndex = bodyStart;
    }

    return found;
}

/**
 * Where an open tag ends, counting from just after the tag name. Quoted values are skipped whole,
 * because an attribute may carry a `>` — `onclick="if (a > b) f()"` is a tag that ends later than
 * the first `>` in it.
 */
function endOfOpenTag(text: string, from: number): number {
    let quote: string | undefined;

    for (let i = from; i < text.length; i++) {
        const c = text[i];

        if (quote) {
            if (c === quote) {
                quote = undefined;
            }
            continue;
        }

        if (c === '"' || c === '\'') {
            quote = c;
        } else if (c === '>') {
            return i + 1;
        }
    }

    return -1;
}

/** The closing tag for a body that starts at `bodyStart`. */
function closeOf(text: string, name: string, bodyStart: number): { bodyEnd: number; end: number } {
    const closing = new RegExp(`</${name}\\s*>`, 'i');
    const rest = text.slice(bodyStart);
    const match = closing.exec(rest);

    return match
        ? { bodyEnd: bodyStart + match.index, end: bodyStart + match.index + match[0].length }
        : { bodyEnd: text.length, end: text.length };
}
