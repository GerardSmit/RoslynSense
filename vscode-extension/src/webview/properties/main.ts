/// <reference path="../../properties/protocol.d.ts" />

/**
 * The Properties panel's browser half: it draws a form from what the server said about one file
 * or folder, and turns each changed control into one message.
 *
 * Built with `createElement` throughout. A path, a namespace and a file name all come from disk,
 * and none of them is ever assigned as HTML.
 *
 * Edits apply as they are made, which is how VS Code's own Settings editor behaves — there is no
 * Save button to forget, and the answer that comes back is the project file re-read rather than
 * the control's own value, so a write that did more than asked shows what it did.
 */

declare function acquireVsCodeApi(): { postMessage(message: PropsMsg.ToHost): void };

const vscode = acquireVsCodeApi();

const form = document.getElementById('form') as HTMLElement;
const title = document.getElementById('title') as HTMLElement;
const subtitle = document.getElementById('subtitle') as HTMLElement;
const notice = document.getElementById('notice') as HTMLElement;

window.addEventListener('message', (event: MessageEvent<PropsMsg.ToView>) => {
    const message = event.data;

    if (message.type === 'failed') {
        title.textContent = 'Properties';
        subtitle.textContent = '';
        notice.textContent = message.message;
        form.replaceChildren();
        return;
    }

    render(message);
});

vscode.postMessage({ type: 'ready' });

/** True while a write is in flight, which is the whole of the panel's own state. */
let busy = false;

function render(state: PropsMsg.State): void {
    busy = false;
    const properties = state.properties;

    title.textContent = basename(properties.path);
    subtitle.replaceChildren();
    subtitle.appendChild(document.createTextNode(properties.path));

    if (properties.projectName && properties.projectPath) {
        subtitle.appendChild(document.createTextNode(' — in '));
        subtitle.appendChild(
            linkButton(properties.projectName, () =>
                vscode.postMessage({ type: 'reveal', target: 'project' })
            )
        );
    }

    notice.textContent = state.notice ?? '';

    form.replaceChildren();

    if (properties.reason) {
        form.appendChild(paragraph(properties.reason));
        return;
    }

    if (properties.folder) {
        renderFolder(properties.folder);
        return;
    }

    if (properties.file) {
        renderFile(properties.file);
    }
}

function renderFolder(folder: PropsMsg.Folder): void {
    form.appendChild(
        checkboxRow(
            "Include this folder's name in namespaces",
            'Off is ReSharper and Rider’s "do not create a namespace" for a folder, ' +
                'written to the project’s .DotSettings file. It changes what new files here ' +
                'are given and what the code style expects of the ones already here.',
            folder.namespaceProvider,
            (value) => send({ type: 'apply', namespaceProvider: value })
        )
    );

    if (folder.namespace) {
        form.appendChild(readonlyRow('Namespace for new files', folder.namespace));
    }
}

/** The empty option's label, which is a real answer rather than a blank line. */
const NOT_SET = 'Not set';

const COPY_CHOICES: readonly (readonly [string, string])[] = [
    ['', NOT_SET],
    ['Never', 'Do not copy'],
    ['Always', 'Copy always'],
    ['PreserveNewest', 'Copy if newer'],
];

function renderFile(file: PropsMsg.File): void {
    if (!file.inProject) {
        form.appendChild(
            paragraph(
                'No item in the project claims this file. Choosing a build action adds one.'
            )
        );
    }

    form.appendChild(
        selectRow(
            'Build action',
            'What the build does with the file.',
            file.itemTypes.map((type) => [type, type] as const),
            file.itemType,
            (value) => send({ type: 'apply', itemType: value })
        )
    );

    form.appendChild(
        selectRow(
            'Copy to output directory',
            'Whether the file is copied next to the build output.',
            COPY_CHOICES,
            file.copyToOutputDirectory ?? '',
            (value) => send({ type: 'apply', copyToOutputDirectory: value })
        )
    );

    form.appendChild(
        textRow(
            'Custom tool',
            'The generator that runs over the file — ResXFileCodeGenerator for a .resx, ' +
                'MSBuild:Compile for XAML. Empty means none.',
            file.generator ?? '',
            (value) => send({ type: 'apply', generator: value })
        )
    );

    form.appendChild(
        textRow(
            'Custom tool namespace',
            'The namespace the generator puts its output in. Empty means the file’s own.',
            file.customToolNamespace ?? '',
            (value) => send({ type: 'apply', customToolNamespace: value })
        )
    );

    if (file.dependentUpon) {
        form.appendChild(readonlyRow('Nested under', file.dependentUpon));
    }

    if (file.link) {
        form.appendChild(readonlyRow('Shown in the project as', file.link));
    }

    if (file.inProject) {
        const where = document.createElement('div');
        where.className = 'row';
        where.appendChild(label('Comes from'));

        const value = document.createElement('div');
        value.className = 'value';
        value.appendChild(
            document.createTextNode(
                file.fromGlob
                    ? 'A wildcard, not a line naming this file. Changing the build action writes ' +
                          'both a Remove and an entry of the new type.'
                    : 'An entry in the project file naming this file.'
            )
        );

        if (file.declaredIn) {
            value.appendChild(document.createTextNode(' '));
            value.appendChild(
                linkButton(basename(file.declaredIn), () =>
                    vscode.postMessage({ type: 'reveal', target: 'declaredIn' })
                )
            );
        }

        where.appendChild(value);
        form.appendChild(where);
    }
}

/**
 * Sends one edit, and refuses the second one until the first has come back.
 *
 * Two writes to the same project file in flight at once would each have parsed it before the
 * other wrote, so the later one would silently drop the earlier. Disabling the form is the honest
 * version of that, and the round trip is a file write rather than a network call.
 */
function send(message: PropsMsg.Apply): void {
    if (busy) {
        return;
    }

    busy = true;
    form.querySelectorAll('select, input, button').forEach((control) => {
        (control as HTMLInputElement).disabled = true;
    });
    notice.textContent = 'Writing…';
    vscode.postMessage(message);
}

function row(name: string, description: string | undefined): HTMLElement {
    const element = document.createElement('div');
    element.className = 'row';
    element.appendChild(label(name));

    if (description) {
        const note = document.createElement('p');
        note.className = 'description';
        note.textContent = description;
        element.appendChild(note);
    }

    return element;
}

function label(text: string): HTMLElement {
    const element = document.createElement('h2');
    element.className = 'label';
    element.textContent = text;
    return element;
}

function selectRow(
    name: string,
    description: string,
    choices: readonly (readonly [string, string])[],
    selected: string,
    onChange: (value: string) => void
): HTMLElement {
    const element = row(name, description);
    const select = document.createElement('select');

    for (const [value, text] of choices) {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = text;
        option.selected = value === selected;
        select.appendChild(option);
    }

    select.addEventListener('change', () => onChange(select.value));
    element.insertBefore(select, element.querySelector('.description'));
    return element;
}

function textRow(
    name: string,
    description: string,
    value: string,
    onCommit: (value: string) => void
): HTMLElement {
    const element = row(name, description);
    const input = document.createElement('input');
    input.type = 'text';
    input.value = value;

    // On commit rather than on every keystroke: each one is a project-file write, and a write per
    // character would fight the person typing.
    const commit = () => {
        if (input.value !== value) {
            onCommit(input.value);
        }
    };

    input.addEventListener('change', commit);
    input.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
            commit();
        }
    });

    element.insertBefore(input, element.querySelector('.description'));
    return element;
}

function checkboxRow(
    name: string,
    description: string,
    checked: boolean,
    onChange: (value: boolean) => void
): HTMLElement {
    // The label is the checkbox's own, as in the Settings editor — a heading above a checkbox
    // that repeats the same words is the same sentence twice.
    const element = document.createElement('div');
    element.className = 'row';

    const wrapper = document.createElement('label');
    wrapper.className = 'checkbox';

    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = checked;
    input.addEventListener('change', () => onChange(input.checked));

    wrapper.appendChild(input);
    wrapper.appendChild(document.createTextNode(name));
    element.appendChild(wrapper);

    const note = document.createElement('p');
    note.className = 'description';
    note.textContent = description;
    element.appendChild(note);
    return element;
}

function readonlyRow(name: string, value: string): HTMLElement {
    const element = row(name, undefined);
    const text = document.createElement('div');
    text.className = 'value';
    text.textContent = value;
    element.appendChild(text);
    return element;
}

function paragraph(text: string): HTMLElement {
    const element = document.createElement('p');
    element.className = 'note';
    element.textContent = text;
    return element;
}

function linkButton(text: string, onClick: () => void): HTMLElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'linklike';
    button.textContent = text;
    button.addEventListener('click', onClick);
    return button;
}

function basename(path: string): string {
    const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
    return cut < 0 ? path : path.slice(cut + 1);
}
