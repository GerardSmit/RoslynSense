/**
 * A small, honest tokenizer for the preview pane — comments, strings, numbers, keywords and a
 * PascalCase heuristic, not a grammar. The webview has no access to the editor's TextMate
 * engine, and shipping one for a 40-line preview would outweigh the preview; this covers the
 * languages a .NET solution's search hits actually land in (C#, XML-shaped files, JSON).
 */
namespace SE {
    export interface Token {
        text: string;
        /** A `tok-*` CSS class, or null for plain text. */
        cls: string | null;
    }

    /** Tokenizes a whole snippet so block comments survive line breaks. */
    export function highlightLines(lines: string[], languageId: string): Token[][] {
        switch (languageId) {
            case 'csharp':
            case 'cs-embedded':
                return csharp(lines);
            case 'xml':
            case 'msbuild':
            case 'resx':
            case 'webforms':
            case 'html':
                return xml(lines);
            case 'json':
            case 'jsonc':
                return json(lines);
            default:
                return lines.map((line) => [{ text: line, cls: null }]);
        }
    }

    const CSHARP_KEYWORDS = new Set(
        (
            'abstract as base bool break byte case catch char checked class const continue decimal ' +
            'default delegate do double else enum event explicit extern false finally fixed float for ' +
            'foreach goto if implicit in int interface internal is lock long namespace new null object ' +
            'operator out override params private protected public readonly record ref return sbyte sealed ' +
            'short sizeof stackalloc static string struct switch this throw true try typeof uint ulong ' +
            'unchecked unsafe ushort using var virtual void volatile while yield async await when where ' +
            'partial get set init value nameof with required scoped file'
        ).split(' ')
    );

    function csharp(lines: string[]): Token[][] {
        let inBlockComment = false;

        return lines.map((line) => {
            const tokens: Token[] = [];
            let i = 0;

            const push = (text: string, cls: string | null) => {
                if (text.length > 0) {
                    tokens.push({ text, cls });
                }
            };

            while (i < line.length) {
                if (inBlockComment) {
                    const end = line.indexOf('*/', i);
                    if (end < 0) {
                        push(line.slice(i), 'tok-com');
                        i = line.length;
                    } else {
                        push(line.slice(i, end + 2), 'tok-com');
                        i = end + 2;
                        inBlockComment = false;
                    }
                    continue;
                }

                const ch = line[i];

                if (ch === '/' && line[i + 1] === '/') {
                    push(line.slice(i), 'tok-com');
                    break;
                }

                if (ch === '/' && line[i + 1] === '*') {
                    inBlockComment = true;
                    continue;
                }

                if (ch === '"' || (ch === '$' && line[i + 1] === '"') || (ch === '@' && line[i + 1] === '"')) {
                    const start = i;
                    while (i < line.length && line[i] !== '"') i++; // the prefix
                    i++;
                    while (i < line.length) {
                        if (line[i] === '\\' && line[i + 1] !== undefined) {
                            i += 2;
                            continue;
                        }
                        if (line[i] === '"') {
                            i++;
                            break;
                        }
                        i++;
                    }
                    push(line.slice(start, i), 'tok-str');
                    continue;
                }

                if (ch === "'") {
                    const start = i;
                    i++;
                    while (i < line.length) {
                        if (line[i] === '\\' && line[i + 1] !== undefined) {
                            i += 2;
                            continue;
                        }
                        if (line[i] === "'") {
                            i++;
                            break;
                        }
                        i++;
                    }
                    push(line.slice(start, i), 'tok-str');
                    continue;
                }

                if (/\d/.test(ch)) {
                    const start = i;
                    while (i < line.length && /[\w.]/.test(line[i])) i++;
                    push(line.slice(start, i), 'tok-num');
                    continue;
                }

                if (/[A-Za-z_@]/.test(ch)) {
                    const start = i;
                    if (ch === '@') i++;
                    while (i < line.length && /\w/.test(line[i])) i++;
                    const word = line.slice(start, i);
                    const bare = word.startsWith('@') ? word.slice(1) : word;

                    if (CSHARP_KEYWORDS.has(bare)) {
                        push(word, 'tok-kw');
                    } else if (line[i] === '(') {
                        push(word, 'tok-meth');
                    } else if (/^[A-Z]/.test(bare)) {
                        push(word, 'tok-type');
                    } else {
                        push(word, null);
                    }
                    continue;
                }

                // Consume a run of anything else in one token rather than one per character.
                const start = i;
                while (i < line.length && !/[A-Za-z_@\d"'/]/.test(line[i])) i++;
                if (i === start) i++;
                push(line.slice(start, i), null);
            }

            return tokens;
        });
    }

    function xml(lines: string[]): Token[][] {
        let inComment = false;

        return lines.map((line) => {
            const tokens: Token[] = [];
            let i = 0;

            const push = (text: string, cls: string | null) => {
                if (text.length > 0) {
                    tokens.push({ text, cls });
                }
            };

            while (i < line.length) {
                if (inComment) {
                    const end = line.indexOf('-->', i);
                    if (end < 0) {
                        push(line.slice(i), 'tok-com');
                        i = line.length;
                    } else {
                        push(line.slice(i, end + 3), 'tok-com');
                        i = end + 3;
                        inComment = false;
                    }
                    continue;
                }

                if (line.startsWith('<!--', i)) {
                    inComment = true;
                    continue;
                }

                if (line[i] === '<') {
                    // <tag, </tag, <%@ — the name is one token, attributes follow.
                    const tag = /^<\/?[\w:%@.-]*/.exec(line.slice(i));
                    if (tag) {
                        push(tag[0], 'tok-kw');
                        i += tag[0].length;
                        continue;
                    }
                }

                if (line[i] === '"' || line[i] === "'") {
                    const quote = line[i];
                    const end = line.indexOf(quote, i + 1);
                    const stop = end < 0 ? line.length : end + 1;
                    push(line.slice(i, stop), 'tok-str');
                    i = stop;
                    continue;
                }

                if (/[\w:-]/.test(line[i])) {
                    const start = i;
                    while (i < line.length && /[\w:.-]/.test(line[i])) i++;
                    // An attribute name sits before '='; anything else is text content.
                    const isAttribute = /^\s*=/.test(line.slice(i));
                    push(line.slice(start, i), isAttribute ? 'tok-attr' : null);
                    continue;
                }

                const start = i;
                while (i < line.length && !/[<"'\w]/.test(line[i])) i++;
                if (i === start) i++;
                push(line.slice(start, i), null);
            }

            return tokens;
        });
    }

    function json(lines: string[]): Token[][] {
        return lines.map((line) => {
            const tokens: Token[] = [];
            let i = 0;

            const push = (text: string, cls: string | null) => {
                if (text.length > 0) {
                    tokens.push({ text, cls });
                }
            };

            while (i < line.length) {
                const ch = line[i];

                if (ch === '"') {
                    const start = i;
                    i++;
                    while (i < line.length) {
                        if (line[i] === '\\') {
                            i += 2;
                            continue;
                        }
                        if (line[i] === '"') {
                            i++;
                            break;
                        }
                        i++;
                    }
                    const isKey = /^\s*:/.test(line.slice(i));
                    push(line.slice(start, i), isKey ? 'tok-attr' : 'tok-str');
                    continue;
                }

                if (/[\d-]/.test(ch) && /[\d]/.test(line[i + 1] ?? ch)) {
                    const start = i;
                    i++;
                    while (i < line.length && /[\d.eE+-]/.test(line[i])) i++;
                    push(line.slice(start, i), 'tok-num');
                    continue;
                }

                if (/[a-z]/.test(ch)) {
                    const start = i;
                    while (i < line.length && /[a-z]/.test(line[i])) i++;
                    const word = line.slice(start, i);
                    push(word, word === 'true' || word === 'false' || word === 'null' ? 'tok-kw' : null);
                    continue;
                }

                if (ch === '/' && line[i + 1] === '/') {
                    push(line.slice(i), 'tok-com');
                    break;
                }

                const start = i;
                while (i < line.length && !/["\da-z/-]/.test(line[i])) i++;
                if (i === start) i++;
                push(line.slice(start, i), null);
            }

            return tokens;
        });
    }
}
