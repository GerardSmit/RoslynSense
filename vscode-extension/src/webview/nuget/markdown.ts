/// <reference path="./markdownInline.ts" />
/// <reference path="./highlight.ts" />

/**
 * A small markdown renderer for package READMEs.
 *
 * READMEs are written by strangers, so the safety property here is structural rather than a matter
 * of sanitizing correctly: the only thing this code can produce is `document.createElement` calls
 * from the fixed tag list below, and it never sets `href`, `style` or any event-handler attribute.
 * No input — however crafted — can make it emit a script, an iframe, or a navigable link. That is a
 * claim you can check by reading it, which a sanitizer never is.
 *
 * `src` is the one attribute that is now set, and only in `markdownInline.ts`, only for a host on
 * nuget.org's own image allowlist, which is also the only thing the page's CSP permits.
 *
 * The subset covers what .NET READMEs actually use: headings, paragraphs, code with highlighting,
 * emphasis, nested lists, links (inline, reference and bare), images, block quotes, rules and
 * tables. Raw HTML other than comments still renders as its own literal text rather than silently
 * disappearing; HTML comments are dropped, because the Microsoft package templates ship with
 * instructional ones that were showing up in the panel as prose.
 */
namespace NG {
    const MaxSourceLength = 200_000;

    export function renderMarkdown(
        source: string,
        plain: boolean,
        projectUrl: string | null = null
    ): HTMLElement {
        const root = make('div', 'md');

        if (plain) {
            root.appendChild(make('pre', 'md-raw', source));
            return root;
        }

        const truncated = source.length > MaxSourceLength;
        const text = stripComments(truncated ? source.slice(0, MaxSourceLength) : source);

        const ctx: InlineContext = { refs: new Map(), baseUrl: projectUrl };
        const lines = takeDefinitions(text.split(/\r?\n/), ctx.refs);

        renderBlocks(root, lines, ctx);

        if (truncated) {
            root.appendChild(make('p', 'muted', 'The README was truncated.'));
        }
        return root;
    }

    /**
     * HTML comments, gone before anything else looks at the text.
     *
     * The `dotnet new` README template is full of them — "<!-- A description of the package -->" —
     * and every package that shipped the template unedited was rendering those instructions to the
     * reader as if they were documentation.
     *
     * Fenced code is exempt: a comment in an XML sample is the sample, and stripping it would edit
     * the snippet the author meant to show. That is why this walks lines instead of running one
     * replace over the whole document.
     */
    function stripComments(source: string): string {
        const out: string[] = [];
        let prose: string[] = [];
        let fenced = false;

        // Buffered rather than stripped line by line, because a comment may span several lines.
        const flush = () => {
            if (prose.length > 0) {
                out.push(prose.join('\n').replace(/<!--[\s\S]*?-->/g, ''));
                prose = [];
            }
        };

        for (const line of source.split(/\r?\n/)) {
            if (/^\s{0,3}(?:```|~~~)/.test(line)) {
                flush();
                fenced = !fenced;
                out.push(line);
            } else if (fenced) {
                out.push(line);
            } else {
                prose.push(line);
            }
        }

        flush();
        return out.join('\n');
    }

    /**
     * Pulls `[label]: url` definitions out and returns the remaining lines.
     *
     * Reference links are how nearly every Azure SDK README writes its header row — the
     * `[Source code][source] | [Package (NuGet)][package]` line — and with no definitions
     * collected, all of it rendered as literal brackets.
     */
    function takeDefinitions(lines: string[], refs: Map<string, string>): string[] {
        const kept: string[] = [];
        let fenced = false;

        for (const line of lines) {
            if (/^\s{0,3}(?:```|~~~)/.test(line)) {
                fenced = !fenced;
            }

            const definition = fenced
                ? null
                : /^\s{0,3}\[([^\]^][^\]]*)\]:\s*<?([^\s>]+)>?(?:\s+["'(].*)?\s*$/.exec(line);

            if (definition) {
                refs.set(definition[1].toLowerCase(), definition[2]);
                continue;
            }
            kept.push(line);
        }

        return kept;
    }

    function renderBlocks(root: HTMLElement, lines: string[], ctx: InlineContext): void {
        let index = 0;
        let lists: { indent: number; el: HTMLElement }[] = [];

        const endLists = () => {
            lists = [];
        };

        while (index < lines.length) {
            const line = lines[index];

            // Fenced code: everything until the closing fence is literal text.
            const fence = /^\s*(```|~~~)(.*)$/.exec(line);
            if (fence) {
                endLists();
                const marker = fence[1];
                const body: string[] = [];
                index++;
                while (index < lines.length && !lines[index].trimStart().startsWith(marker)) {
                    body.push(lines[index]);
                    index++;
                }
                index++;
                root.appendChild(codeBlock(body.join('\n'), fenceLanguage(fence[2])));
                continue;
            }

            if (line.trim().length === 0) {
                endLists();
                index++;
                continue;
            }

            // Indented code, but only outside a list: four spaces inside one is a continuation.
            if (/^\s{4,}\S/.test(line) && lists.length === 0) {
                const body: string[] = [];
                while (index < lines.length && (/^\s{4,}/.test(lines[index]) || lines[index].trim() === '')) {
                    body.push(lines[index].replace(/^\s{4}/, ''));
                    index++;
                }
                root.appendChild(codeBlock(body.join('\n').trimEnd(), ''));
                continue;
            }

            if (/^\s*(?:-\s*){3,}$|^\s*(?:\*\s*){3,}$|^\s*(?:_\s*){3,}$/.test(line)) {
                endLists();
                root.appendChild(make('hr'));
                index++;
                continue;
            }

            const heading = /^(#{1,6})\s+(.*)$/.exec(line);
            if (heading) {
                endLists();
                const level = Math.min(heading[1].length, 6);
                const node = make(`h${level}` as 'h1', 'md-h');
                inline(node, heading[2].trim().replace(/\s+#+\s*$/, ''), ctx);
                root.appendChild(node);
                index++;
                continue;
            }

            // A pipe table needs its delimiter row to identify it, so it is recognised two lines
            // at a time. Without this the rows fall into the paragraph branch below and its
            // line-joining turns the whole table into one run-on sentence.
            if (index + 1 < lines.length && isDelimiterRow(lines[index + 1]) && line.includes('|')) {
                endLists();
                const [node, next] = table(lines, index, ctx);
                root.appendChild(node);
                index = next;
                continue;
            }

            const quote = /^\s*>\s?(.*)$/.exec(line);
            if (quote) {
                endLists();
                const node = make('blockquote', 'md-quote');
                const body: string[] = [];
                while (index < lines.length && /^\s*>/.test(lines[index])) {
                    body.push(lines[index].replace(/^\s*>\s?/, ''));
                    index++;
                }
                inline(node, body.join(' '), ctx);
                root.appendChild(node);
                continue;
            }

            const item = /^(\s*)(?:([-*+])|(\d+)[.)])\s+(.*)$/.exec(line);
            if (item) {
                lists = pushItem(root, lists, item, ctx);
                index++;
                continue;
            }

            endLists();
            const paragraph = make('p', 'md-p');
            const body: string[] = [];
            while (
                index < lines.length &&
                lines[index].trim().length > 0 &&
                !/^\s*(#{1,6}\s|>|[-*+]\s|\d+[.)]\s|```|~~~)/.test(lines[index]) &&
                !(index + 1 < lines.length && isDelimiterRow(lines[index + 1]))
            ) {
                body.push(lines[index].trim());
                index++;
            }
            // A line that broke the loop without being consumed would spin forever.
            if (body.length === 0) {
                body.push(lines[index].trim());
                index++;
            }
            inline(paragraph, body.join(' '), ctx);
            root.appendChild(paragraph);
        }
    }

    function codeBlock(code: string, language: string): HTMLElement {
        const pre = make('pre', 'md-code');
        const node = make('code');
        if (language) {
            node.dataset.lang = language;
        }
        node.appendChild(highlight(code, language));
        pre.appendChild(node);
        return pre;
    }

    /**
     * Adds one list item, opening and closing nested lists as the indentation moves.
     *
     * Nesting is by indentation alone, which is what READMEs actually write. A flat renderer made
     * every sub-bullet a sibling, so an options list read as one long undifferentiated run.
     */
    function pushItem(
        root: HTMLElement,
        lists: { indent: number; el: HTMLElement }[],
        match: RegExpExecArray,
        ctx: InlineContext
    ): { indent: number; el: HTMLElement }[] {
        const indent = match[1].replace(/\t/g, '    ').length;
        const wanted = match[2] ? 'ul' : 'ol';

        while (lists.length > 0 && indent < lists[lists.length - 1].indent) {
            lists.pop();
        }

        let current = lists[lists.length - 1];

        if (!current || indent > current.indent) {
            const nested = make(wanted, 'md-list');
            // A nested list belongs inside the item above it, not beside it.
            const parent = current?.el.lastElementChild;
            (parent ?? current?.el ?? root).appendChild(nested);
            lists.push({ indent, el: nested });
            current = lists[lists.length - 1];
        } else if (current.el.tagName.toLowerCase() !== wanted) {
            const replacement = make(wanted, 'md-list');
            current.el.after(replacement);
            lists[lists.length - 1] = { indent, el: replacement };
            current = lists[lists.length - 1];
        }

        const li = make('li');
        inline(li, match[4], ctx);
        current.el.appendChild(li);
        return lists;
    }

    /** `|---|:--:|---:|` — the row that turns the lines around it into a table. */
    function isDelimiterRow(line: string): boolean {
        return /^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$/.test(line) && line.includes('-');
    }

    function table(
        lines: string[],
        start: number,
        ctx: InlineContext
    ): [HTMLElement, number] {
        const headers = cells(lines[start]);
        const alignments = cells(lines[start + 1]).map(alignmentOf);

        const node = make('table', 'md-table');
        const head = make('thead');
        const headRow = make('tr');
        headers.forEach((cell, column) => {
            const th = make('th', alignments[column]);
            inline(th, cell, ctx);
            headRow.appendChild(th);
        });
        head.appendChild(headRow);
        node.appendChild(head);

        const body = make('tbody');
        let index = start + 2;
        while (index < lines.length && lines[index].includes('|') && lines[index].trim().length > 0) {
            const row = make('tr');
            cells(lines[index]).forEach((cell, column) => {
                const td = make('td', alignments[column]);
                inline(td, cell, ctx);
                row.appendChild(td);
            });
            body.appendChild(row);
            index++;
        }
        node.appendChild(body);

        // Its own scroll container: a six-column table must not stretch the details pane, and the
        // pane must never scroll sideways as a whole.
        const wrap = make('div', 'md-table-wrap');
        wrap.appendChild(node);
        return [wrap, index];
    }

    function cells(line: string): string[] {
        return line
            .trim()
            .replace(/^\|/, '')
            .replace(/\|\s*$/, '')
            // An escaped pipe is content, not a cell boundary.
            .split(/(?<!\\)\|/)
            .map((cell) => cell.replace(/\\\|/g, '|').trim());
    }

    /** A class rather than an inline style: the no-`style`-attribute rule holds. */
    function alignmentOf(spec: string): string {
        const left = spec.startsWith(':');
        const right = spec.endsWith(':');
        return left && right ? 'md-center' : right ? 'md-right' : '';
    }
}
