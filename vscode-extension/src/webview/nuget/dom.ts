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
