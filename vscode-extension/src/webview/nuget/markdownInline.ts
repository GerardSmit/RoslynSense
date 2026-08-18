/// <reference path="./dom.ts" />

/**
 * Inline markdown: emphasis, code, links and images.
 *
 * The structural safety rule from `markdown.ts` holds here with one deliberate exception. Links
 * still never carry an `href` — they are `role="link"` spans that ask the host to open a URL, which
 * validates the scheme before it does. Images are now real `<img>` elements, but only when the host
 * is on nuget.org's own badge and CDN allowlist, which is also what the page's CSP permits. Both
 * checks exist: the allowlist so an untrusted host renders as a chip rather than a broken-image
 * box, the CSP so a bug here still cannot fetch anything.
 */
namespace NG {
    export interface InlineContext {
        /** Link reference definitions collected from the document, keyed by lowercase label. */
        refs: Map<string, string>;
        /** The package's project URL, used to resolve relative links. */
        baseUrl: string | null;
    }

    /**
     * One pass, one pattern.
     *
     * Groups, in order: inline link/image (bang, label, url), reference link/image (bang, label,
     * ref), autolink, code, bold, strike, italic, bare URL, shortcut reference. The rules carry no
     * back-references on purpose — numbering them across an alternation this wide is where a
     * pattern like this stops being readable.
     */
    /**
     * A link label, allowing one level of nested brackets.
     *
     * Two shapes in real READMEs need this and `[^\]]*` broke both. `[![Build Status](url)](link)`
     * is how every badge that links somewhere is written, and MailKit's `[SCRAM-SHA-1[-PLUS]](rfc)`
     * puts brackets in the prose. One level is where a regex stops being able to count; it is also
     * as deep as either case goes.
     */
    const Label = String.raw`(?:[^\[\]]|\[[^\[\]]*\])*`;

    const Pattern = new RegExp(
        [
            String.raw`(!?)\[(${Label})\]\(\s*<?([^)\s>]*)>?(?:\s+"[^"]*")?\s*\)`,
            String.raw`(!?)\[(${Label})\]\[([^\]]*)\]`,
            String.raw`<(https?:\/\/[^>\s]+)>`,
            String.raw`\`([^\`]+)\``,
            String.raw`\*\*([^*]+)\*\*`,
            String.raw`(?<!\w)__([^_]+)__(?!\w)`,
            String.raw`~~([^~]+)~~`,
            String.raw`\*([^*\n]+)\*`,
            String.raw`(?<!\w)_([^_\n]+)_(?!\w)`,
            String.raw`(https?:\/\/[^\s<>()[\]"']+[^\s<>()[\]"'.,;:!?])`,
            String.raw`\[(${Label})\]`,
        ].join('|')
    );

    /** A runaway pattern must not hang the panel; no real README has this many inline spans. */
    const MaxSpans = 5000;

    export function inline(target: Node, text: string, ctx: InlineContext): void {
        let rest = text;
        let guard = 0;

        while (rest.length > 0 && guard++ < MaxSpans) {
            const match = Pattern.exec(rest);
            if (!match) {
                break;
            }

            if (match.index > 0) {
                target.appendChild(document.createTextNode(rest.slice(0, match.index)));
            }

            const node = build(match, ctx);
            if (node) {
                target.appendChild(node);
            } else {
                // A shortcut reference with no definition is not a link, it is brackets. Emitting
                // the literal text keeps `[see below]` reading the way its author wrote it.
                target.appendChild(document.createTextNode(match[0]));
            }

            rest = rest.slice(match.index + match[0].length);
        }

        if (rest.length > 0) {
            target.appendChild(document.createTextNode(rest));
        }
    }

    function build(match: RegExpExecArray, ctx: InlineContext): Node | null {
        const [, bang, label, url, refBang, refLabel, refName, autolink, code, bold, boldAlt, strike, italic, italicAlt, bare, shortcut] =
            match;

        if (url !== undefined) {
            return bang === '!' ? image(url, label, ctx) : anchor(url, label || url, ctx);
        }

        if (refName !== undefined) {
            // `[text][]` and `[text][ref]` both resolve against the definitions; an empty second
            // pair means the label is the reference.
            const target = ctx.refs.get((refName || refLabel).toLowerCase());
            if (!target) {
                return null;
            }
            return refBang === '!' ? image(target, refLabel, ctx) : anchor(target, refLabel || target, ctx);
        }

        if (shortcut !== undefined) {
            const target = ctx.refs.get(shortcut.toLowerCase());
            return target ? anchor(target, shortcut, ctx) : null;
        }

        if (autolink !== undefined) {
            return link(autolink, autolink);
        }
        if (bare !== undefined) {
            return link(bare, bare);
        }
        if (code !== undefined) {
            return make('code', 'md-inline-code', code);
        }

        const emphasis = bold ?? boldAlt;
        if (emphasis !== undefined) {
            return nested(make('strong'), emphasis, ctx);
        }
        if (strike !== undefined) {
            return nested(make('s', 'md-strike'), strike, ctx);
        }

        const slanted = italic ?? italicAlt;
        return slanted !== undefined ? nested(make('em'), slanted, ctx) : null;
    }

    /** Emphasis can contain links and code, so its body goes back through the same pass. */
    function nested(host: HTMLElement, text: string, ctx: InlineContext): HTMLElement {
        inline(host, text, ctx);
        return host;
    }

    function anchor(raw: string, label: string, ctx: InlineContext): Node {
        const url = resolve(raw, ctx.baseUrl, false);
        // A link that resolves nowhere useful reads as text rather than pretending to be
        // clickable — the same treatment relative links always had, now only when they really
        // cannot be resolved.
        if (!url) {
            const plain = document.createDocumentFragment();
            inline(plain, label, ctx);
            return plain;
        }

        // The label goes back through the pass rather than in as text, because the linked badge —
        // an image wrapped in a link — is the single most common inline construct in a README
        // header, and it is precisely a link whose label is not text.
        const node = link('', url);
        inline(node, label, ctx);
        return node;
    }

    function image(raw: string, alt: string, ctx: InlineContext): Node {
        const url = resolve(raw, ctx.baseUrl, true);
        if (!url || !isTrustedImage(url)) {
            return chip(alt, url ?? raw);
        }

        const img = make('img', 'md-image') as HTMLImageElement;
        img.alt = alt;
        img.loading = 'lazy';
        img.title = url;
        // A badge service that is up but has nothing to say for this package still leaves a broken
        // box; swapping in the chip is what the chip was always for.
        img.addEventListener('error', () => img.replaceWith(chip(alt, url)));
        img.src = url;
        return img;
    }

    function chip(alt: string, url: string): HTMLElement {
        const node = make('span', 'md-img', `[image: ${alt || 'untitled'}]`);
        node.title = url;
        return node;
    }

    function isTrustedImage(url: string): boolean {
        try {
            const parsed = new URL(url);
            return (
                parsed.protocol === 'https:' &&
                state.settings.trustedImageHosts.some(
                    (host) => parsed.hostname.toLowerCase() === host.toLowerCase()
                )
            );
        } catch {
            return false;
        }
    }

    /**
     * Turns whatever a README wrote into an absolute http(s) URL, or nothing.
     *
     * Relative paths are the common case in a README that was written to be read inside its own
     * repository, and they were rendering as plain text. A GitHub project URL is enough to resolve
     * them properly: `HEAD` is a ref GitHub accepts, so the link lands on the default branch
     * without this having to guess whether it is called `main` or `master`.
     */
    function resolve(raw: string, baseUrl: string | null, isImage: boolean): string | null {
        const value = raw.trim();
        if (value.length === 0) {
            return null;
        }

        if (/^https?:\/\//i.test(value)) {
            return isImage ? rawContent(value) : value;
        }

        // In-page anchors point at a document this panel does not render.
        if (value.startsWith('#') || /^[a-z][a-z0-9+.-]*:/i.test(value)) {
            return null;
        }

        if (!baseUrl) {
            return null;
        }

        try {
            const base = new URL(baseUrl);
            const repository = /^\/([^/]+)\/([^/]+?)(?:\.git)?\/?$/.exec(base.pathname);

            if (base.hostname.toLowerCase() === 'github.com' && repository) {
                const [, owner, name] = repository;
                const path = value.replace(/^\.?\//, '');
                return isImage
                    ? `https://raw.githubusercontent.com/${owner}/${name}/HEAD/${path}`
                    : `https://github.com/${owner}/${name}/blob/HEAD/${path}`;
            }

            const resolved = new URL(value, base);
            return resolved.protocol === 'http:' || resolved.protocol === 'https:'
                ? resolved.href
                : null;
        } catch {
            return null;
        }
    }

    /**
     * The `github.com/o/r/blob/ref/path` form serves an HTML page, not the image. nuget.org rewrites
     * these too, which is why a badge that works on the gallery would otherwise fail here.
     */
    function rawContent(url: string): string {
        const blob = /^https:\/\/github\.com\/([^/]+)\/([^/]+)\/(?:blob|raw)\/(.+)$/i.exec(url);
        return blob
            ? `https://raw.githubusercontent.com/${blob[1]}/${blob[2]}/${blob[3].replace(/\?.*$/, '')}`
            : url;
    }
}
