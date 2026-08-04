/// <reference path="./dom.ts" />

/**
 * A small markdown renderer for package READMEs.
 *
 * READMEs are written by strangers, so the safety property here is structural rather than a matter
 * of sanitizing correctly: the only thing this code can produce is `document.createElement` calls
 * from the fixed tag list below, and it never sets `href`, `src`, `style` or any event-handler
 * attribute. No input — however crafted — can make it emit a script, an iframe, or a navigable
 * link. That is a claim you can check by reading it, which a sanitizer never is.
 *
 * The subset is deliberate: headings, paragraphs, code, emphasis, lists, links, block quotes and
 * rules. Tables, raw HTML and reference links render as their own literal text rather than
 * silently disappearing.
 */
namespace NG {
    const MaxSourceLength = 200_000;

    export function renderMarkdown(source: string, plain: boolean): HTMLElement {
        const root = make('div', 'md');

        if (plain) {
            root.appendChild(make('pre', 'md-raw', source));
            return root;
        }

        const truncated = source.length > MaxSourceLength;
        const lines = (truncated ? source.slice(0, MaxSourceLength) : source).split(/\r?\n/);

        let index = 0;
        let list: HTMLElement | null = null;

        const endList = () => {
            list = null;
        };

        while (index < lines.length) {
            const line = lines[index];

            // Fenced code: everything until the closing fence is literal text.
            const fence = /^\s*(```|~~~)(.*)$/.exec(line);
            if (fence) {
                endList();
                const marker = fence[1];
                const body: string[] = [];
                index++;
                while (index < lines.length && !lines[index].trimStart().startsWith(marker)) {
                    body.push(lines[index]);
                    index++;
                }
                index++;
                const pre = make('pre', 'md-code');
                pre.appendChild(make('code', undefined, body.join('\n')));
                root.appendChild(pre);
                continue;
            }

            if (line.trim().length === 0) {
                endList();
                index++;
                continue;
            }

            if (/^\s{4,}\S/.test(line)) {
                endList();
                const body: string[] = [];
                while (index < lines.length && (/^\s{4,}/.test(lines[index]) || lines[index].trim() === '')) {
                    body.push(lines[index].replace(/^\s{4}/, ''));
                    index++;
                }
                const pre = make('pre', 'md-code');
                pre.appendChild(make('code', undefined, body.join('\n').trimEnd()));
                root.appendChild(pre);
                continue;
            }

            if (/^\s*(?:-\s*){3,}$|^\s*(?:\*\s*){3,}$|^\s*(?:_\s*){3,}$/.test(line)) {
                endList();
                root.appendChild(make('hr'));
                index++;
                continue;
            }

            const heading = /^(#{1,6})\s+(.*)$/.exec(line);
            if (heading) {
                endList();
                const level = Math.min(heading[1].length, 6);
                const node = make(`h${level}` as 'h1', 'md-h');
                inline(node, heading[2].trim());
                root.appendChild(node);
                index++;
                continue;
            }

            const quote = /^\s*>\s?(.*)$/.exec(line);
            if (quote) {
                endList();
                const node = make('blockquote', 'md-quote');
                inline(node, quote[1]);
                root.appendChild(node);
                index++;
                continue;
            }

            const bullet = /^\s*[-*+]\s+(.*)$/.exec(line);
            const ordered = /^\s*\d+[.)]\s+(.*)$/.exec(line);
            if (bullet || ordered) {
                const wanted = bullet ? 'ul' : 'ol';
                if (!list || list.tagName.toLowerCase() !== wanted) {
                    list = make(wanted, 'md-list');
                    root.appendChild(list);
                }
                const item = make('li');
                inline(item, (bullet ?? ordered)![1]);
                list.appendChild(item);
                index++;
                continue;
            }

            endList();
            const paragraph = make('p', 'md-p');
            const body: string[] = [];
            while (
                index < lines.length &&
                lines[index].trim().length > 0 &&
                !/^\s*(#{1,6}\s|>|[-*+]\s|\d+[.)]\s|```|~~~)/.test(lines[index])
            ) {
                body.push(lines[index].trim());
                index++;
            }
            inline(paragraph, body.join(' '));
            root.appendChild(paragraph);
        }

        if (truncated) {
            root.appendChild(make('p', 'muted', 'The README was truncated.'));
        }

        return root;
    }

    /**
     * Inline spans. The image case deliberately produces a chip rather than an `<img>`: the CSP
     * blocks every remote image, so a badge-heavy README would otherwise render as a wall of
     * broken-image boxes. Proxying them instead would turn the daemon into a fetcher driven by
     * package-authored URLs, which is not a trade worth making for a badge.
     */
    function inline(target: HTMLElement, text: string): void {
        const pattern = /(!?)\[([^\]]*)\]\(([^)\s]+)(?:\s+"[^"]*")?\)|`([^`]+)`|(\*\*|__)(.+?)\5|(\*|_)(.+?)\7/;

        let rest = text;
        let guard = 0;

        while (rest.length > 0 && guard++ < 2000) {
            const match = pattern.exec(rest);
            if (!match) {
                break;
            }

            if (match.index > 0) {
                target.appendChild(document.createTextNode(rest.slice(0, match.index)));
            }

            if (match[3] !== undefined) {
                const label = match[2];
                const url = match[3];
                if (match[1] === '!') {
                    const chip = make('span', 'md-img', `[image: ${label || 'untitled'}]`);
                    chip.title = url;
                    target.appendChild(chip);
                } else if (/^https?:\/\//i.test(url)) {
                    target.appendChild(link(label || url, url));
                } else {
                    // A relative link resolves to nothing meaningful outside the repository, so it
                    // reads as text rather than pretending to be clickable.
                    target.appendChild(document.createTextNode(label || url));
                }
            } else if (match[4] !== undefined) {
                target.appendChild(make('code', 'md-inline-code', match[4]));
            } else if (match[6] !== undefined) {
                target.appendChild(make('strong', undefined, match[6]));
            } else if (match[8] !== undefined) {
                target.appendChild(make('em', undefined, match[8]));
            }

            rest = rest.slice(match.index + match[0].length);
        }

        if (rest.length > 0) {
            target.appendChild(document.createTextNode(rest));
        }
    }
}
