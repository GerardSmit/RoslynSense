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
        case 'settingChoices':
        case 'memberShape':
            answer(message.token, message);
            break;
        case 'resolvable':
            for (const again of [...askAgain]) {
                again();
            }
            break;
        case 'saved':
            pending.clear();
            renderPending();
            break;
    }
});

/**
 * The settings changed since the last save, keyed by scope and path so that changing one of them
 * twice writes it once.
 *
 * Nothing here has reached a file. The panel used to write on every commit, which made a file
 * write out of every blur — including the ones that were on the way to somewhere else — and left
 * no way to change your mind. The host is told about each edit as it happens all the same,
 * because closing the tab is what asks whether to keep them and this page is gone by then.
 */
const pending = new Map<
    string,
    { readonly scope: SettingsMsg.Scope; readonly path: string[]; readonly value: unknown }
>();

function pendingKey(scope: SettingsMsg.Scope, path: readonly string[]): string {
    return `${scope}\u0000${path.join('.')}`;
}

/** What the toolbar says about the edits waiting: how many, and the two things to do with them. */
function renderPending(): void {
    const save = document.getElementById('save') as HTMLButtonElement;
    const discard = document.getElementById('discard') as HTMLButtonElement;
    const label = document.getElementById('pending')!;

    save.disabled = pending.size === 0;
    discard.hidden = pending.size === 0;
    label.textContent = pending.size === 0 ? '' : count(pending.size, 'unsaved change');
}

function save(): void {
    // An item open in its dialog is an edit like any other, and Ctrl+S over it means the same
    // thing Done does — otherwise saving from inside the dialog writes everything except the item
    // being looked at.
    openDialog?.finish();

    // A field still being typed in has not committed yet, and it commits on leaving. Without this
    // the edit somebody just made is the one Ctrl+S does not write.
    if (document.activeElement instanceof HTMLElement) {
        document.activeElement.blur();
    }
    vscode.postMessage({ type: 'save' });
}

/**
 * Controls whose answer came from the solution rather than from the schema.
 *
 * Every one of them asks once, when it is built, and a panel opened while the solution was still
 * loading gets "nothing yet" from all of them — which then stood until the panel was closed and
 * reopened. They ask again when the host says there is something new to ask.
 */
const askAgain = new Set<() => void>();

/**
 * Where a control's "ask again" also goes while something short-lived is building its controls.
 *
 * The item dialog is opened and closed many times over a panel's life, and every control it built
 * would otherwise go on being asked again for the rest of it — against a form that is no longer on
 * screen. What it registers here is what it takes away when it closes.
 */
let collecting: Set<() => void> | undefined;

/**
 * The item dialog currently open, as the two things that can happen to it from outside: a save
 * takes what is in it, and a re-render takes the list it was editing away. Only ever one, since
 * the dialog covers the page it was opened from.
 */
let openDialog: { readonly finish: () => void; readonly dismiss: () => void } | undefined;

function registerAsk(ask: () => void): void {
    askAgain.add(ask);
    collecting?.add(ask);
}

/**
 * Questions to the host are answered out of band, and several controls have one outstanding at
 * once. A token per question, and an answer for a token nobody is waiting for any more — because
 * the form re-rendered, or the field was typed in again — is dropped rather than applied to
 * whatever is on screen now.
 */
const waiting = new Map<number, (message: SettingsMsg.ToView) => void>();
let nextToken = 0;

function askHost<T extends SettingsMsg.ToView>(
    build: (token: number) => SettingsMsg.ToHost,
    onAnswer: (message: T) => void
): void {
    const token = ++nextToken;
    waiting.set(token, (message) => onAnswer(message as T));
    vscode.postMessage(build(token));
}

function answer(token: number, message: SettingsMsg.ToView): void {
    const handler = waiting.get(token);
    waiting.delete(token);
    handler?.(message);
}

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

    // A state arriving while an item is open replaces the list the dialog was editing, so the
    // dialog goes with it rather than writing back into a form that is no longer on the page.
    openDialog?.dismiss();

    renderScopes(state);
    renderNotice(state);
    renderPending();

    const form = document.getElementById('form')!;
    form.textContent = '';
    askAgain.clear();

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
    /** `"server"` — the values are a fact about the solution, so the host is asked for them. */
    'x-choices'?: string;
    /** Several fields that together name a call shape; see {@link MemberShapeSpec}. */
    'x-shape'?: MemberShapeSpec;
    /** Groups of fields of which exactly one group is filled in; see {@link ExclusiveGroup}. */
    'x-exclusive'?: readonly ExclusiveGroup[];
    /** On a field: the sibling field whose value decides whether this one applies at all. */
    'x-when'?: FieldCondition;
    /** On a list item: the fields that together say which item this is, in a collapsed row. */
    'x-summary'?: readonly string[];
}

/**
 * Which of an item's fields name a class, a member on it, and a position in its parameter list.
 *
 * Declared by the schema rather than recognised by name here, so the next setting that names a
 * call shape gets the same editor by saying so rather than by being special-cased in this file.
 */
interface MemberShapeSpec {
    kind: string;
    /** The field holding the declaring type's full name. */
    type: string;
    /** The field holding the member name. */
    member: string;
    /**
     * The field holding the positional parameter types. Absent where the shape names something
     * with no parameter list — a property or a field — and the line is then a plain member name.
     */
    parameters?: string;
    /** Field name → what that parameter carries, in words: `{ keyIndex: "key" }`. */
    positions?: Record<string, string>;
    /** What the row is called: `Method`, `Property`. Defaults to `Method`. */
    label?: string;
    /** Which kinds of member the setting can name, of `method`, `indexer`, `property`, `field`. */
    memberKinds?: readonly string[];
}

/**
 * Two ways of saying the same thing, of which an item says one: a set is a query against a
 * connection or a list of values written out, and it is never both.
 *
 * Shown as a choice rather than as every field at once, because the fields of the alternative that
 * is not chosen are not merely empty — writing them is what makes the item ambiguous.
 */
interface ExclusiveGroup {
    readonly alternatives: readonly Alternative[];
}

interface Alternative {
    readonly title: string;
    readonly fields: readonly string[];
}

/** A field that only applies while a sibling field says one of these values. */
interface FieldCondition {
    readonly field: string;
    readonly equals: readonly string[];
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
        blurb.append(prose(node.description));
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
        blurb.append(prose(node.description));
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

    return dropdown(select);
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
    return dropdown(select);
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

/**
 * A list of objects, each shown as one line and edited in a dialog.
 *
 * Every item used to render its whole form inline, which turned a list of six lookups into six
 * repeated forms and made the page a wall: what a list says — which members it binds, in what
 * order — was the one thing it could not be read for. A line per item says that, and the form is
 * where it has always been, one click away.
 *
 * The items are held here as values rather than read back out of the DOM, because a dialog only
 * has the item being edited on screen and the other five still have to be written.
 */
function objectListControl(itemSchema: SchemaNode, path: string[]): HTMLElement {
    const box = document.createElement('div');
    box.className = 'item-editor block';

    const list = document.createElement('div');
    list.className = 'item-list';
    box.append(list);

    const own = ownValue(path);
    const items: Record<string, unknown>[] = Array.isArray(own)
        ? own
            .filter((entry): entry is Record<string, unknown> => !!entry && typeof entry === 'object')
            .map((entry) => ({ ...entry }))
        : [];

    const add = addButton('Add item', () =>
        edit({}, (value) => {
            items.push(value);
            return items.length - 1;
        })
    );

    function commit(): void {
        const result = items.filter((item) => Object.keys(item).length > 0);
        setIfChanged(path, result.length === 0 ? null : result);
    }

    /**
     * Opens the dialog on a copy, and puts what came back where `place` says. Cancelling leaves
     * the list exactly as it was, which is what makes an item added and then thought better of
     * disappear rather than arrive empty.
     */
    function edit(
        value: Record<string, unknown>,
        place: (edited: Record<string, unknown>) => number,
        existing?: number
    ): void {
        openItemDialog({
            title: itemSchema.title ?? 'Item',
            itemSchema,
            value,
            listPath: path,
            onDone: (edited) => {
                // An item edited down to nothing is not an item. `commit` already filters it out
                // of the value, so leaving it in the list would show a "(empty)" line for something
                // the file does not have — the same disagreement Remove exists to avoid, so it
                // ends the same way.
                if (Object.keys(edited).length === 0) {
                    if (existing !== undefined) {
                        items.splice(existing, 1);
                    }
                    draw();
                    commit();
                    add.focus();
                    return;
                }

                const at = place(edited);
                draw();
                commit();
                focusRow(at);
            },
            // Back where it was opened from: the row for an item that was already there, and the
            // button that would have made one for an item that never arrived.
            onCancel: () => (existing === undefined ? add.focus() : focusRow(existing)),
        });
    }

    function focusRow(index: number): void {
        const row = list.children[index] as HTMLElement | undefined;
        row?.querySelector<HTMLButtonElement>('.item-edit')?.focus();
    }

    function draw(): void {
        list.textContent = '';

        if (items.length === 0) {
            const empty = document.createElement('p');
            empty.className = 'item-empty';
            empty.textContent = 'Nothing here yet.';
            list.append(empty);
            return;
        }

        items.forEach((item, index) => {
            const row = document.createElement('div');
            row.className = 'item-summary';

            const summary = document.createElement('span');
            summary.className = 'item-summary-text';
            summary.textContent = summarize(itemSchema, item);
            summary.title = summary.textContent;

            const open = document.createElement('button');
            open.type = 'button';
            open.className = 'linklike item-edit';
            open.textContent = 'Edit';
            open.addEventListener('click', () =>
                edit(
                    { ...item },
                    (edited) => {
                        items[index] = edited;
                        return index;
                    },
                    index
                )
            );

            const drop = document.createElement('button');
            drop.type = 'button';
            drop.className = 'linklike remove';
            drop.textContent = 'Remove';
            drop.addEventListener('click', () => {
                items.splice(index, 1);
                draw();
                commit();
                add.focus();
            });

            row.append(summary, open, drop);
            list.append(row);
        });
    }

    draw();
    box.append(add);
    return box;
}

/**
 * One item as a line: what it names, in the words the form would show for it.
 *
 * A shape says itself — the call is the item — and everything else is composed from the fields the
 * schema nominates. An item with nothing in it still gets a line, because "empty" is a state
 * somebody has to be able to see and fix.
 */
function summarize(itemSchema: SchemaNode, value: Record<string, unknown>): string {
    const parts: string[] = [];
    const shape = itemSchema['x-shape'];
    const owned = shape ? shapeFields(shape) : new Set<string>();

    if (shape) {
        const call = shapeSummary(shape, value);
        if (call !== '') {
            parts.push(call);
        }
    }

    const named = itemSchema['x-summary'];
    const fields = named ?? properties(itemSchema).map(([name]) => name);

    // Without a schema's say-so, a field or two beyond the shape: the point of the line is to tell
    // one item from its neighbours, and the whole item spelled out is the wall this replaced.
    const room = named ? fields.length : shape ? 1 : 2;
    let taken = 0;

    for (const name of fields) {
        if (owned.has(name) || taken >= room) {
            continue;
        }
        const written = summaryText(value[name]);
        if (written !== '') {
            parts.push(written);
            taken++;
        }
    }

    return parts.length === 0 ? `${itemSchema.title ?? 'Item'} (empty)` : parts.join('  ·  ');
}

/** The call a shape names, written the way the shape editor's own line writes it. */
function shapeSummary(shape: MemberShapeSpec, value: Record<string, unknown>): string {
    const type = text(value[shape.type]);
    const member = text(value[shape.member]);
    if (type === '' && member === '') {
        return '';
    }

    const stored =
        shape.parameters !== undefined && Array.isArray(value[shape.parameters])
            ? (value[shape.parameters] as unknown[]).map(text)
            : undefined;

    return formatCall(type, member, stored && stored.length > 0 ? stored : undefined);
}

function summaryText(value: unknown): string {
    if (value === undefined || value === null || value === '') {
        return '';
    }
    return Array.isArray(value) ? value.map(text).join(', ') : String(value);
}

/**
 * The per-item form, and the reader that turns it back into the item.
 *
 * The same rows the list used to draw inline, now the contents of the dialog. Fields the item does
 * not currently have — the alternative that is not chosen, a field whose condition does not hold —
 * are marked rather than removed, so that what is on screen and what is written stay one thing.
 */
function itemForm(
    itemSchema: SchemaNode,
    value: Record<string, unknown>,
    listPath: readonly string[]
): { body: HTMLElement; read: () => Record<string, unknown> } {
    const body = document.createElement('div');
    body.className = 'item-form';

    const shape = itemSchema['x-shape'];
    const owned = shape ? shapeFields(shape) : new Set<string>();

    // A field belonging to an alternative is drawn by the group, in the place the first of the
    // group's fields would have been — so the choice sits where the fields it governs are.
    const groups = itemSchema['x-exclusive'] ?? [];
    const claimedBy = new Map<string, ExclusiveGroup>();
    for (const group of groups) {
        for (const alternative of group.alternatives) {
            for (const field of alternative.fields) {
                claimedBy.set(field, group);
            }
        }
    }

    if (shape) {
        body.append(memberShapeEditor(shape, itemSchema, value, listPath));
    }

    const drawn = new Set<ExclusiveGroup>();
    for (const [name, child] of properties(itemSchema)) {
        // The fields a shape editor owns are drawn by it, together and in its own order, rather
        // than as five text boxes that only mean something read as a group.
        if (owned.has(name)) {
            continue;
        }

        const group = claimedBy.get(name);
        if (group) {
            if (!drawn.has(group)) {
                drawn.add(group);
                body.append(exclusiveGroup(group, itemSchema, value, listPath));
            }
            continue;
        }

        body.append(itemRow(name, child, value[name], listPath));
    }

    // One listener for the whole form: any field changing may be the one another field's condition
    // reads, and re-checking every condition is cheaper than working out which.
    const recheck = () => applyConditions(body);
    body.addEventListener('change', recheck);
    body.addEventListener('input', recheck);
    applyConditions(body);

    return { body, read: () => readFields(body) };
}

/**
 * The choice between two ways of filling an item in, as a row of plain words.
 *
 * Which one is chosen to start with is which one the item already says something under — a set
 * with a query opens on Query — and the first, for an item that says nothing yet.
 */
function exclusiveGroup(
    group: ExclusiveGroup,
    itemSchema: SchemaNode,
    value: Record<string, unknown>,
    listPath: readonly string[]
): HTMLElement {
    const box = document.createElement('div');
    box.className = 'alternatives';

    const choice = document.createElement('div');
    choice.className = 'alt-choice';
    choice.setAttribute('role', 'radiogroup');
    box.append(choice);

    // Said only while an alternative that is not on screen still holds something, because that is
    // the only time the choice decides the fate of anything already written.
    const note = document.createElement('p');
    note.className = 'item-help';
    note.hidden = true;
    box.append(note);

    const filled = (field: string) => {
        const held = value[field];
        return (
            held !== undefined &&
            held !== null &&
            held !== '' &&
            !(Array.isArray(held) && held.length === 0)
        );
    };

    const active = Math.max(
        0,
        group.alternatives.findIndex((alternative) => alternative.fields.some(filled))
    );

    const panels: HTMLElement[] = [];
    const name = `alt-${++nextToken}`;

    group.alternatives.forEach((alternative, index) => {
        const option = document.createElement('label');
        option.className = 'alt-option';

        const radio = document.createElement('input');
        radio.type = 'radio';
        radio.name = name;
        radio.checked = index === active;
        radio.addEventListener('change', () => choose(index));
        // A click on the option that is already chosen changes nothing and so raises no `change`,
        // but it is still somebody saying which alternative they mean — which is the whole of what
        // is being waited for here.
        radio.addEventListener('click', () => choose(index));

        option.append(radio, document.createTextNode(alternative.title));
        choice.append(option);

        const panel = document.createElement('div');
        panel.className = 'alt-fields';
        for (const field of alternative.fields) {
            const child = itemSchema.properties?.[field];
            if (child) {
                panel.append(itemRow(field, child, value[field], listPath));
            }
        }

        panels.push(panel);
        box.append(panel);
    });

    /**
     * Whether somebody has said, here, which alternative they mean.
     *
     * Opening on an alternative is not the same as choosing it. An item written before this choice
     * existed can have both alternatives filled in, and dropping the one the dialog happened not to
     * open on would throw away a value nobody was asked about — the item would come back out of the
     * dialog missing something, with nothing on screen having said so.
     */
    let decided = false;

    function choose(chosen: number): void {
        decided = true;
        show(chosen);
    }

    /**
     * Only the chosen alternative is on screen, and only it is written — once the choice has been
     * made. Until then an alternative that is off screen but says something is written back
     * unchanged; one that says nothing has nothing to lose and is dropped from the start.
     */
    function show(chosen: number): void {
        const holding: string[] = [];

        panels.forEach((panel, index) => {
            const chosenHere = index === chosen;
            const holds = group.alternatives[index].fields.some(filled);
            if (!chosenHere && holds) {
                holding.push(group.alternatives[index].title);
            }

            panel.hidden = !chosenHere;
            panel.dataset.omit = chosenHere || (!decided && holds) ? 'false' : 'true';
        });

        note.hidden = decided || holding.length === 0;
        note.textContent =
            `${holding.join(' and ')} still says something, and is being left as it is. `
            + 'Choosing an option here keeps only that option’s fields.';
    }

    show(active);
    return box;
}

/**
 * Which conditional rows apply right now. A row whose condition does not hold is off the screen
 * and out of the item — a lookup taking its file name from a constant has no parameter index, and
 * writing one anyway is a setting that says two things at once.
 */
function applyConditions(root: HTMLElement): void {
    for (const row of root.querySelectorAll<HTMLElement>('[data-when-field]')) {
        const driver = root.querySelector<HTMLElement>(
            `[data-field="${CSS.escape(row.dataset.whenField!)}"]`
        );
        const equals = JSON.parse(row.dataset.whenEquals ?? '[]') as string[];
        // Compared without regard to case, because the server reads these values that way too: a
        // file saying `Argument` is fully valid to it, and hiding the row it drives — which also
        // omits the field on commit — would drop a working setting over a capital letter.
        const said = fieldText(driver ?? row).toLowerCase();
        const shown = driver !== null && equals.some((value) => value.toLowerCase() === said);

        row.hidden = !shown;
        row.dataset.omit = shown ? 'false' : 'true';
    }
}

/**
 * Says on an element what `applyConditions` reads back: which sibling field decides it, and the
 * answers that keep it.
 *
 * `itemRow` stamps the row it builds, and so must anything drawn in place of a row — the shape
 * editor draws some of its fields as a line of chips over a hidden input, and neither of those is
 * an `item-row`. A condition nobody stamps is a condition nobody applies: the field stays on
 * screen and its value goes to the file whatever the field it depends on says.
 */
function stampCondition(element: HTMLElement, when: FieldCondition | undefined): void {
    if (!when) {
        return;
    }
    element.dataset.whenField = when.field;
    element.dataset.whenEquals = JSON.stringify(when.equals);
}

/** What a field currently says, for the conditions that read one. */
function fieldText(field: HTMLElement): string {
    if (field instanceof HTMLSelectElement || field instanceof HTMLInputElement) {
        return field.value;
    }
    return field.dataset.value ?? '';
}

/** Every field on the form that still applies, as the item to write. */
function readFields(root: HTMLElement): Record<string, unknown> {
    const value: Record<string, unknown> = {};
    for (const field of root.querySelectorAll<HTMLElement>('[data-field]')) {
        if (field.closest('[data-omit="true"]')) {
            continue;
        }
        const parsed = readItemField(field);
        if (parsed !== undefined) {
            value[field.dataset.field!] = parsed;
        }
    }
    return value;
}

/**
 * The item's form, over the page, until it is done with.
 *
 * A dialog rather than an expanding row because the form is tall — a shape editor with its
 * suggestions and its evidence is most of a screen — and because editing one item is a thing with
 * a beginning and an end: Done writes it into the list, Escape and clicking away leave it alone.
 * Nothing here reaches a file; the list commits into the pending edits as it always did.
 */
function openItemDialog(options: {
    readonly title: string;
    readonly itemSchema: SchemaNode;
    readonly value: Record<string, unknown>;
    readonly listPath: readonly string[];
    readonly onDone: (value: Record<string, unknown>) => void;
    readonly onCancel: () => void;
}): void {
    // The dialog covers the page it was opened from, and a second one over the first would leave
    // the first unreachable and still holding an unread edit. The Edit and Add buttons are behind
    // the overlay, but the keyboard can still reach them, so the activation is refused here rather
    // than guarded at every place that offers one.
    if (openDialog) {
        return;
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';

    const panel = document.createElement('div');
    panel.className = 'modal';
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-modal', 'true');
    panel.setAttribute('aria-label', options.title);

    const heading = document.createElement('h3');
    heading.className = 'modal-title';
    heading.textContent = options.title;

    // The controls the dialog builds ask the server for what they offer, and would go on being
    // asked again after it is closed. What it registered is what it takes with it.
    const registered = new Set<() => void>();
    const outer = collecting;
    collecting = registered;
    const form = itemForm(options.itemSchema, options.value, options.listPath);
    collecting = outer;

    const body = document.createElement('div');
    body.className = 'modal-body';
    body.append(form.body);

    const actions = document.createElement('div');
    actions.className = 'modal-actions';

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'linklike';
    cancel.textContent = 'Cancel';
    cancel.addEventListener('click', () => close(false));

    const done = document.createElement('button');
    done.type = 'button';
    done.className = 'save';
    done.textContent = 'Done';
    done.addEventListener('click', () => close(true));

    actions.append(cancel, done);
    panel.append(heading, body, actions);
    overlay.append(panel);

    function close(commit: boolean): void {
        openDialog = undefined;
        document.removeEventListener('keydown', onEscape);

        // A field being typed in has not been read yet, and the shape editor's line is one of them.
        if (document.activeElement instanceof HTMLElement) {
            document.activeElement.blur();
        }

        const edited = commit ? form.read() : undefined;

        for (const ask of registered) {
            askAgain.delete(ask);
        }
        overlay.remove();

        if (edited) {
            options.onDone(edited);
        } else {
            options.onCancel();
        }
    }

    // Clicking the page behind the dialog leaves the item alone; clicking inside it does not.
    overlay.addEventListener('mousedown', (event) => {
        if (event.target === overlay) {
            close(false);
        }
    });

    /**
     * Escape leaves the item alone, from wherever the focus happens to be.
     *
     * On the overlay it only worked while the focus was inside the dialog, and the focus is not
     * always there: clicking the page behind it, or a control that gave the focus back to the body,
     * left the one key that cancels doing nothing at all.
     *
     * The controls inside stop Escape from reaching this while they have something of their own
     * open — a suggestion list closes before the dialog does — and so does the search box while
     * there is a query to clear, because this listens where they bubble to rather than ahead of
     * them.
     */
    const onEscape = (event: KeyboardEvent): void => {
        if (event.key === 'Escape') {
            event.stopPropagation();
            close(false);
        }
    };
    document.addEventListener('keydown', onEscape);

    openDialog = { finish: () => close(true), dismiss: () => close(false) };

    document.body.append(overlay);
    body.querySelector<HTMLElement>('input:not([type="hidden"]), select, textarea, button')?.focus();
}

/**
 * One labelled field of one list item, with the sentence the schema gives it.
 *
 * The label used to be the raw property name and there was no sentence at all, which is what made
 * the page unreadable to anyone who had not written the code behind it: `rootInterpretation` is
 * not a question anybody can answer from the word alone.
 */
function itemRow(
    name: string,
    schema: SchemaNode,
    value: unknown,
    listPath: readonly string[]
): HTMLElement {
    const row = document.createElement('div');
    row.className = 'item-row';

    // A field that only applies under some other field's answer says so here; the form reads these
    // back in `applyConditions`, which is also what hides the row and drops the value.
    stampCondition(row, schema['x-when']);

    const label = document.createElement('label');
    label.textContent = schema.title ?? name;
    // The key as the file spells it, for whoever edits the JSON by hand.
    label.title = name;
    row.append(label, itemField(name, schema, value, listPath));

    if (schema.description) {
        const blurb = document.createElement('p');
        blurb.className = 'item-help';
        blurb.append(prose(schema.description));
        row.append(blurb);
    }

    return row;
}

/** One field of one list item. `data-field` carries the property name for readItemField. */
function itemField(
    name: string,
    schema: SchemaNode,
    value: unknown,
    listPath: readonly string[]
): HTMLElement {
    const type = typeOf(schema);

    if (type === 'boolean') {
        const select = document.createElement('select');
        select.dataset.field = name;
        select.dataset.kind = 'boolean';
        addOption(select, '', 'Default');
        addOption(select, 'true', 'On');
        addOption(select, 'false', 'Off');
        select.value = typeof value === 'boolean' ? String(value) : '';
        return dropdown(select);
    }

    // A closed list is a closed list wherever it appears. Inside an item it used to be a text box,
    // so the six spellings of `rootSource` were something you had to already know.
    if (Array.isArray(schema.enum) && schema.enum.length > 0) {
        const select = document.createElement('select');
        select.dataset.field = name;
        select.dataset.kind = 'text';
        addOption(select, '', 'Default');
        for (const choice of schema.enum) {
            addOption(select, String(choice), String(choice));
        }

        const current = value === undefined || value === null ? '' : String(value);
        // A value the schema does not list is still what the file says; showing it beats
        // silently rewriting it to the first option the moment the item is committed.
        if (current !== '' && !schema.enum.some((choice) => String(choice) === current)) {
            addOption(select, current, `${current} (not a known value)`);
        }
        select.value = current;
        return dropdown(select);
    }

    if (schema['x-choices'] === 'server') {
        const path = `${listPath.join('.')}[].${name}`;
        return type === 'array'
            ? choiceListField(name, path, value)
            : choiceSelectField(name, path, value);
    }

    const input = document.createElement('input');
    input.type = 'text';
    input.dataset.field = name;

    if (type === 'array') {
        input.dataset.kind = 'list';
        input.placeholder = 'Comma-separated';
        input.value = Array.isArray(value) ? value.join(', ') : '';
    } else if (type === 'integer' || type === 'number') {
        // Written as a number rather than as the text of one. A quoted `"0"` where the schema says
        // integer does not fail the field — it fails the whole file, and every setting in it goes
        // back to its default with nothing in the editor to say so.
        input.type = 'number';
        input.dataset.kind = 'number';
        input.value = value === undefined || value === null ? '' : String(value);
    } else {
        input.dataset.kind = 'text';
        input.value = value === undefined || value === null ? '' : String(value);
    }

    return input;
}

/**
 * Several of what the solution offers, in the order they are tried — a lookup's fallbacks.
 *
 * A dropdown with the chosen values in front of it rather than a column of checkboxes, because
 * order is the whole meaning of this field and a checkbox column cannot show it: "custom, then
 * globalCustom, then globalLocal" is a sentence, and six ticks in a list are not. What is chosen
 * reads left to right; what is available is behind the caret, where a list of things you are not
 * using belongs.
 *
 * Choosing something already chosen moves it to the end, which is also how it is reordered.
 */
function choiceListField(name: string, path: string, value: unknown): HTMLElement {
    const box = document.createElement('div');
    box.className = 'picker';
    box.dataset.field = name;
    box.dataset.kind = 'chips';

    let chosen: string[] = Array.isArray(value) ? value.map(String) : [];
    let offered: readonly SettingsMsg.Choice[] = [];
    box.dataset.value = JSON.stringify(chosen);

    const face = document.createElement('button');
    face.type = 'button';
    face.className = 'picker-face';
    face.setAttribute('aria-haspopup', 'listbox');
    face.setAttribute('aria-expanded', 'false');

    const pills = document.createElement('span');
    pills.className = 'pills';

    const caret = document.createElement('span');
    caret.className = 'chevron';
    caret.setAttribute('aria-hidden', 'true');

    face.append(pills, caret);

    const menu = document.createElement('div');
    menu.className = 'picker-menu';
    menu.setAttribute('role', 'listbox');
    menu.hidden = true;

    box.append(face, menu);

    const commit = () => {
        box.dataset.value = JSON.stringify(chosen);
        drawPills();
        drawMenu();
    };

    /** Everything to offer: what the solution defines, plus what the file already named. */
    const rows = (): SettingsMsg.Choice[] => {
        const known = new Set(offered.map((choice) => choice.value));
        return [
            ...offered,
            // A value the solution no longer defines stays offerable and stays marked. Dropping
            // it on the next save is data loss dressed up as tidying.
            ...chosen
                .filter((id) => !known.has(id))
                .map((id) => ({ value: id, detail: 'not defined in this solution' })),
        ];
    };

    function drawPills(): void {
        pills.textContent = '';

        if (chosen.length === 0) {
            const empty = document.createElement('span');
            empty.className = 'picker-empty';
            empty.textContent = 'Choose where else to look';
            pills.append(empty);
            return;
        }

        for (const id of chosen) {
            const pill = document.createElement('span');
            pill.className = 'pill';

            const text = document.createElement('span');
            text.textContent = id;

            const drop = document.createElement('span');
            drop.className = 'pill-remove';
            drop.setAttribute('role', 'button');
            drop.setAttribute('aria-label', `Remove ${id}`);
            drop.textContent = '×';
            drop.addEventListener('click', (event) => {
                // The pill sits on the button that opens the menu; removing one is not opening it.
                event.stopPropagation();
                chosen = chosen.filter((candidate) => candidate !== id);
                commit();
            });

            pill.append(text, drop);
            pills.append(pill);
        }
    }

    function drawMenu(): void {
        menu.textContent = '';
        const all = rows();

        if (all.length === 0) {
            const empty = document.createElement('p');
            empty.className = 'picker-note';
            empty.textContent = 'Nothing to choose from yet.';
            menu.append(empty);
            return;
        }

        for (const choice of all) {
            const picked = chosen.includes(choice.value);

            const option = document.createElement('div');
            option.className = 'picker-option';
            option.tabIndex = -1;
            option.setAttribute('role', 'option');
            option.setAttribute('aria-selected', String(picked));

            const tick = document.createElement('span');
            tick.className = 'picker-tick';
            tick.setAttribute('aria-hidden', 'true');
            tick.textContent = picked ? '✓' : '';

            const label = document.createElement('span');
            label.className = 'picker-name';
            label.textContent = choice.value;

            option.append(tick, label);

            if (choice.detail) {
                const detail = document.createElement('span');
                detail.className = 'picker-detail';
                detail.textContent = choice.detail;
                option.append(detail);
            }

            option.addEventListener('click', () => {
                chosen = picked
                    ? chosen.filter((id) => id !== choice.value)
                    : [...chosen.filter((id) => id !== choice.value), choice.value];
                commit();
            });

            menu.append(option);
        }
    }

    const open = (wanted: boolean) => {
        menu.hidden = !wanted;
        face.setAttribute('aria-expanded', String(wanted));
    };

    face.addEventListener('click', () => open(menu.hidden));

    box.addEventListener('keydown', (event) => {
        const key = (event as KeyboardEvent).key;

        if (key === 'Escape' && !menu.hidden) {
            open(false);
            face.focus();
            event.stopPropagation();
            return;
        }

        if (key === 'ArrowDown' || key === 'ArrowUp') {
            const options = [...menu.querySelectorAll<HTMLElement>('.picker-option')];
            if (options.length === 0) {
                return;
            }
            open(true);
            const at = options.indexOf(document.activeElement as HTMLElement);
            const next = key === 'ArrowDown' ? at + 1 : at - 1;
            options[Math.max(0, Math.min(options.length - 1, next))].focus();
            event.preventDefault();
        }

        if ((key === 'Enter' || key === ' ') && document.activeElement?.matches('.picker-option')) {
            (document.activeElement as HTMLElement).click();
            event.preventDefault();
        }
    });

    // Anywhere else in the page closes it, the way every dropdown does.
    box.addEventListener('focusout', (event) => {
        const next = (event as FocusEvent).relatedTarget;
        if (!(next instanceof Node) || !box.contains(next)) {
            open(false);
        }
    });

    const ask = () =>
        askHost<SettingsMsg.SettingChoices>(
            (token) => ({ type: 'askChoices', token, path }),
            (message) => {
                offered = message.items;
                drawMenu();
            }
        );

    drawPills();
    drawMenu();
    ask();
    registerAsk(ask);

    return box;
}

/**
 * One string chosen from what the rest of the file offers — which value set a binding names, which
 * connection a set queries.
 *
 * The list arrives after the control does, so it is drawn twice: once with whatever the file
 * already says, and again once the answer comes back. A value the answer does not contain stays in
 * the list, marked — a binding naming a set that was renamed is wrong and should look wrong, not
 * quietly become the first option in the dropdown.
 */
function choiceSelectField(name: string, path: string, value: unknown): HTMLElement {
    const select = document.createElement('select');
    select.dataset.field = name;
    select.dataset.kind = 'text';

    const current = value === undefined || value === null ? '' : String(value);

    const draw = (offered: readonly SettingsMsg.Choice[]) => {
        select.textContent = '';
        addOption(select, '', 'Default');

        for (const choice of offered) {
            addOption(
                select,
                choice.value,
                choice.detail ? `${choice.value} — ${choice.detail}` : choice.value
            );
        }

        if (current !== '' && !offered.some((choice) => choice.value === current)) {
            addOption(select, current, `${current} (not defined in this file)`);
        }

        select.value = current;
    };

    const ask = () =>
        askHost<SettingsMsg.SettingChoices>(
            (token) => ({ type: 'askChoices', token, path }),
            (message) => draw(message.items)
        );

    draw([]);
    ask();
    registerAsk(ask);

    return dropdown(select);
}

/** The field's value for the file, or undefined when it should be omitted from the item. */
function readItemField(field: HTMLElement): unknown {
    const kind = field.dataset.kind;

    if (kind === 'chips') {
        const chosen = JSON.parse(field.dataset.value ?? '[]') as string[];
        return chosen.length > 0 ? chosen : undefined;
    }

    if (field instanceof HTMLSelectElement) {
        return kind === 'boolean'
            ? field.value === ''
                ? undefined
                : field.value === 'true'
            : field.value === ''
                ? undefined
                : field.value;
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
    if (kind === 'number') {
        const parsed = Number(text);
        return Number.isFinite(parsed) ? parsed : undefined;
    }
    return text;
}

// ---------------------------------------------------------------------------
// Naming a call shape
// ---------------------------------------------------------------------------

/** Every field the shape editor draws itself. */
function shapeFields(shape: MemberShapeSpec): Set<string> {
    const fields = new Set<string>([shape.type, shape.member]);
    // A shape naming something with no parameter list — a property, a field — has neither of these,
    // and the fields it does not claim are drawn as ordinary rows.
    if (shape.parameters !== undefined) {
        fields.add(shape.parameters);
    }
    for (const field of Object.keys(shape.positions ?? {})) {
        fields.add(field);
    }
    return fields;
}

/**
 * One editor over the fields that together name a method: the class, the member, and the signature
 * that tells one overload from its siblings.
 *
 * Written as the call itself — `Contoso.Web.Localizer.GetString(string, *)` — because that is the
 * form the person is copying from. They are looking at a call site; asking them to take it apart
 * into three boxes is asking them to do the parsing, and getting it wrong in any one of the three
 * binds nothing, silently, forever. So the line is typed the way it reads, split on the way to the
 * file, and completed against the solution as it is typed: classes, then the members on the class
 * that resolved, then the overloads that share the member's name.
 *
 * Below it, the same answer as evidence rather than as completion — which overloads this entry
 * currently selects, and which parameter of one carries the key. "2 of 3 overloads match" is the
 * fact three text boxes could never state.
 */
function memberShapeEditor(
    shape: MemberShapeSpec,
    itemSchema: SchemaNode,
    value: Record<string, unknown>,
    listPath: readonly string[]
): HTMLElement {
    void listPath;

    const box = document.createElement('div');
    box.className = 'shape';

    // Whether the line has a signature on it at all. A property is named and that is the whole of
    // it; offering parentheses there would offer a distinction that does not exist.
    const takesParameters = shape.parameters !== undefined;
    const what = shape.label ?? 'Method';

    // The fields the line stands for. Hidden rather than dropped: the form reads them like every
    // other field in the item, so what lands in the file is unchanged by any of this.
    const stored: readonly string[] | undefined =
        takesParameters && Array.isArray(value[shape.parameters!])
            ? (value[shape.parameters!] as unknown[]).map(text)
            : undefined;

    const typeField = hiddenField(shape.type, 'text', value[shape.type]);
    const memberField = hiddenField(shape.member, 'text', value[shape.member]);
    const parametersField = takesParameters
        ? hiddenField(shape.parameters!, 'list', stored?.join(', '))
        : undefined;

    const row = document.createElement('div');
    row.className = 'item-row';

    const label = document.createElement('label');
    label.textContent = what;
    label.htmlFor = `call-${++nextToken}`;

    const well = document.createElement('div');
    well.className = 'call';

    const input = document.createElement('input');
    input.type = 'text';
    input.id = label.htmlFor;
    input.className = 'call-input';
    input.spellcheck = false;
    input.autocomplete = 'off';
    input.placeholder = takesParameters
        ? 'Namespace.Class.Method(string, *)'
        : `Namespace.Class.${what}`;
    input.setAttribute('role', 'combobox');
    input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-expanded', 'false');
    input.value = formatCall(
        text(value[shape.type]),
        text(value[shape.member]),
        stored !== undefined && stored.length > 0 ? stored : undefined
    );

    const menu = document.createElement('div');
    menu.className = 'call-menu';
    menu.setAttribute('role', 'listbox');
    menu.hidden = true;

    well.append(input, menu);
    row.append(label, well);

    const help = document.createElement('p');
    help.className = 'item-help';
    help.textContent = takesParameters
        ? 'Leave the class off to match any class declaring the method. Add parentheses to pick one '
        + 'overload, and * for a parameter of any type.'
        : `Leave the class off to match any class declaring the ${what.toLowerCase()}.`;
    row.append(help);

    box.append(row);

    const status = document.createElement('p');
    status.className = 'shape-status';
    box.append(status);

    const overloads = document.createElement('div');
    overloads.className = 'shape-overloads';
    box.append(overloads);

    // The positions are stored on hidden fields so `commit` reads them like any other, and set by
    // clicking a parameter rather than by counting one.
    const positions = new Map<string, HTMLInputElement>();
    const positionRows = document.createElement('div');
    positionRows.className = 'shape-positions';
    box.append(positionRows);

    for (const field of Object.keys(shape.positions ?? {})) {
        // 'number', because a position is an index: see itemField for what a quoted one costs.
        const hidden = hiddenField(field, 'number', value[field]);
        // A position field can be conditional like any other — a lookup taking its file name from a
        // constant has no parameter to point at — and the schema says so on the field, not on the
        // shape. The field is drawn here rather than as an ordinary row, so the condition has to be
        // carried onto what is drawn instead: this input, and the chips row `drawPositions` builds.
        stampCondition(hidden, itemSchema.properties?.[field]?.['x-when']);
        positions.set(field, hidden);
        box.append(hidden);
    }

    box.append(typeField, memberField);
    if (parametersField) {
        box.append(parametersField);
    }

    // ---- what the line says ------------------------------------------------------------------

    /** The line taken apart, onto the fields that are written to the file. */
    function sync(): void {
        const call = parseCall(input.value, takesParameters);
        typeField.value = call.type;
        memberField.value = call.member;
        if (parametersField) {
            parametersField.value = (call.parameters ?? []).join(', ');
        }
    }

    let timer: number | undefined;
    const refresh = () => {
        sync();
        window.clearTimeout(timer);
        timer = window.setTimeout(ask, 200);
    };

    input.addEventListener('input', refresh);

    /**
     * Two readings of one line, because a name being typed is ambiguous until the next character
     * arrives: `Contoso.Web.Localizer` is a class, and `Contoso.Web.Localizer.Get` is a class and
     * the start of a method on it. Both are asked, and the one that resolved the more specific
     * thing is the one shown — which is what makes the completion continue rather than restart
     * every time a dot is typed.
     */
    let generation = 0;

    function ask(): void {
        const call = parseCall(input.value, takesParameters);
        const mine = ++generation;

        let asMember: SettingsMsg.MemberShape | undefined;
        let asType: SettingsMsg.MemberShape | undefined;
        let outstanding = call.parameters === undefined ? 2 : 1;

        const arrived = () => {
            if (mine !== generation) {
                return;
            }
            if (--outstanding === 0) {
                show(pick(call, asMember, asType));
            }
        };

        askHost<SettingsMsg.MemberShape>(
            (token) => ({
                type: 'askMemberShape',
                token,
                containingType: call.type,
                memberName: call.member,
                parameterTypes: call.parameters ?? [],
                kinds: shape.memberKinds,
            }),
            (message) => {
                asMember = message;
                arrived();
            }
        );

        // A parenthesis cannot be part of a class name, so once there is one the line is no longer
        // half-written and there is nothing to read the second way.
        if (call.parameters === undefined) {
            askHost<SettingsMsg.MemberShape>(
                (token) => ({
                    type: 'askMemberShape',
                    token,
                    containingType: head(input.value),
                    memberName: '',
                    kinds: shape.memberKinds,
                }),
                (message) => {
                    asType = message;
                    arrived();
                }
            );
        }
    }

    /**
     * Which reading to believe, and the half-typed member name it should be narrowed by — which is
     * only a fragment under the reading that treated it as one; see {@link ask}.
     */
    function pick(
        call: Call,
        asMember: SettingsMsg.MemberShape | undefined,
        asType: SettingsMsg.MemberShape | undefined
    ): Reading {
        if (asMember && asMember.resolvedType !== undefined && asMember.matches.length > 0) {
            return { answer: asMember, typed: call.member, alsoClasses: [] };
        }
        if (asType && asType.resolvedType !== undefined) {
            // The whole line is a class, so there is no member half-typed yet: the menu offers
            // every one it declares.
            return { answer: asType, typed: '', alsoClasses: [] };
        }
        if (asMember && asMember.resolvedType !== undefined) {
            return { answer: asMember, typed: call.member, alsoClasses: [] };
        }

        // Nothing resolved, and `Modules.Localize` is either a class being typed or a method being
        // searched for with nothing to say which. Both answers are offered rather than one of them
        // guessed at — the methods first, since adopting one settles the whole line.
        return asMember && asMember.matches.length > 0
            ? {
                answer: asMember,
                typed: call.member,
                alsoClasses: asType?.typeSuggestions ?? [],
            }
            : { answer: asType ?? asMember, typed: '', alsoClasses: [] };
    }

    function show(reading: Reading): void {
        const answerMessage = reading.answer;

        overloads.textContent = '';
        positionRows.textContent = '';
        menu.textContent = '';

        if (!answerMessage) {
            open(false);
            return;
        }

        const matched = answerMessage.matches.filter((match) => match.matched);

        // Nothing resolved the class, so these came from a search of the whole solution and are
        // candidates rather than overloads of one method — each has to say where it lives.
        const searched = answerMessage.resolvedType === undefined;

        status.classList.toggle(
            'warn',
            !searched && answerMessage.matches.length > 0 && matched.length === 0
        );
        // A shape with no parameter list has no overloads to count: what there is to say is which
        // member it settled on.
        status.textContent = answerMessage.problem
            ? answerMessage.problem
            : answerMessage.matches.length === 0
                ? ''
                : !takesParameters
                    ? resolvedMember(matched[0] ?? answerMessage.matches[0])
                    : matched.length === answerMessage.matches.length
                        ? `${count(matched.length, 'overload')} — all of them.`
                        : `${matched.length} of ${count(answerMessage.matches.length, 'overload')} match.`;

        // Only against a settled class, where "these are the overloads this entry selects" is a
        // short, checkable claim. Search candidates are offered in the completion menu alone:
        // drawn here as well, twenty of them pushed the rest of the form off the screen while
        // saying nothing the menu was not already saying.
        if (!searched) {
            for (const match of answerMessage.matches) {
                overloads.append(overloadRow(match, 'overload', searched));
            }
        }

        drawMenu(reading);

        // Only against a settled class. Counting a parameter of one search hit out of forty says
        // nothing about the method that ends up being named.
        drawPositions(searched ? undefined : matched[0]);

        // The position rows were just built anew, and a row that has only this moment been put on
        // the page has never been through `applyConditions` — every server answer would otherwise
        // put a conditional row back on screen regardless of what its condition says.
        reapplyConditions();
    }

    /**
     * Re-checks the conditions across the whole item form, from inside a redraw of part of it.
     *
     * The form is the root because a condition names a sibling field — the dropdown that decides a
     * position row is an ordinary row elsewhere in the same dialog, not inside this editor.
     */
    function reapplyConditions(): void {
        const form = box.closest<HTMLElement>('.item-form');
        if (form) {
            applyConditions(form);
        }
    }

    /** What one match resolved to, in the words the line itself is written in. */
    function resolvedMember(match: SettingsMsg.ShapeMatch): string {
        return `${match.declaredBy}.${match.name}`;
    }

    /** One member, as a line that can be adopted by clicking it. */
    function overloadRow(
        match: SettingsMsg.ShapeMatch,
        className: string,
        searched: boolean
    ): HTMLElement {
        const line = document.createElement('button');
        line.type = 'button';
        line.className = match.matched ? `${className} matched` : className;
        line.title = match.kind
            ? `${resolvedMember(match)} — ${match.kind}`
            : resolvedMember(match);

        const written = document.createElement('span');
        written.textContent = match.signature;
        line.append(written);

        // Where a searched-for member lives, or — for a setting that binds several kinds at once —
        // which kind this one is. The same quiet column either way, and never both.
        const detail = searched ? match.declaredBy : takesParameters ? '' : match.kind ?? '';
        if (detail !== '') {
            const where = document.createElement('span');
            where.className = 'declared-by';
            where.textContent = detail;
            line.append(where);
        }

        line.addEventListener('click', () =>
            adopt(
                formatCall(
                    match.declaredBy,
                    match.name,
                    takesParameters
                        ? match.parameters.map((parameter) => parameter.type)
                        : undefined
                )
            )
        );
        return line;
    }

    /**
     * What to offer next: the overloads once a method is named, the methods once a class is, and
     * the classes until then. One rung at a time, because offering all three at once is offering a
     * list nobody can read.
     */
    function drawMenu(reading: Reading): void {
        const answerMessage = reading.answer;

        if (!answerMessage || document.activeElement !== input) {
            open(false);
            return;
        }

        if (answerMessage.matches.length > 0) {
            const searched = answerMessage.resolvedType === undefined;
            for (const match of answerMessage.matches) {
                menu.append(overloadRow(match, 'call-option', searched));
            }
            // The other reading of the same words; see `pick`.
            for (const name of reading.alsoClasses.slice(0, 20)) {
                menu.append(option(name, segment(head(input.value)), () => adopt(`${name}.`)));
            }
            open(true);
            return;
        }

        const owner = answerMessage.resolvedType;

        if (owner !== undefined && answerMessage.memberSuggestions.length > 0) {
            // The server answers with every name the class declares; which of them are worth
            // showing is whatever the person has typed of one so far.
            const typed = reading.typed;
            const offered = answerMessage.memberSuggestions.filter((name) =>
                contains(name, typed)
            );

            for (const name of offered.slice(0, 40)) {
                menu.append(
                    option(name, typed, () => adopt(formatCall(owner, name)))
                );
            }
            open(offered.length > 0);
            return;
        }

        if (answerMessage.typeSuggestions.length > 0) {
            const typed = segment(head(input.value));
            for (const name of answerMessage.typeSuggestions.slice(0, 40)) {
                menu.append(option(name, typed, () => adopt(`${name}.`)));
            }
            open(true);
            return;
        }

        open(false);
    }

    function option(name: string, typed: string, choose: () => void): HTMLElement {
        const line = document.createElement('button');
        line.type = 'button';
        line.className = 'call-option';
        line.title = name;

        // One element rather than the fragment itself. The row is a flex container — an overload
        // needs a signature and a place it is declared, one at each end — and a bare fragment's
        // pieces each became a column of their own, so the tail of a highlighted name was thrown
        // to the far right as if it were a second field.
        const label = document.createElement('span');
        label.append(highlight(name, typed));
        line.append(label);

        line.addEventListener('click', choose);
        return line;
    }

    /** Replaces the line with a resolved one and carries on from there. */
    function adopt(line: string): void {
        input.value = line;
        input.focus();
        input.setSelectionRange(line.length, line.length);
        sync();
        ask();
    }

    function drawPositions(example: SettingsMsg.ShapeMatch | undefined): void {
        // Parameters to choose from come from an overload the entry actually selects: naming the
        // key's position against an overload this shape does not bind would be arithmetic about
        // nothing.
        if (!example) {
            return;
        }

        for (const [field, carries] of Object.entries(shape.positions ?? {})) {
            const hidden = positions.get(field);
            if (!hidden) {
                continue;
            }

            const line = document.createElement('div');
            line.className = 'item-row';
            // The same condition the hidden field carries, so the chips go off the screen with the
            // value rather than inviting a choice that is about to be thrown away.
            stampCondition(line, itemSchema.properties?.[field]?.['x-when']);

            const name = document.createElement('label');
            name.textContent = itemSchema.properties?.[field]?.title ?? field;
            name.title = field;
            line.append(name);

            const chips = document.createElement('div');
            chips.className = 'chips';

            example.parameters.forEach((parameter, index) => {
                const chip = document.createElement('button');
                chip.type = 'button';
                chip.className = 'chip';
                chip.textContent = `${index}  ${parameter.name}`;
                chip.title = parameter.type;
                chip.setAttribute('aria-pressed', String(hidden.value === String(index)));

                if (hidden.value === String(index)) {
                    chip.classList.add('picked');
                }

                chip.addEventListener('click', () => {
                    hidden.value = String(index);
                    for (const sibling of chips.querySelectorAll<HTMLElement>('.chip')) {
                        sibling.classList.remove('picked');
                        sibling.setAttribute('aria-pressed', 'false');
                    }
                    chip.classList.add('picked');
                    chip.setAttribute('aria-pressed', 'true');
                });

                chips.append(chip);
            });

            line.append(chips);

            const blurb = document.createElement('p');
            blurb.className = 'item-help';
            blurb.textContent = `Which parameter carries the ${carries}.`;
            line.append(blurb);

            positionRows.append(line);
        }
    }

    // ---- opening and closing -----------------------------------------------------------------

    function open(wanted: boolean): void {
        menu.hidden = !wanted || menu.childElementCount === 0;
        input.setAttribute('aria-expanded', String(!menu.hidden));
        if (menu.hidden) {
            active = -1;
        }
    }

    let active = -1;

    const options = () => [...menu.querySelectorAll<HTMLElement>('.call-option')];

    function highlightActive(): void {
        options().forEach((line, index) => line.classList.toggle('active', index === active));
        options()[active]?.scrollIntoView({ block: 'nearest' });
    }

    input.addEventListener('focus', ask);

    input.addEventListener('keydown', (event) => {
        const all = options();

        if (event.key === 'Escape' && !menu.hidden) {
            open(false);
            event.stopPropagation();
            return;
        }

        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            if (all.length === 0) {
                return;
            }
            open(true);
            active = Math.max(
                0,
                Math.min(all.length - 1, active + (event.key === 'ArrowDown' ? 1 : -1))
            );
            highlightActive();
            event.preventDefault();
            return;
        }

        if (event.key === 'Enter' && !menu.hidden && active >= 0) {
            all[active]?.click();
            event.preventDefault();
        }
    });

    // Clicking a suggestion must not take the focus off the field it is completing — the line is
    // still being typed, and the panel commits when focus leaves the item.
    menu.addEventListener('mousedown', (event) => event.preventDefault());

    well.addEventListener('focusout', (event) => {
        const next = (event as FocusEvent).relatedTarget;
        if (!(next instanceof Node) || !well.contains(next)) {
            open(false);
        }
    });

    sync();
    ask();
    registerAsk(ask);

    return box;
}

/** A field the item's `commit` reads, drawn by something other than a text box. */
function hiddenField(name: string, kind: string, value: unknown): HTMLInputElement {
    const input = document.createElement('input');
    input.type = 'hidden';
    input.dataset.field = name;
    input.dataset.kind = kind;
    input.value = text(value);
    return input;
}

/** One of the two readings of a half-written line, once the server has answered it. */
interface Reading {
    readonly answer?: SettingsMsg.MemberShape;
    /** The part of a member name typed so far, which the suggestions are narrowed by. */
    readonly typed: string;
    /** Classes the other reading found, offered under the methods when both are possible. */
    readonly alsoClasses: readonly string[];
}

/** A call taken apart, as the three fields that are stored. */
interface Call {
    readonly type: string;
    readonly member: string;
    /** The parameter list, or undefined when the line has no parentheses — any overload. */
    readonly parameters?: readonly string[];
}

/** Everything before the parameter list, with a trailing dot dropped. */
function head(line: string): string {
    const open = line.indexOf('(');
    return (open < 0 ? line : line.slice(0, open)).trim().replace(/\.$/, '');
}

/** The last dotted segment — the part of a name somebody types from memory. */
function segment(name: string): string {
    const dot = name.lastIndexOf('.');
    return dot < 0 ? name : name.slice(dot + 1);
}

/**
 * `Contoso.Web.Localizer.GetString(string, *)` as the fields that hold it.
 *
 * The class is everything before the last dot, so a line with no dot at all names no class — which
 * is the documented escape hatch for matching any class that declares the member, and reads as one:
 * `GetString(string)` is a method, said without saying whose.
 *
 * `withParameters` is false for a shape that names something with no parameter list; the whole line
 * is then a name, and a stray parenthesis in it is part of what did not resolve rather than the
 * start of a signature.
 */
function parseCall(line: string, withParameters = true): Call {
    const trimmed = line.trim();
    const open = withParameters ? trimmed.indexOf('(') : -1;
    const name = (open < 0 ? trimmed : trimmed.slice(0, open)).trim();
    const inside = open < 0 ? undefined : trimmed.slice(open + 1).replace(/\)\s*$/, '');
    const dot = name.lastIndexOf('.');

    return {
        type: dot < 0 ? '' : name.slice(0, dot).trim(),
        member: (dot < 0 ? name : name.slice(dot + 1)).trim(),
        parameters:
            inside === undefined
                ? undefined
                : inside
                    .split(',')
                    .map((part) => part.trim())
                    .filter((part) => part.length > 0),
    };
}

function formatCall(type: string, member: string, parameters?: readonly string[]): string {
    const name = type === '' ? member : `${type}.${member}`;
    return parameters === undefined ? name : `${name}(${parameters.join(', ')})`;
}

/**
 * A schema description, with what it writes in backticks set as code.
 *
 * The descriptions are markdown — the same strings feed VS Code's own settings UI, which renders
 * them — so a page that set them as plain text showed the backticks around every file extension
 * and every identifier in them.
 */
function prose(description: string): DocumentFragment {
    const out = document.createDocumentFragment();

    // Split on the backticks themselves: every odd piece is what was between a pair. An unpaired
    // trailing backtick leaves its piece odd and unclosed, and setting it as code is the more
    // useful reading of what the author meant.
    const pieces = description.split('`');
    for (let i = 0; i < pieces.length; i++) {
        if (pieces[i] === '') {
            continue;
        }
        if (i % 2 === 0) {
            out.append(pieces[i]);
            continue;
        }
        const code = document.createElement('code');
        code.textContent = pieces[i];
        out.append(code);
    }

    return out;
}

/** The typed fragment picked out of a suggestion, so it is clear why it is being offered. */
function highlight(name: string, typed: string): DocumentFragment {
    const out = document.createDocumentFragment();
    const at = typed === '' ? -1 : name.toLowerCase().indexOf(typed.toLowerCase());

    if (at < 0) {
        out.append(name);
        return out;
    }

    const hit = document.createElement('span');
    hit.className = 'match';
    hit.textContent = name.slice(at, at + typed.length);
    out.append(name.slice(0, at), hit, name.slice(at + typed.length));
    return out;
}

function contains(name: string, typed: string): boolean {
    return typed === '' || name.toLowerCase().includes(typed.toLowerCase());
}

function text(value: unknown): string {
    return value === undefined || value === null ? '' : String(value);
}

function count(n: number, noun: string): string {
    return `${n} ${noun}${n === 1 ? '' : 's'}`;
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

/**
 * What the scope being edited says itself, or undefined when it says nothing.
 *
 * An unsaved edit answers ahead of the file, so the form shows what was typed rather than
 * snapping back to disk on the next render — and `null`, the panel's "unset", reads as the scope
 * saying nothing, which is what saving it would make true.
 */
function ownValue(path: string[]): unknown {
    const edit = state ? pending.get(pendingKey(state.scope, path)) : undefined;
    if (edit) {
        return edit.value === null ? undefined : edit.value;
    }

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
    pending.set(pendingKey(state.scope, path), { scope: state.scope, path, value });
    vscode.postMessage({ type: 'edit', scope: state.scope, path, value });
    renderPending();
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

/**
 * A select with the panel's own chevron on it.
 *
 * The platform draws its own arrow on a `<select>`, and it is not the one VS Code draws on the
 * dropdowns beside it — so a page holding both had two shapes meaning the same thing. The native
 * one is turned off and the chevron is drawn here, which is also the one the multi-select uses.
 */
function dropdown(select: HTMLSelectElement): HTMLElement {
    const box = document.createElement('div');
    box.className = 'dropdown';
    box.append(select);
    return box;
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

document.getElementById('save')!.addEventListener('click', save);

document.getElementById('discard')!.addEventListener('click', () => {
    pending.clear();
    vscode.postMessage({ type: 'discard' });
});

// The shortcut every other editor in the window saves with. A webview does not get VS Code's own
// keybindings, so a page that writes files has to bind it.
window.addEventListener('keydown', (event) => {
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 's') {
        event.preventDefault();
        save();
    }
});

// The page holds nothing until the host sends it something, and the host does not know the script
// has run until it says so. VS Code reloads a webview whenever it likes, which without this left
// an empty form behind.
vscode.postMessage({ type: 'ready' });
