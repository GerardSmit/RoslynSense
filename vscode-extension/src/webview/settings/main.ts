/// <reference path="../../settings/protocol.d.ts" />

/**
 * The settings panel's browser half: it turns the JSON Schema into a form, and turns edits back
 * into one message per changed setting.
 *
 * Everything on screen is built with `createElement`. Nothing from a config file — a connection
 * string, a path, a file name someone typed — is ever assigned as HTML.
 */

declare function acquireVsCodeApi(): { postMessage(message: SettingsMsg.ToHost): void };

const vscode = acquireVsCodeApi();

let state: SettingsMsg.State | undefined;

window.addEventListener('message', (event: MessageEvent<SettingsMsg.ToView>) => {
    const message = event.data;
    switch (message.type) {
        case 'state':
            state = message;
            render();
            break;
        case 'connectionCompletions':
            applyConnectionCompletions(message);
            break;
        case 'connectionsResolved':
            applyConnectionPreviews(message);
            break;
    }
});

const searchBox = document.getElementById('search') as HTMLInputElement;
searchBox.addEventListener('input', applyFilter);
searchBox.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && searchBox.value !== '') {
        searchBox.value = '';
        applyFilter();
        event.stopPropagation();
    }
});

function render(): void {
    if (!state) {
        return;
    }

    renderScopes(state);
    renderNotice(state);

    const form = document.getElementById('form')!;
    form.textContent = '';

    const schema = state.schema as SchemaNode;
    for (const [name, child] of properties(schema)) {
        form.append(renderNode(child, [name], ''));
    }

    applyFilter();
}

/**
 * Hides rows the query does not mention, then groups left with nothing to show. Toggling `hidden`
 * rather than re-rendering keeps focus in the search box and open controls undisturbed.
 */
function applyFilter(): void {
    const form = document.getElementById('form')!;
    const tokens = searchBox.value.trim().toLowerCase().split(/\s+/).filter(Boolean);

    let visible = 0;
    for (const row of form.querySelectorAll<HTMLElement>('.setting')) {
        const haystack = row.dataset.search ?? '';
        row.hidden = tokens.length > 0 && !tokens.every((token) => haystack.includes(token));
        if (!row.hidden) {
            visible++;
        }
    }

    // Innermost first, so a group whose only content is hidden subgroups hides too.
    const groups = Array.from(form.querySelectorAll<HTMLElement>('.group')).reverse();
    for (const group of groups) {
        group.hidden =
            tokens.length > 0 &&
            !Array.from(group.querySelectorAll<HTMLElement>('.setting')).some((row) => !row.hidden);
    }

    const empty = document.getElementById('no-matches') as HTMLParagraphElement;
    empty.hidden = tokens.length === 0 || visible > 0;
    empty.textContent = `No settings match “${searchBox.value.trim()}”.`;
}

// ---------------------------------------------------------------------------
// Chrome
// ---------------------------------------------------------------------------

function renderScopes(current: SettingsMsg.State): void {
    const nav = document.getElementById('scopes')!;
    nav.textContent = '';

    // Weakest to strongest, the order the merge applies them in — the selector doubles as a
    // diagram of the cascade.
    const order: SettingsMsg.Scope[] = ['global', 'repo', 'repoLocal', 'personal'];
    let first = true;
    for (const scope of order) {
        const layer = current.layers.find((candidate) => candidate.scope === scope && candidate.editable);
        if (!layer) {
            continue;
        }

        if (!first) {
            const arrow = document.createElement('span');
            arrow.className = 'cascade-arrow';
            arrow.setAttribute('aria-hidden', 'true');
            arrow.textContent = '›';
            nav.append(arrow);
        }
        first = false;

        const tab = document.createElement('button');
        tab.setAttribute('role', 'tab');
        tab.setAttribute('aria-selected', String(scope === current.scope));
        tab.textContent = layer.label;

        if (!layer.exists) {
            tab.classList.add('empty');
            tab.title = 'No file yet — saving a setting here creates it';
        }
        tab.addEventListener('click', () => vscode.postMessage({ type: 'selectScope', scope }));
        nav.append(tab);
    }

    const selected = current.layers.find(
        (layer) => layer.scope === current.scope && layer.editable
    );
    const file = document.getElementById('scope-file')!;
    file.textContent = '';
    if (selected) {
        const link = document.createElement('button');
        link.className = 'linklike';
        link.textContent = selected.filePath + (selected.exists ? '' : '  (not created yet)');
        link.addEventListener('click', () =>
            vscode.postMessage({ type: 'openFile', filePath: selected.filePath })
        );
        file.append(link);
    }
}

function renderNotice(current: SettingsMsg.State): void {
    const notice = document.getElementById('notice') as HTMLParagraphElement;
    const broken = current.layers.filter((layer) => layer.parseError);

    if (broken.length > 0) {
        notice.hidden = false;
        notice.className = 'notice error';
        notice.textContent = `${broken[0].filePath} did not parse (${broken[0].parseError}); it is being skipped.`;
        return;
    }

    if (current.notice) {
        notice.hidden = false;
        notice.className = 'notice';
        notice.textContent = current.notice;
        return;
    }

    notice.hidden = true;
}

// ---------------------------------------------------------------------------
// The form
// ---------------------------------------------------------------------------

interface SchemaNode {
    type?: string | string[];
    title?: string;
    description?: string;
    properties?: Record<string, SchemaNode>;
    items?: SchemaNode;
    enum?: unknown[];
}

function properties(node: SchemaNode): [string, SchemaNode][] {
    return Object.entries(node.properties ?? {});
}

/**
 * `context` is the searchable text inherited from enclosing groups, so that a query naming a
 * section ("language packs", "database") matches the settings inside it.
 */
function renderNode(node: SchemaNode, path: string[], context: string): HTMLElement {
    if (node.properties) {
        return renderSection(node, path, context);
    }
    return renderSetting(node, path, context);
}

function renderSection(node: SchemaNode, path: string[], context: string): HTMLElement {
    const section = document.createElement('section');
    section.className = 'group';

    const title = node.title ?? path[path.length - 1];
    const heading = document.createElement('h2');
    heading.textContent = title;
    // The key as the file spells it, for whoever edits the JSON by hand.
    heading.title = path.join('.');
    section.append(heading);

    if (node.description) {
        const blurb = document.createElement('p');
        blurb.className = 'group-description';
        blurb.textContent = node.description;
        section.append(blurb);
    }

    const childContext = `${context} ${title} ${path.join('.')}`.toLowerCase();
    for (const [name, child] of properties(node)) {
        section.append(renderNode(child, [...path, name], childContext));
    }

    return section;
}

function renderSetting(node: SchemaNode, path: string[], context: string): HTMLElement {
    const row = document.createElement('div');
    row.className = 'setting';

    const title = node.title ?? path[path.length - 1];
    const key = path.join('.');
    row.dataset.search = `${context} ${title} ${key} ${node.description ?? ''}`.toLowerCase();

    if (ownValue(path) !== undefined) {
        // This scope says something itself — the accent bar marks the rows a tab actually owns.
        row.classList.add('own');
    }

    const label = document.createElement('label');
    label.className = 'setting-label';
    label.textContent = title;
    label.title = key;
    row.append(label);

    const control = buildControl(node, path);
    control.classList.add('setting-control');
    if (control.classList.contains('block')) {
        // Row and list editors need the row's full width, not the control column.
        row.classList.add('wide');
    }
    row.append(control);

    if (node.description) {
        const blurb = document.createElement('p');
        blurb.className = 'setting-description';
        blurb.textContent = node.description;
        row.append(blurb);
    }

    const origin = originLine(path);
    if (origin) {
        row.append(origin);
    }
    return row;
}

/**
 * Where the effective value comes from, in words — the one thing a layered file cannot show you
 * by being opened. Shown only when it is not the scope being edited: either this scope says
 * nothing and inherits, or it says something and a nearer scope overrides it.
 */
function originLine(path: string[]): HTMLElement | undefined {
    if (!state) {
        return undefined;
    }

    const winner = originOf(path);
    if (!winner || winner.scope === state.scope) {
        return undefined;
    }

    const line = document.createElement('span');
    line.className = 'origin';

    const overridden = ownValue(path) !== undefined;
    const text = overridden ? `Overridden by ${winner.label}` : `Value from ${winner.label}`;

    if (winner.editable) {
        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'linklike';
        link.textContent = text;
        link.title = `${winner.filePath} — click to edit that scope`;
        link.addEventListener('click', () =>
            vscode.postMessage({ type: 'selectScope', scope: winner.scope })
        );
        line.append(link);
    } else {
        // An ancestor directory's file: not a tab, so name the file instead.
        line.textContent = text;
        line.title = winner.filePath;
    }

    return line;
}

function buildControl(node: SchemaNode, path: string[]): HTMLElement {
    const type = typeOf(node);

    if (Array.isArray(node.enum) && node.enum.length > 0) {
        return enumControl(node.enum, path);
    }

    switch (type) {
        case 'boolean':
            return booleanControl(path);
        case 'integer':
        case 'number':
            return numberControl(path);
        case 'string':
            return stringControl(path);
        case 'array': {
            const items = node.items ?? {};
            if (typeOf(items) === 'string') {
                return stringListControl(path);
            }
            if (items.properties) {
                return objectListControl(items, path);
            }
            return escapeHatch('This list is edited in the file.');
        }
        case 'object':
            // An object the schema gives no fixed properties: a map of the person's own keys.
            return mapControl(path);
        default:
            return escapeHatch('This setting is edited in the file.');
    }
}

/**
 * Three states, not two. A checkbox would make "the layer says false" and "the layer says nothing"
 * look identical, and with four layers in play the difference is the whole point: unsetting is how
 * you stop overriding what a weaker layer said.
 */
function booleanControl(path: string[]): HTMLElement {
    const select = document.createElement('select');
    addOption(select, '', 'Default');
    addOption(select, 'true', 'On');
    addOption(select, 'false', 'Off');

    const own = ownValue(path);
    select.value = own === undefined ? '' : String(own);

    select.addEventListener('change', () => {
        set(path, select.value === '' ? null : select.value === 'true');
    });

    return select;
}

function enumControl(values: unknown[], path: string[]): HTMLElement {
    const select = document.createElement('select');
    addOption(select, '', 'Default');
    for (const value of values) {
        addOption(select, String(value), String(value));
    }

    const own = ownValue(path);
    select.value = own === undefined || own === null ? '' : String(own);
    select.addEventListener('change', () => set(path, select.value === '' ? null : select.value));
    return select;
}

function stringControl(path: string[]): HTMLElement {
    const input = document.createElement('input');
    input.type = 'text';

    const own = ownValue(path);
    input.value = own === undefined || own === null ? '' : String(own);
    input.placeholder = placeholderFor(path);

    commitOnBlur(input, () => set(path, input.value.trim() === '' ? null : input.value));
    return input;
}

function numberControl(path: string[]): HTMLElement {
    const input = document.createElement('input');
    input.type = 'number';

    const own = ownValue(path);
    input.value = own === undefined || own === null ? '' : String(own);
    input.placeholder = placeholderFor(path);

    commitOnBlur(input, () => {
        const text = input.value.trim();
        if (text === '') {
            set(path, null);
            return;
        }
        const parsed = Number(text);
        if (Number.isFinite(parsed)) {
            set(path, parsed);
        }
    });
    return input;
}

/** One name per line. Empty means unset, which is not the same as an empty list. */
function stringListControl(path: string[]): HTMLElement {
    const area = document.createElement('textarea');
    area.rows = 3;
    area.spellcheck = false;

    const own = ownValue(path);
    area.value = Array.isArray(own) ? own.join('\n') : '';
    area.placeholder = placeholderFor(path) || 'One per line';

    commitOnBlur(area, () => {
        const lines = area.value
            .split('\n')
            .map((line) => line.trim())
            .filter((line) => line.length > 0);
        set(path, area.value.trim() === '' ? null : lines);
    });
    return area;
}

/**
 * A map of the person's own keys to string values — `database.connections`. Writes once, when
 * focus leaves the editor or a row is removed, never on every field hop.
 */
function mapControl(path: string[]): HTMLElement {
    const box = document.createElement('div');
    box.className = 'kv-editor block';

    // Connections get live help the generic map cannot: suggestions for `json:`/`xml:` config
    // references, and a preview of what each reference resolves to.
    const connections = path.join('.') === 'database.connections';

    const rows = document.createElement('div');
    box.append(rows);

    const addRow = (key: string, value: string) => {
        const entry = document.createElement('div');
        entry.className = 'kv-entry';

        const row = document.createElement('div');
        row.className = 'kv-row';

        const keyInput = document.createElement('input');
        keyInput.type = 'text';
        keyInput.className = 'kv-key';
        keyInput.value = key;
        keyInput.placeholder = 'Name';
        keyInput.setAttribute('aria-label', 'Entry name');

        const valueInput = document.createElement('input');
        valueInput.type = 'text';
        valueInput.className = 'kv-value';
        valueInput.value = value;
        valueInput.placeholder = 'Value';
        valueInput.setAttribute('aria-label', 'Entry value');

        row.append(keyInput, valueInput, removeButton(entry, commit));
        entry.append(row);

        if (connections) {
            valueInput.setAttribute('list', connectionDatalist().id);
            wireConnectionHints(valueInput);

            const preview = document.createElement('p');
            preview.className = 'kv-resolved';
            preview.hidden = true;
            entry.append(preview);
        }

        rows.append(entry);
        return entry;
    };

    const own = ownValue(path);
    if (own && typeof own === 'object' && !Array.isArray(own)) {
        for (const [key, value] of Object.entries(own as Record<string, unknown>)) {
            addRow(key, stringifyMapValue(value));
        }
    }

    if (connections) {
        requestConnectionPreviews(rows);
    }

    function commit(): void {
        const result: Record<string, string> = {};
        for (const row of rows.querySelectorAll<HTMLElement>('.kv-row')) {
            const key = row.querySelector<HTMLInputElement>('.kv-key')!.value.trim();
            const value = row.querySelector<HTMLInputElement>('.kv-value')!.value.trim();
            if (key !== '') {
                result[key] = value;
            }
        }
        setIfChanged(path, Object.keys(result).length === 0 ? null : result);
    }

    box.append(addButton('Add entry', () => addRow('', '').querySelector('input')!.focus()));
    commitOnLeave(box, commit);
    return box;
}

// ---------------------------------------------------------------------------
// Connection hints: suggestions while typing, and what each reference resolves to
// ---------------------------------------------------------------------------

/** One shared datalist — only one value input has focus at a time. */
function connectionDatalist(): HTMLDataListElement {
    let list = document.getElementById('connection-suggestions') as HTMLDataListElement | null;
    if (!list) {
        list = document.createElement('datalist');
        list.id = 'connection-suggestions';
        document.body.append(list);
    }
    return list;
}

function wireConnectionHints(input: HTMLInputElement): void {
    let timer: number | undefined;
    input.addEventListener('input', () => {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => {
            vscode.postMessage({ type: 'completeConnection', value: input.value });
            vscode.postMessage({ type: 'resolveConnections', values: [input.value] });
        }, 150);
    });
}

/** Ask the host what every current connection value resolves to. */
function requestConnectionPreviews(rows: HTMLElement): void {
    const values = Array.from(rows.querySelectorAll<HTMLInputElement>('.kv-value'))
        .map((input) => input.value)
        .filter((value) => value.trim() !== '');
    if (values.length > 0) {
        vscode.postMessage({ type: 'resolveConnections', values });
    }
}

function applyConnectionCompletions(message: SettingsMsg.ConnectionCompletions): void {
    // Only if the answer still matches what is being typed — answers race edits.
    const active = document.activeElement;
    if (!(active instanceof HTMLInputElement) || active.value !== message.value) {
        return;
    }

    const list = connectionDatalist();
    list.textContent = '';
    for (const item of message.items) {
        const option = document.createElement('option');
        option.value = item;
        list.append(option);
    }
}

function applyConnectionPreviews(message: SettingsMsg.ConnectionsResolved): void {
    for (const entry of document.querySelectorAll<HTMLElement>('.kv-entry')) {
        const value = entry.querySelector<HTMLInputElement>('.kv-value')!.value;
        const preview = entry.querySelector<HTMLParagraphElement>('.kv-resolved');
        if (!preview || !(value in message.results)) {
            continue;
        }

        const info = message.results[value];
        if (info.resolved !== undefined) {
            preview.hidden = false;
            preview.classList.remove('error');
            preview.textContent = `→ ${info.resolved}`;
        } else if (info.error !== undefined) {
            preview.hidden = false;
            preview.classList.add('error');
            preview.textContent = info.error;
        } else {
            preview.hidden = true;
            preview.textContent = '';
        }
    }
}

/**
 * A connections value may be `provider:connectionString` or an object saying the same thing; the
 * editor shows and writes the string form, which the schema names as equivalent.
 */
function stringifyMapValue(value: unknown): string {
    if (typeof value === 'string') {
        return value;
    }
    if (value && typeof value === 'object') {
        const record = value as Record<string, unknown>;
        if (typeof record.provider === 'string' && typeof record.connectionString === 'string') {
            return `${record.provider}:${record.connectionString}`;
        }
    }
    return JSON.stringify(value);
}

/** A list of objects, each edited as a small form built from the item schema's properties. */
function objectListControl(itemSchema: SchemaNode, path: string[]): HTMLElement {
    const box = document.createElement('div');
    box.className = 'item-editor block';

    const list = document.createElement('div');
    box.append(list);

    const addItem = (value: Record<string, unknown>) => {
        const item = document.createElement('fieldset');
        item.className = 'item';

        for (const [name, child] of properties(itemSchema)) {
            const row = document.createElement('div');
            row.className = 'item-row';

            const label = document.createElement('label');
            label.textContent = name;
            row.append(label, itemField(name, child, value[name]));
            item.append(row);
        }

        const actions = document.createElement('div');
        actions.className = 'item-actions';
        actions.append(removeButton(item, commit));
        item.append(actions);

        list.append(item);
        return item;
    };

    const own = ownValue(path);
    if (Array.isArray(own)) {
        for (const entry of own) {
            if (entry && typeof entry === 'object') {
                addItem(entry as Record<string, unknown>);
            }
        }
    }

    function commit(): void {
        const result: Record<string, unknown>[] = [];
        for (const item of list.querySelectorAll<HTMLElement>('.item')) {
            const value: Record<string, unknown> = {};
            for (const field of item.querySelectorAll<HTMLElement>('[data-field]')) {
                const parsed = readItemField(field);
                if (parsed !== undefined) {
                    value[field.dataset.field!] = parsed;
                }
            }
            if (Object.keys(value).length > 0) {
                result.push(value);
            }
        }
        setIfChanged(path, result.length === 0 ? null : result);
    }

    box.append(
        addButton('Add item', () => {
            const item = addItem({});
            item.querySelector<HTMLElement>('input, select')?.focus();
        })
    );
    commitOnLeave(box, commit);
    return box;
}

/** One field of one list item. `data-field` carries the property name for readItemField. */
function itemField(name: string, schema: SchemaNode, value: unknown): HTMLElement {
    const type = typeOf(schema);

    if (type === 'boolean') {
        const select = document.createElement('select');
        select.dataset.field = name;
        select.dataset.kind = 'boolean';
        addOption(select, '', 'Default');
        addOption(select, 'true', 'On');
        addOption(select, 'false', 'Off');
        select.value = typeof value === 'boolean' ? String(value) : '';
        return select;
    }

    const input = document.createElement('input');
    input.type = 'text';
    input.dataset.field = name;

    if (type === 'array') {
        input.dataset.kind = 'list';
        input.placeholder = 'Comma-separated';
        input.value = Array.isArray(value) ? value.join(', ') : '';
    } else {
        input.dataset.kind = 'text';
        input.value = value === undefined || value === null ? '' : String(value);
    }

    return input;
}

/** The field's value for the file, or undefined when it should be omitted from the item. */
function readItemField(field: HTMLElement): unknown {
    const kind = field.dataset.kind;

    if (field instanceof HTMLSelectElement) {
        return field.value === '' ? undefined : field.value === 'true';
    }
    if (!(field instanceof HTMLInputElement)) {
        return undefined;
    }

    const text = field.value.trim();
    if (text === '') {
        return undefined;
    }
    if (kind === 'list') {
        const parts = text
            .split(',')
            .map((part) => part.trim())
            .filter((part) => part.length > 0);
        return parts.length > 0 ? parts : undefined;
    }
    return text;
}

function removeButton(row: HTMLElement, commit: () => void): HTMLButtonElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'linklike remove';
    button.textContent = 'Remove';
    button.addEventListener('click', () => {
        row.remove();
        commit();
    });
    return button;
}

function addButton(text: string, onClick: () => void): HTMLButtonElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'linklike add';
    button.textContent = text;
    button.addEventListener('click', onClick);
    return button;
}

/**
 * Commits when focus leaves the editor as a whole — tabbing between its own fields stays silent,
 * so a half-filled row is not written to disk (and re-rendered over) on every keystroke's blur.
 */
function commitOnLeave(container: HTMLElement, commit: () => void): void {
    container.addEventListener('focusout', (event) => {
        const next = (event as FocusEvent).relatedTarget;
        if (!(next instanceof Node) || !container.contains(next)) {
            commit();
        }
    });
}

/** Writes only when the value actually differs from what the scope already says. */
function setIfChanged(path: string[], value: unknown): void {
    const own = ownValue(path);
    const current = own === undefined ? null : own;
    if (JSON.stringify(current) !== JSON.stringify(value)) {
        set(path, value);
    }
}

function escapeHatch(text: string): HTMLElement {
    const span = document.createElement('span');
    span.className = 'escape-hatch';
    span.textContent = text;

    const link = document.createElement('button');
    link.className = 'linklike';
    link.textContent = 'Open';
    link.addEventListener('click', () => {
        const layer = state?.layers.find(
            (candidate) => candidate.scope === state!.scope && candidate.editable
        );
        if (layer) {
            vscode.postMessage({ type: 'openFile', filePath: layer.filePath });
        }
    });

    span.append(' ', link);
    return span;
}

// ---------------------------------------------------------------------------
// Values
// ---------------------------------------------------------------------------

/** What the scope being edited says itself, or undefined when it says nothing. */
function ownValue(path: string[]): unknown {
    const layer = state?.layers.find(
        (candidate) => candidate.scope === state!.scope && candidate.editable
    );
    return layer?.json ? valueAt(layer.json, path) : undefined;
}

/** The strongest layer that names this setting. */
function originOf(path: string[]): SettingsMsg.Layer | undefined {
    if (!state) {
        return undefined;
    }
    for (let i = state.layers.length - 1; i >= 0; i--) {
        const layer = state.layers[i];
        if (layer.json && valueAt(layer.json, path) !== undefined) {
            return layer;
        }
    }
    return undefined;
}

/** The inherited value, shown greyed in the control so the field is never blank-but-meaningful. */
function placeholderFor(path: string[]): string {
    const effective = state ? valueAt(state.effective, path) : undefined;
    if (effective === undefined || effective === null) {
        return '';
    }
    return Array.isArray(effective) ? effective.join('\n') : String(effective);
}

function valueAt(root: unknown, path: readonly string[]): unknown {
    let node: unknown = root;
    for (const segment of path) {
        if (typeof node !== 'object' || node === null) {
            return undefined;
        }
        node = (node as Record<string, unknown>)[segment];
    }
    return node;
}

function set(path: string[], value: unknown): void {
    if (!state) {
        return;
    }
    vscode.postMessage({ type: 'set', scope: state.scope, path, value });
}

function typeOf(node: SchemaNode): string {
    // The exporter writes nullable settings as `["string", "null"]`; the null is what makes them
    // omittable and says nothing about the control to draw.
    const type = node.type;
    if (Array.isArray(type)) {
        return type.find((candidate) => candidate !== 'null') ?? 'null';
    }
    return type ?? 'object';
}

function addOption(select: HTMLSelectElement, value: string, text: string): void {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = text;
    select.append(option);
}

/** Saves on blur and on Enter, never on every keystroke — a write is a file write. */
function commitOnBlur(element: HTMLInputElement | HTMLTextAreaElement, commit: () => void): void {
    element.addEventListener('blur', commit);
    element.addEventListener('keydown', (event) => {
        const key = (event as KeyboardEvent).key;
        if (key === 'Enter' && !(element instanceof HTMLTextAreaElement)) {
            event.preventDefault();
            commit();
        }
    });
}
