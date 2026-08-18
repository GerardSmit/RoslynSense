/// <reference path="../../search/protocol.d.ts" />
/// <reference path="./highlight.ts" />

/**
 * The Search Everywhere popup: a query box, Rider's tab row, the server-ranked list, and a
 * preview of the selection. All rendering is `createElement` + `textContent` — no result text
 * ever becomes markup.
 *
 * The server owns the ranking. This script never re-sorts and never filters; stale responses are
 * dropped by request id, which is what keeps a slow early query from overwriting a fast late one.
 */

declare function acquireVsCodeApi(): { postMessage(message: SearchMsg.ToHost): void };

namespace SE {
    const vscode = acquireVsCodeApi();

    /** Long enough that a fast typist sends one request per word, short enough to feel live. */
    const DEBOUNCE_MS = 90;
    const PREVIEW_DEBOUNCE_MS = 120;

    const TABS: SearchMsg.Tab[] = ['all', 'classes', 'files', 'symbols', 'actions', 'text'];

    type Row =
        | { kind: 'symbol'; item: SearchMsg.SymbolItem }
        | { kind: 'text'; item: SearchMsg.TextItem }
        | { kind: 'action'; item: SearchMsg.ActionItem }
        | { kind: 'recent'; item: SearchMsg.RecentItem };

    let activeTab: SearchMsg.Tab = 'all';
    let rows: Row[] = [];
    let selected = -1;
    let recent: SearchMsg.RecentItem[] = [];
    let searchId = 0;
    let previewId = 0;
    let searchTimer: number | undefined;
    let previewTimer: number | undefined;

    const query = document.getElementById('query') as HTMLInputElement;
    const nonSolution = document.getElementById('non-solution') as HTMLInputElement;
    const results = document.getElementById('results') as HTMLUListElement;
    const preview = document.getElementById('preview') as HTMLElement;
    const previewLines = document.getElementById('preview-lines') as HTMLElement;
    const statusPath = document.getElementById('status-path') as HTMLElement;
    const openSplit = document.getElementById('open-split') as HTMLButtonElement;
    const progress = document.getElementById('progress') as HTMLElement;

    // ---- Searching -------------------------------------------------------------------

    function requestSearch(immediate = false): void {
        if (searchTimer !== undefined) {
            clearTimeout(searchTimer);
        }
        // Claimed before the debounce, not inside it: a tab switch must orphan the previous
        // tab's in-flight reply immediately, or it renders under the new tab for a beat.
        const id = ++searchId;
        searchTimer = window.setTimeout(
            () => {
                const value = query.value.trim();
                if (value.length === 0) {
                    progress.classList.remove('busy');
                    showRecent();
                    return;
                }
                progress.classList.add('busy');
                vscode.postMessage({
                    type: 'search',
                    id,
                    tab: activeTab,
                    query: value,
                    includeNonSolution: nonSolution.checked,
                });
            },
            immediate ? 0 : DEBOUNCE_MS
        );
    }

    function showRecent(): void {
        rows = recent.map((item) => ({ kind: 'recent', item }) as Row);
        render(rows.length === 0 ? 'Start typing to search.' : undefined);
        select(rows.length > 0 ? 0 : -1);
    }

    // ---- Rendering -------------------------------------------------------------------

    function render(placeholder?: string, truncated = false): void {
        results.replaceChildren();

        if (placeholder !== undefined) {
            const li = document.createElement('li');
            li.className = 'placeholder';
            li.textContent = placeholder;
            results.append(li);
            return;
        }

        rows.forEach((row, index) => {
            const li = document.createElement('li');
            li.setAttribute('role', 'option');
            li.dataset.index = String(index);
            li.append(...renderRow(row));
            // Focus returns to the input so Enter/Escape keep working after a click.
            li.addEventListener('click', () => {
                select(index);
                query.focus();
            });
            li.addEventListener('dblclick', () => accept(false));
            results.append(li);
        });

        if (truncated) {
            const li = document.createElement('li');
            li.className = 'placeholder';
            li.textContent = 'More results exist — keep typing to narrow the search.';
            results.append(li);
        }
    }

    function renderRow(row: Row): HTMLElement[] {
        switch (row.kind) {
            case 'symbol': {
                const { item } = row;
                const name = span('name', item.name);
                const container = span('muted', item.container ?? '');
                const location = span(
                    'location',
                    item.kind === 'file' ? dirOf(item.path) : fileAndLine(item)
                );
                return [badge(item), name, container, spacer(), location];
            }
            case 'text': {
                const { item } = row;
                const line = span('name', '');
                appendHighlighted(line, item.lineText, query.value.trim());
                const location = span('location', `${baseName(item.path)}:${item.line + 1}`);
                return [badgeOf('text', '≡'), line, spacer(), location];
            }
            case 'action': {
                const { item } = row;
                const title = span('name', item.category ? `${item.category}: ${item.title}` : item.title);
                const parts = [badgeOf('action', '⚡'), title, spacer()];
                if (item.keybinding) {
                    const kbd = document.createElement('kbd');
                    kbd.textContent = item.keybinding;
                    parts.push(kbd);
                }
                return parts;
            }
            case 'recent': {
                const { item } = row;
                return [
                    badgeOf('file', '≣'),
                    span('name', item.name),
                    spacer(),
                    span('location', item.relativePath),
                ];
            }
        }
    }

    /** The matched substring emboldened, the way Rider shows why a text row is here. */
    function appendHighlighted(parent: HTMLElement, text: string, needle: string): void {
        const at = needle.length === 0 ? -1 : text.toLowerCase().indexOf(needle.toLowerCase());
        if (at < 0) {
            parent.textContent = text;
            return;
        }
        parent.append(
            document.createTextNode(text.slice(0, at)),
            Object.assign(document.createElement('b'), { textContent: text.slice(at, at + needle.length) }),
            document.createTextNode(text.slice(at + needle.length))
        );
    }

    function span(className: string, text: string): HTMLElement {
        const el = document.createElement('span');
        el.className = className;
        el.textContent = text;
        return el;
    }

    function spacer(): HTMLElement {
        return span('spacer', '');
    }

    /** LSP SymbolKind → a colored letter badge, standing in for the editor's symbol icons. */
    function badge(item: SearchMsg.SymbolItem): HTMLElement {
        if (item.kind === 'file') {
            return badgeOf('file', '≣');
        }
        const [letter, cls] = BADGES[item.symbolKind] ?? (item.kind === 'type' ? ['T', 'class'] : ['M', 'method']);
        return badgeOf(cls, letter);
    }

    const BADGES: Record<number, [string, string]> = {
        3: ['N', 'namespace'],
        5: ['C', 'class'],
        6: ['M', 'method'],
        7: ['P', 'property'],
        8: ['F', 'field'],
        9: ['C', 'method'],
        10: ['E', 'enum'],
        11: ['I', 'interface'],
        12: ['M', 'method'],
        13: ['V', 'field'],
        14: ['K', 'enum'],
        22: ['E', 'enum'],
        23: ['S', 'struct'],
        24: ['V', 'event'],
        25: ['O', 'method'],
    };

    function badgeOf(cls: string, letter: string): HTMLElement {
        const el = span(`badge ${cls}`, letter);
        el.setAttribute('aria-hidden', 'true');
        return el;
    }

    function baseName(path: string): string {
        return path.split(/[\\/]/).pop() ?? path;
    }

    function dirOf(path: string): string {
        const parts = path.split(/[\\/]/);
        parts.pop();
        return parts.slice(-3).join('/');
    }

    function fileAndLine(item: SearchMsg.SymbolItem): string {
        return `${baseName(item.path)}:${item.line + 1}`;
    }

    // ---- Selection and preview -------------------------------------------------------

    function select(index: number): void {
        selected = index;
        for (const li of results.children) {
            (li as HTMLElement).classList.toggle(
                'selected',
                (li as HTMLElement).dataset.index === String(index)
            );
        }
        const li = results.querySelector<HTMLElement>(`li[data-index="${index}"]`);
        li?.scrollIntoView({ block: 'nearest' });

        const row = index >= 0 ? rows[index] : undefined;
        openSplit.hidden = !row || row.kind === 'action';
        statusPath.textContent = row ? pathOf(row) : '';

        if (previewTimer !== undefined) {
            clearTimeout(previewTimer);
        }
        if (!row || row.kind === 'action') {
            previewLines.replaceChildren();
            return;
        }

        previewTimer = window.setTimeout(() => {
            const target = targetOf(row);
            if (target) {
                vscode.postMessage({
                    type: 'preview',
                    id: ++previewId,
                    uri: target.uri,
                    line: target.line,
                    skipPreamble: target.isFile,
                });
            }
        }, PREVIEW_DEBOUNCE_MS);
    }

    function pathOf(row: Row): string {
        switch (row.kind) {
            case 'symbol':
                return row.item.path;
            case 'text':
                return `${row.item.path}:${row.item.line + 1}`;
            case 'action':
                return row.item.command;
            case 'recent':
                return row.item.relativePath;
        }
    }

    function targetOf(row: Row): { uri: string; line: number; character: number; isFile: boolean } | undefined {
        switch (row.kind) {
            case 'symbol':
                return {
                    uri: row.item.uri,
                    line: row.item.line,
                    character: row.item.character,
                    isFile: row.item.kind === 'file' && row.item.line === 0,
                };
            case 'text':
                return { uri: row.item.uri, line: row.item.line, character: row.item.character, isFile: false };
            case 'recent':
                return { uri: row.item.uri, line: 0, character: 0, isFile: true };
            case 'action':
                return undefined;
        }
    }

    function accept(beside: boolean): void {
        const row = selected >= 0 ? rows[selected] : undefined;
        if (!row) {
            return;
        }
        if (row.kind === 'action') {
            vscode.postMessage({ type: 'runAction', command: row.item.command });
            return;
        }
        const target = targetOf(row);
        if (target) {
            vscode.postMessage({ type: 'open', ...target, beside });
        }
    }

    function renderPreview(message: Extract<SearchMsg.ToView, { type: 'previewText' }>): void {
        previewLines.replaceChildren();

        // The host's TextMate spans carry the theme's real colors; the local tokenizer is the
        // fallback for languages with no installed grammar.
        const fallback = message.tokens ? null : SE.highlightLines(message.lines, message.languageId);

        message.lines.forEach((text, offset) => {
            const lineNumber = message.startLine + offset;
            const line = document.createElement('div');
            line.className = lineNumber === message.targetLine ? 'line target' : 'line';

            const number = span('line-number', String(lineNumber + 1));
            const code = span('line-text', '');
            if (lineNumber === message.targetLine && activeTab === 'text') {
                // The Text tab's target line bolds the match — that beats coloring it.
                appendHighlighted(code, text, query.value.trim());
            } else if (message.tokens) {
                for (const token of message.tokens[offset] ?? []) {
                    code.append(themedSpan(token));
                }
            } else {
                for (const token of fallback![offset]) {
                    if (token.cls === null) {
                        code.append(document.createTextNode(token.text));
                    } else {
                        code.append(span(token.cls, token.text));
                    }
                }
            }
            line.append(number, code);
            previewLines.append(line);
        });

        preview.querySelector('.line.target')?.scrollIntoView({ block: 'center' });
    }

    function themedSpan(token: SearchMsg.PreviewToken): HTMLElement | Text {
        if (token.color === null && token.fontStyle === 0) {
            return document.createTextNode(token.text);
        }

        const el = document.createElement('span');
        el.textContent = token.text;
        if (token.color !== null) {
            el.style.color = token.color;
        }
        if (token.fontStyle & 1) {
            el.style.fontStyle = 'italic';
        }
        if (token.fontStyle & 2) {
            el.style.fontWeight = 'bold';
        }
        const decorations = [token.fontStyle & 4 ? 'underline' : '', token.fontStyle & 8 ? 'line-through' : '']
            .filter(Boolean)
            .join(' ');
        if (decorations.length > 0) {
            el.style.textDecoration = decorations;
        }
        return el;
    }

    // ---- Tabs ------------------------------------------------------------------------

    function setTab(tab: SearchMsg.Tab): void {
        activeTab = tab;
        for (const button of document.querySelectorAll<HTMLButtonElement>('nav [role="tab"]')) {
            button.setAttribute('aria-selected', String(button.dataset.tab === tab));
        }
        requestSearch(true);
    }

    // ---- Events ----------------------------------------------------------------------

    for (const button of document.querySelectorAll<HTMLButtonElement>('nav [role="tab"]')) {
        button.addEventListener('click', () => {
            setTab(button.dataset.tab as SearchMsg.Tab);
            query.focus();
        });
    }

    query.addEventListener('input', () => requestSearch());
    nonSolution.addEventListener('change', () => {
        requestSearch(true);
        query.focus();
    });

    query.addEventListener('keydown', (event) => {
        switch (event.key) {
            case 'ArrowDown':
                event.preventDefault();
                if (rows.length > 0) {
                    select(Math.min(selected + 1, rows.length - 1));
                }
                return;
            case 'ArrowUp':
                event.preventDefault();
                if (rows.length > 0) {
                    select(Math.max(selected - 1, 0));
                }
                return;
            case 'Enter':
                event.preventDefault();
                accept(event.ctrlKey);
                return;
            case 'Tab': {
                event.preventDefault();
                const step = event.shiftKey ? TABS.length - 1 : 1;
                setTab(TABS[(TABS.indexOf(activeTab) + step) % TABS.length]);
                return;
            }
            case 'Escape':
                event.preventDefault();
                vscode.postMessage({ type: 'close' });
                return;
            case 'n':
                if (event.altKey) {
                    event.preventDefault();
                    nonSolution.checked = !nonSolution.checked;
                    requestSearch(true);
                }
                return;
        }
    });

    openSplit.addEventListener('click', () => accept(true));

    // Double Shift toggles "include non-solution items", as in Rider's Search Everywhere.
    // A press only counts when Shift was the whole gesture — Shift+Tab must not accumulate.
    let lastShiftUp = 0;
    let shiftChorded = false;

    window.addEventListener('keydown', (event) => {
        // Only a key pressed WITH Shift held is a chord; plain typing must not poison the
        // gesture, or the first double-Shift after typing never registers.
        if (event.key !== 'Shift' && event.shiftKey) {
            shiftChorded = true;
        }
    });

    window.addEventListener('keyup', (event) => {
        if (event.key !== 'Shift') {
            return;
        }
        const now = Date.now();
        if (!shiftChorded && now - lastShiftUp < 400) {
            lastShiftUp = 0;
            nonSolution.checked = !nonSolution.checked;
            requestSearch(true);
        } else {
            lastShiftUp = shiftChorded ? 0 : now;
        }
        shiftChorded = false;
    });

    window.addEventListener('focus', () => query.focus());

    // ---- Splitter --------------------------------------------------------------------

    const splitter = document.getElementById('splitter') as HTMLElement;
    splitter.addEventListener('pointerdown', (down) => {
        down.preventDefault();
        splitter.setPointerCapture(down.pointerId);
        const startHeight = preview.getBoundingClientRect().height;

        const move = (event: PointerEvent) => {
            const height = Math.max(60, startHeight + (down.clientY - event.clientY));
            preview.style.flexBasis = `${height}px`;
        };
        const up = () => {
            splitter.removeEventListener('pointermove', move);
            splitter.removeEventListener('pointerup', up);
        };
        splitter.addEventListener('pointermove', move);
        splitter.addEventListener('pointerup', up);
    });

    // ---- Host messages ---------------------------------------------------------------

    window.addEventListener('message', (event: MessageEvent<SearchMsg.ToView>) => {
        const message = event.data;
        switch (message.type) {
            case 'boot':
                recent = message.recent;
                if (query.value.trim().length === 0) {
                    showRecent();
                }
                return;

            case 'results': {
                if (message.id !== searchId) {
                    return; // a newer query is already out
                }
                progress.classList.remove('busy');
                const items = message.items;
                rows =
                    message.tab === 'text'
                        ? (items as SearchMsg.TextItem[]).map((item) => ({ kind: 'text', item }) as Row)
                        : message.tab === 'actions'
                          ? (items as SearchMsg.ActionItem[]).map((item) => ({ kind: 'action', item }) as Row)
                          : (items as SearchMsg.SymbolItem[]).map((item) => ({ kind: 'symbol', item }) as Row);
                render(rows.length === 0 ? `No results for "${query.value.trim()}"` : undefined, message.truncated);
                select(rows.length > 0 ? 0 : -1);
                return;
            }

            case 'previewText':
                if (message.id === previewId) {
                    renderPreview(message);
                }
                return;

            case 'error':
                if (message.scope === 'preview') {
                    if (message.id === previewId) {
                        previewLines.replaceChildren(span('placeholder', message.message));
                    }
                    return;
                }
                if (message.id === searchId) {
                    progress.classList.remove('busy');
                    rows = [];
                    render(message.message);
                    select(-1);
                }
                return;
        }
    });

    query.focus();
    vscode.postMessage({ type: 'ready' });
}
