/**
 * DOM helpers for the panel.
 *
 * Everything package-authored reaches the document through `textContent`. There is no code path in
 * this panel that assigns `innerHTML` from data, which is what makes the CSP a second line of
 * defence rather than the only one.
 */
namespace NG {
    export function el<T extends HTMLElement>(id: string): T {
        return document.getElementById(id) as T;
    }

    export function make<K extends keyof HTMLElementTagNameMap>(
        tag: K,
        className?: string,
        text?: string
    ): HTMLElementTagNameMap[K] {
        const node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined) {
            node.textContent = text;
        }
        return node;
    }

    export function banner(kind: 'warn' | 'error' | 'info', text: string): HTMLDivElement {
        return make('div', `banner ${kind}`, text);
    }

    /** A link that never carries an href, so a `javascript:` or `command:` URL has nowhere to go. */
    export function link(label: string, url: string): HTMLElement {
        const node = make('span', 'md-link', label);
        node.setAttribute('role', 'link');
        node.tabIndex = 0;
        // The real destination is visible before the click, not after it.
        node.title = url;
        node.addEventListener('click', () => post({ type: 'openExternal', url }));
        node.addEventListener('keydown', (event) => {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                post({ type: 'openExternal', url });
            }
        });
        return node;
    }

    /**
     * A link to another package in this panel. Nothing leaves the webview; the details pane
     * navigates, and the back button knows about it.
     */
    export function packageLink(id: string, label?: string): HTMLElement {
        const node = make('span', 'md-link pkg-link', label ?? id);
        node.setAttribute('role', 'link');
        node.tabIndex = 0;
        node.title = `Show ${id}`;
        const open = (event: Event) => {
            event.stopPropagation();
            openPackage(id);
        };
        node.addEventListener('click', open);
        node.addEventListener('keydown', (event) => {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                open(event);
            }
        });
        return node;
    }

    /**
     * Bare http(s) URLs in otherwise plain text.
     *
     * The deprecation notices NuGet attaches end "you can learn more about it from <url>", which is
     * the one part of the banner worth clicking and was rendering as dead text. Trailing sentence
     * punctuation is left outside the link — a URL at the end of a sentence is far more common than
     * one that genuinely ends in a full stop.
     */
    export const UrlPattern = /https?:\/\/[^\s<>()[\]"']+[^\s<>()[\]"'.,;:!?]/g;

    export function linkify(text: string): DocumentFragment {
        const fragment = document.createDocumentFragment();
        let last = 0;

        for (const match of text.matchAll(UrlPattern)) {
            if (match.index > last) {
                fragment.appendChild(document.createTextNode(text.slice(last, match.index)));
            }
            fragment.appendChild(link(match[0], match[0]));
            last = match.index + match[0].length;
        }

        if (last < text.length) {
            fragment.appendChild(document.createTextNode(text.slice(last)));
        }
        return fragment;
    }

    /**
     * `== null`, not `=== null`: the server's serializer omits null properties entirely, so a
     * package with no download count arrives with no `downloads` key at all rather than with a
     * null one. The strict check let `undefined` through to `toLocaleString`.
     */
    export function formatCount(value: number | null | undefined): string | null {
        return value == null ? null : `${value.toLocaleString()} downloads`;
    }

    export function formatDate(iso: string | null): string | null {
        if (!iso) {
            return null;
        }
        const parsed = new Date(iso);
        return Number.isNaN(parsed.getTime())
            ? null
            : parsed.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
    }

    export function severityName(severity: number): string {
        return ['low', 'moderate', 'high', 'critical'][Math.min(Math.max(severity, 0), 3)];
    }
}
