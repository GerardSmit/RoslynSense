import * as vscode from 'vscode';
import {
    CloseAction,
    ErrorAction,
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    State,
    TransportKind,
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;
let statusItem: vscode.LanguageStatusItem | undefined;

// VSCode percent-encodes the drive-letter colon ("c%3A"); servers comparing raw paths then
// mismatch. Serialize file URIs unencoded and with an uppercase drive letter so the server
// always sees "C:" style paths (mirrors ms-dotnettools.csharp's UriConverter).
function code2Protocol(uri: vscode.Uri): string {
    if (uri.scheme !== 'file') {
        return uri.toString();
    }
    let serialized = uri.toString(/* skipEncoding */ true);
    const driveLetter = /^file:\/\/\/([a-z])(:|%3a)/i.exec(serialized);
    if (driveLetter) {
        serialized =
            `file:///${driveLetter[1].toUpperCase()}:` +
            serialized.substring(driveLetter[0].length);
    }
    return serialized;
}

function protocol2Code(value: string): vscode.Uri {
    return vscode.Uri.parse(value);
}

async function pickSolution(): Promise<string | undefined> {
    const config = vscode.workspace.getConfiguration('roslynSense');
    const configured = config.get<string>('solutionPath', '');
    if (configured) {
        return configured;
    }

    const solutions = await vscode.workspace.findFiles(
        '**/*.{sln,slnx}', '**/{node_modules,bin,obj,artifacts}/**', 25);
    if (solutions.length === 0) {
        return undefined; // server resolves the nearest solution from cwd
    }
    if (solutions.length === 1) {
        return solutions[0].fsPath;
    }

    const items = solutions
        .map((uri) => ({
            label: vscode.workspace.asRelativePath(uri),
            fsPath: uri.fsPath,
        }))
        .sort((a, b) => a.label.localeCompare(b.label));
    const picked = await vscode.window.showQuickPick(items, {
        placeHolder: 'Multiple solutions found — pick one for RoslynSense',
    });
    if (!picked) {
        return undefined;
    }

    const remember = await vscode.window.showQuickPick(
        [
            { label: 'Yes', description: 'Save to workspace settings (roslynSense.solutionPath)', save: true },
            { label: 'No', description: 'Ask again next time', save: false },
        ],
        { placeHolder: `Use ${picked.label} as the default solution for this workspace?` }
    );
    if (remember?.save) {
        try {
            await config.update('solutionPath', picked.fsPath, vscode.ConfigurationTarget.Workspace);
        } catch {
            // No workspace open to persist into — still use the pick for this session.
        }
    }
    return picked.fsPath;
}

async function startClient(context: vscode.ExtensionContext): Promise<void> {
    const config = vscode.workspace.getConfiguration('roslynSense');
    const serverPath = config.get<string>('serverPath', 'roslyn-sense');
    const solutionPath = await pickSolution();

    const args = ['--lsp'];
    if (solutionPath) {
        args.push('--solution', solutionPath);
    }

    const cwd = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;

    const serverOptions: ServerOptions = {
        command: serverPath,
        args,
        transport: TransportKind.stdio,
        options: { cwd },
    };

    // More generous than the default error handler (which gives up after 5 restarts in a
    // short window with "Cannot call write after a stream was destroyed"): keep restarting
    // as long as crashes are not rapid-fire; only a crash loop stops the client, with the
    // status item offering a manual restart.
    const restartTimes: number[] = [];
    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'csharp' }],
        uriConverters: { code2Protocol, protocol2Code },
        // No client-side file watcher: the server checks file freshness itself on every
        // request (mtime-based), so didChangeWatchedFiles traffic would be redundant.
        errorHandler: {
            error: () => ({ action: ErrorAction.Continue }),
            closed: () => {
                const now = Date.now();
                restartTimes.push(now);
                while (restartTimes.length > 0 && now - restartTimes[0] > 3 * 60_000) {
                    restartTimes.shift();
                }
                if (restartTimes.length > 8) {
                    if (statusItem) {
                        statusItem.severity = vscode.LanguageStatusSeverity.Error;
                        statusItem.text = 'RoslynSense: crashed repeatedly';
                        statusItem.command = {
                            title: 'Restart Server',
                            command: 'roslynSense.restartServer',
                        };
                    }
                    return { action: CloseAction.DoNotRestart };
                }
                return { action: CloseAction.Restart };
            },
        },
    };

    client = new LanguageClient('roslynSense', 'RoslynSense', serverOptions, clientOptions);
    client.onDidChangeState((e) => {
        if (!statusItem) {
            return;
        }
        if (e.newState === State.Starting) {
            statusItem.busy = true;
            statusItem.text = 'RoslynSense: reconnecting';
        } else if (e.newState === State.Running) {
            statusItem.busy = false;
            statusItem.severity = vscode.LanguageStatusSeverity.Information;
            statusItem.text = solutionPath
                ? `RoslynSense: ${vscode.workspace.asRelativePath(solutionPath)}`
                : 'RoslynSense: running';
        }
    });

    statusItem ??= vscode.languages.createLanguageStatusItem(
        'roslynSense.status', { language: 'csharp' });
    statusItem.name = 'RoslynSense';
    statusItem.busy = true;
    statusItem.text = 'RoslynSense: starting';
    statusItem.command = { title: 'Pick Solution', command: 'roslynSense.openSolution' };

    try {
        await client.start();
        statusItem.busy = false;
        statusItem.severity = vscode.LanguageStatusSeverity.Information;
        statusItem.text = solutionPath
            ? `RoslynSense: ${vscode.workspace.asRelativePath(solutionPath)}`
            : 'RoslynSense: running';
        refreshInheritanceMarkers?.();
    } catch (err) {
        // A client that failed to start cannot be stop()ed later — drop it here so
        // restart/pick-solution paths start clean instead of rejecting forever.
        void client.dispose().then(undefined, () => undefined);
        client = undefined;
        statusItem.busy = false;
        statusItem.severity = vscode.LanguageStatusSeverity.Error;
        statusItem.text = 'RoslynSense: failed to start';
        void vscode.window.showErrorMessage(
            `RoslynSense failed to start: ${err}. Install with: dotnet tool install -g RoslynSense, ` +
            `or set roslynSense.serverPath.`
        );
    }
}

async function stopClient(): Promise<void> {
    if (!client) {
        return;
    }
    const current = client;
    client = undefined; // clear first: a failed stop must not wedge future restarts
    try {
        if (current.needsStop()) {
            await current.stop();
        } else {
            await current.dispose();
        }
    } catch {
        // Already stopped or never started — nothing to do.
    }
}

// After the user types "///" on an empty line, ask the server for an XML doc skeleton
// (custom roslynSense/onAutoInsert) and place the caret inside <summary>.
function registerOnAutoInsert(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument(async (e) => {
            if (
                !client ||
                e.document.languageId !== 'csharp' ||
                e.contentChanges.length !== 1 ||
                !e.contentChanges[0].text.endsWith('/')
            ) {
                return;
            }
            const editor = vscode.window.activeTextEditor;
            if (!editor || editor.document !== e.document) {
                return;
            }
            const change = e.contentChanges[0];
            const position = change.range.start.translate(0, change.text.length);
            const linePrefix = e.document.lineAt(position.line).text.substring(0, position.character);
            if (linePrefix.trimStart() !== '///') {
                return;
            }

            interface AutoInsertResult {
                edit: { range: unknown; newText: string };
                cursor: { line: number; character: number };
            }
            const result = await client.sendRequest<AutoInsertResult | null>(
                'roslynSense/onAutoInsert',
                {
                    textDocument: { uri: code2Protocol(e.document.uri) },
                    position: { line: position.line, character: position.character },
                }
            );
            if (!result) {
                return;
            }
            // The request round-tripped the server; bail if the buffer moved on meanwhile,
            // otherwise the skeleton lands at a stale offset.
            if (
                editor.document.version !== e.document.version ||
                editor.document.lineAt(position.line).text.substring(0, position.character) !==
                    linePrefix
            ) {
                return;
            }

            const applied = await editor.edit(
                (builder) => builder.insert(position, result.edit.newText),
                { undoStopBefore: true, undoStopAfter: true }
            );
            if (applied) {
                const cursor = new vscode.Position(result.cursor.line, result.cursor.character);
                editor.selection = new vscode.Selection(cursor, cursor);
            }
        })
    );
}

async function showInheritanceForLine(line: number | undefined): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    if (!client || !editor || editor.document.languageId !== 'csharp' || line === undefined) {
        return;
    }
    let markers: InheritanceMarker[];
    try {
        markers = await client.sendRequest<InheritanceMarker[]>(
            'roslynSense/inheritanceMarkers',
            { textDocument: { uri: code2Protocol(editor.document.uri) } }
        );
    } catch {
        return;
    }
    const atLine = markers.filter((m) => m.line === line);
    const items = atLine.flatMap((marker) =>
        marker.targets.map((t, index) => ({
            label: `${UP_KINDS.has(marker.kind) ? '$(arrow-up)' : '$(arrow-down)'} ${t.title}`,
            marker,
            target: t,
            index,
        }))
    );
    if (items.length === 0) {
        void vscode.window.showInformationMessage(
            'RoslynSense: no inheritance relations on this line.'
        );
        return;
    }
    const picked =
        items.length === 1
            ? items[0]
            : await vscode.window.showQuickPick(items, { placeHolder: 'Inheritance relations' });
    if (!picked) {
        return;
    }
    if (picked.target.uri) {
        await vscode.commands.executeCommand(
            'roslynSense.openLocation',
            picked.target.uri,
            picked.target.line,
            picked.target.character
        );
    } else {
        await vscode.commands.executeCommand(
            'roslynSense.openInheritanceTarget',
            code2Protocol(editor.document.uri),
            picked.marker.line,
            picked.marker.character,
            picked.marker.kind,
            picked.index
        );
    }
}

function registerLensCommands(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        // CodeLens "▶ Run test": run the test in a terminal via dotnet test --filter.
        vscode.commands.registerCommand(
            'roslynSense.runTest',
            (fullyQualifiedName: string, projectPath: string) => {
                const terminal = vscode.window.createTerminal('RoslynSense Test');
                terminal.show();
                const project = projectPath ? ` "${projectPath}"` : '';
                terminal.sendText(
                    `dotnet test${project} --filter "FullyQualifiedName~${fullyQualifiedName}"`
                );
            }
        ),
        // CodeLens "N references": open VSCode's references peek with server-provided locations.
        vscode.commands.registerCommand(
            'roslynSense.showReferences',
            (uri: string, line: number, character: number, locations: LspLocation[]) => {
                void vscode.commands.executeCommand(
                    'editor.action.showReferences',
                    protocol2Code(uri),
                    new vscode.Position(line, character),
                    (locations ?? []).map(
                        (l) =>
                            new vscode.Location(
                                protocol2Code(l.uri),
                                new vscode.Range(
                                    l.range.start.line,
                                    l.range.start.character,
                                    l.range.end.line,
                                    l.range.end.character
                                )
                            )
                    )
                );
            }
        ),
        // CodeLens "overrides X" / "implements I.M": jump to the base member.
        vscode.commands.registerCommand(
            'roslynSense.openLocation',
            async (uri: string, line: number, character: number) => {
                const doc = await vscode.workspace.openTextDocument(protocol2Code(uri));
                const editor = await vscode.window.showTextDocument(doc);
                const position = new vscode.Position(line, character);
                editor.selection = new vscode.Selection(position, position);
                editor.revealRange(
                    new vscode.Range(position, position),
                    vscode.TextEditorRevealType.InCenterIfOutsideViewport
                );
            }
        ),
        // Inheritance list for a line: invoked by the inheritance CodeLens, the editor
        // context menu, and Ctrl+Alt+U (gutter arrows themselves aren't clickable —
        // no VSCode API for gutter clicks).
        vscode.commands.registerCommand('roslynSense.showInheritance', () =>
            showInheritanceForLine(vscode.window.activeTextEditor?.selection.active.line)
        ),
        vscode.commands.registerCommand(
            'roslynSense.showInheritanceAt',
            (_uri: string, line: number) => showInheritanceForLine(line)
        ),
        // Gutter marker link for a metadata target: server decompiles and returns a location.
        vscode.commands.registerCommand(
            'roslynSense.openInheritanceTarget',
            async (uri: string, line: number, character: number, kind: string, index: number) => {
                if (!client) {
                    return;
                }
                let location: LspLocation | null;
                try {
                    location = await client.sendRequest<LspLocation | null>(
                        'roslynSense/resolveInheritanceTarget',
                        { textDocument: { uri }, line, character, kind, index }
                    );
                } catch {
                    return;
                }
                if (!location) {
                    void vscode.window.showInformationMessage(
                        'RoslynSense: could not resolve the target (decompilation failed).'
                    );
                    return;
                }
                await vscode.commands.executeCommand(
                    'roslynSense.openLocation',
                    location.uri,
                    location.range.start.line,
                    location.range.start.character
                );
            }
        ),
        // Palette ids differ from the server command ids on purpose: vscode-languageclient
        // auto-registers every command in the server's executeCommandProvider capability, so
        // reusing those ids here would collide and fail extension activation.
        vscode.commands.registerCommand('roslynSense.restorePackages', async () => {
            if (!client) {
                return;
            }
            const config = vscode.workspace.getConfiguration('roslynSense');
            const result = await client.sendRequest<string>('workspace/executeCommand', {
                command: 'roslynSense.restore',
                arguments: [config.get<string>('solutionPath', '')].filter((a) => a),
            });
            void vscode.window.showInformationMessage(`RoslynSense: ${result}`);
        }),
        vscode.commands.registerCommand('roslynSense.reloadRoslynWorkspace', async () => {
            if (!client) {
                return;
            }
            const result = await client.sendRequest<string>('workspace/executeCommand', {
                command: 'roslynSense.reloadWorkspace',
                arguments: [],
            });
            void vscode.window.showInformationMessage(`RoslynSense: ${result}`);
        })
    );
}

interface LspLocation {
    uri: string;
    range: {
        start: { line: number; character: number };
        end: { line: number; character: number };
    };
}

interface RunningProcess {
    sessionId: string;
    pid: number;
    projectName: string;
    projectPath: string;
    url: string | null;
    startedAtUtc: string;
}

interface InheritanceTarget {
    title: string;
    uri: string | null; // null for metadata symbols — resolved (decompiled) on click
    line: number;
    character: number;
}

interface InheritanceMarker {
    line: number;
    character: number;
    kind: string; // base | implements | overrides | derived | implemented | overridden
    targets: InheritanceTarget[];
}

let upDecoration: vscode.TextEditorDecorationType | undefined;
let downDecoration: vscode.TextEditorDecorationType | undefined;
let markerRefreshTimer: NodeJS.Timeout | undefined;
let refreshInheritanceMarkers: (() => void) | undefined;

const UP_KINDS = new Set(['base', 'implements', 'overrides']);

// Rider/VS-style inheritance gutter arrows: up = inherits from something, down = something
// inherits from it. Hover the icon for clickable navigation links (custom
// roslynSense/inheritanceMarkers request; gutter icons themselves are not clickable in the
// VSCode API).
function registerInheritanceMarkers(context: vscode.ExtensionContext): void {
    upDecoration = vscode.window.createTextEditorDecorationType({
        gutterIconPath: vscode.Uri.joinPath(context.extensionUri, 'media', 'inherit-up.svg'),
        gutterIconSize: 'contain',
    });
    downDecoration = vscode.window.createTextEditorDecorationType({
        gutterIconPath: vscode.Uri.joinPath(context.extensionUri, 'media', 'inherit-down.svg'),
        gutterIconSize: 'contain',
    });
    context.subscriptions.push(upDecoration, downDecoration);

    const refresh = async (editor: vscode.TextEditor | undefined): Promise<void> => {
        if (!editor || !client || editor.document.languageId !== 'csharp') {
            return;
        }
        let markers: InheritanceMarker[];
        try {
            markers = await client.sendRequest<InheritanceMarker[]>(
                'roslynSense/inheritanceMarkers',
                { textDocument: { uri: code2Protocol(editor.document.uri) } }
            );
        } catch {
            return;
        }

        const docUri = code2Protocol(editor.document.uri);
        const toOptions = (marker: InheritanceMarker): vscode.DecorationOptions => {
            const hover = new vscode.MarkdownString(
                marker.targets
                    .map((t, index) => {
                        // Metadata targets have no location yet — the resolve command
                        // decompiles the containing type server-side, then navigates.
                        const args = t.uri
                            ? encodeURIComponent(JSON.stringify([t.uri, t.line, t.character]))
                            : encodeURIComponent(
                                  JSON.stringify([docUri, marker.line, marker.character, marker.kind, index])
                              );
                        const command = t.uri
                            ? 'roslynSense.openLocation'
                            : 'roslynSense.openInheritanceTarget';
                        return `[$(arrow-right) ${t.title}](command:${command}?${args})`;
                    })
                    .join('\n\n'),
                true
            );
            hover.isTrusted = true;
            const line = Math.min(marker.line, editor.document.lineCount - 1);
            return { range: editor.document.lineAt(line).range, hoverMessage: hover };
        };

        editor.setDecorations(
            upDecoration!,
            markers.filter((m) => UP_KINDS.has(m.kind)).map(toOptions)
        );
        editor.setDecorations(
            downDecoration!,
            markers.filter((m) => !UP_KINDS.has(m.kind)).map(toOptions)
        );
    };

    const scheduleRefresh = (editor: vscode.TextEditor | undefined): void => {
        if (markerRefreshTimer) {
            clearTimeout(markerRefreshTimer);
        }
        markerRefreshTimer = setTimeout(() => void refresh(editor), 700);
    };

    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor((editor) => scheduleRefresh(editor)),
        vscode.workspace.onDidChangeTextDocument((e) => {
            const editor = vscode.window.activeTextEditor;
            if (editor && editor.document === e.document) {
                scheduleRefresh(editor);
            }
        }),
        { dispose: () => markerRefreshTimer && clearTimeout(markerRefreshTimer) }
    );
    refreshInheritanceMarkers = () => scheduleRefresh(vscode.window.activeTextEditor);
}

let processStatusItem: vscode.StatusBarItem | undefined;
let processPollTimer: NodeJS.Timeout | undefined;

// Status bar counter for applications launched via the shared daemon's MCP chats
// (run_project). Click → list with kill / open-URL actions. Polls the server because
// launches happen in other processes (MCP chat clients), not this editor.
function registerProcessStatusBar(context: vscode.ExtensionContext): void {
    processStatusItem = vscode.window.createStatusBarItem(
        'roslynSense.processes', vscode.StatusBarAlignment.Left, 90);
    processStatusItem.name = 'RoslynSense Processes';
    processStatusItem.command = 'roslynSense.showProcesses';
    context.subscriptions.push(processStatusItem);

    const poll = async (): Promise<void> => {
        if (!client) {
            processStatusItem?.hide();
            return;
        }
        try {
            const processes = await client.sendRequest<RunningProcess[]>(
                'roslynSense/runningProcesses');
            if (processes.length === 0) {
                processStatusItem?.hide();
            } else if (processStatusItem) {
                processStatusItem.text = `$(rocket) ${processes.length}`;
                processStatusItem.tooltip =
                    'RoslynSense: running processes\n' +
                    processes.map((p) => `${p.projectName} (pid ${p.pid})`).join('\n');
                processStatusItem.show();
            }
        } catch {
            processStatusItem?.hide();
        }
    };
    processPollTimer = setInterval(() => void poll(), 5000);
    context.subscriptions.push({ dispose: () => clearInterval(processPollTimer) });
    void poll();

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.showProcesses', async () => {
            if (!client) {
                return;
            }
            let processes: RunningProcess[];
            try {
                processes = await client.sendRequest<RunningProcess[]>(
                    'roslynSense/runningProcesses');
            } catch {
                return; // client stopping — nothing to show
            }
            if (processes.length === 0) {
                void vscode.window.showInformationMessage('RoslynSense: no running processes.');
                return;
            }

            const picked = await vscode.window.showQuickPick(
                processes.map((p) => ({
                    label: `$(rocket) ${p.projectName}`,
                    description: `pid ${p.pid}${p.url ? ` — ${p.url}` : ''}`,
                    detail: `${p.projectPath} — started ${new Date(p.startedAtUtc).toLocaleTimeString()}`,
                    process: p,
                })),
                { placeHolder: 'Running processes (launched by AI chats via RoslynSense)' }
            );
            if (!picked) {
                return;
            }

            const actions: { label: string; action: 'kill' | 'open' }[] = [
                { label: '$(trash) Kill process', action: 'kill' },
            ];
            if (picked.process.url) {
                actions.push({ label: '$(globe) Open URL', action: 'open' });
            }
            const action = await vscode.window.showQuickPick(actions, {
                placeHolder: `${picked.process.projectName} (pid ${picked.process.pid})`,
            });
            if (action?.action === 'kill') {
                const result = await client.sendRequest<string>('roslynSense/killProcess', {
                    pid: picked.process.pid,
                });
                void vscode.window.showInformationMessage(`RoslynSense: ${result}`);
            } else if (action?.action === 'open' && picked.process.url) {
                void vscode.env.openExternal(vscode.Uri.parse(picked.process.url));
            }
        })
    );
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.openSolution', async () => {
            const config = vscode.workspace.getConfiguration('roslynSense');
            await config.update('solutionPath', undefined, vscode.ConfigurationTarget.Workspace);
            await stopClient();
            await startClient(context);
        }),
        vscode.commands.registerCommand('roslynSense.restartServer', async () => {
            await stopClient();
            await startClient(context);
        })
    );
    registerLensCommands(context);
    registerOnAutoInsert(context);
    registerProcessStatusBar(context);
    registerInheritanceMarkers(context);

    await startClient(context);
}

export async function deactivate(): Promise<void> {
    statusItem?.dispose();
    statusItem = undefined;
    await stopClient();
}
