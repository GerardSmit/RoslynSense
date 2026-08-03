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
import { DEBUG_TYPE, registerDebugLaunch } from './debugLaunch';
import { registerTestController, runTestById } from './testController';
import { registerSolutionExplorer } from './solutionExplorer';
import { registerVirtualDocuments } from './virtualDocuments';
import { registerNuGetPanel } from './nugetPanel';
import { registerTaskProvider } from './taskProvider';
import { registerEditorContext } from './editorContext';
import { registerHotReload } from './hotReload';

let client: LanguageClient | undefined;
let statusItem: vscode.LanguageStatusItem | undefined;
let activeSolutionPath: string | undefined;

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

/**
 * One client per bound solution, keyed by solution path (or by workspace folder when the server
 * resolves the solution itself). A multi-root workspace with two solutions gets two daemons —
 * which is what already happens on the server side, since the daemon is per solution.
 */
const clientsBySolution = new Map<string, LanguageClient>();

/** Which solution each workspace folder is bound to, resolved once per folder. */
const solutionByFolder = new Map<string, string | undefined>();

/** Finds the solution a file belongs to: the setting for its folder, else the nearest one. */
async function solutionForFolder(folder: vscode.WorkspaceFolder): Promise<string | undefined> {
    const key = folder.uri.fsPath;
    if (solutionByFolder.has(key)) {
        return solutionByFolder.get(key);
    }

    // Folder-scoped so each root of a multi-root workspace can name its own solution.
    const configured = vscode.workspace
        .getConfiguration('roslynSense', folder.uri)
        .get<string>('solutionPath', '');

    let resolved: string | undefined = configured || undefined;
    if (!resolved) {
        const found = await vscode.workspace.findFiles(
            new vscode.RelativePattern(folder, '**/*.{sln,slnx}'),
            '**/{node_modules,bin,obj,artifacts}/**',
            2
        );
        // Exactly one is unambiguous; more than one is the pickSolution case, which only runs
        // for the folder the user is actually working in.
        resolved = found.length === 1 ? found[0].fsPath : undefined;
    }

    solutionByFolder.set(key, resolved);
    return resolved;
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

/**
 * The `roslynSense.*` settings the server reads.
 *
 * Listed explicitly rather than passing the whole configuration object: `getConfiguration`
 * returns every contributed key plus VS Code's own bookkeeping, and the server should be told
 * what it is meant to act on rather than handed everything and left to guess.
 */
function serverSettings(): Record<string, unknown> {
    const config = vscode.workspace.getConfiguration('roslynSense');
    return {
        analyzerDiagnostics: config.get('analyzerDiagnostics'),
        codeStyleDiagnostics: config.get('codeStyleDiagnostics'),
        analyzerTimeoutSeconds: config.get('analyzerTimeoutSeconds'),
        workspaceDiagnostics: config.get('workspaceDiagnostics'),
        sourceLink: config.get('sourceLink'),
        fileNesting: { rules: config.get('fileNesting.rules') },
    };
}

/**
 * Pushes changed settings to every running server.
 *
 * Sent by hand rather than through `synchronize.configurationSection`, which is deprecated in
 * vscode-languageclient 9 and sends the raw configuration tree; this sends the same shape the
 * server already received at initialize.
 */
function registerConfigurationSync(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (!event.affectsConfiguration('roslynSense')) {
                return;
            }
            const settings = serverSettings();
            for (const running of clientsBySolution.values()) {
                void running
                    .sendNotification('workspace/didChangeConfiguration', {
                        settings: { roslynSense: settings },
                    })
                    .catch(() => undefined);
            }
        })
    );
}

async function startClient(
    context: vscode.ExtensionContext,
    binding?: { solutionPath?: string; folder?: vscode.WorkspaceFolder }
): Promise<void> {
    const config = vscode.workspace.getConfiguration('roslynSense');
    const serverPath = config.get<string>('serverPath', 'roslyn-sense');
    const solutionPath = binding ? binding.solutionPath : await pickSolution();
    activeSolutionPath = solutionPath;

    const args = ['--lsp'];
    if (solutionPath) {
        args.push('--solution', solutionPath);
    }

    // The working directory is how the server resolves the solution when none was named, so a
    // second root has to start its client in its own folder.
    const cwd = binding?.folder?.uri.fsPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;

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
    let initFailures = 0;

    function createWorkspaceWatchers(): vscode.FileSystemWatcher[] {
        // VS Code globs cannot express "not under bin/obj", so build output is filtered
        // server-side instead; the events are cheap and the server drops them before it does
        // any work.
        return [
            '**/*.cs',
            '**/*.{csproj,vbproj,fsproj,props,targets,sln,slnx,slnf}',
            '**/{.editorconfig,.globalconfig,Directory.Packages.props}',
        ].map((pattern) => vscode.workspace.createFileSystemWatcher(pattern));
    }
    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'csharp' }],
        uriConverters: { code2Protocol, protocol2Code },
        // Sent at initialize so the very first analyzer pass already runs under the user's
        // settings; changes afterwards go through workspace/didChangeConfiguration.
        initializationOptions: serverSettings(),
        // Content changes to open files arrive via didChange; these watchers cover what the
        // editor never sees — files created, deleted, or rewritten outside it (git checkout,
        // scaffolding, another agent). The server coalesces the burst a branch switch produces.
        synchronize: { fileEvents: createWorkspaceWatchers() },
        // A cold daemon spawn (first window on a solution) can lose the very first
        // connection attempt; a failed initialize must retry, not surface an error toast.
        initializationFailedHandler: () => {
            initFailures += 1;
            return initFailures <= 4; // retry a few times, then give up for real
        },
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
    clientsBySolution.set(bindingKey(solutionPath, binding?.folder), client);
    wireEditorDebugCommandHandler(client);
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
        sendBreakpointSnapshot();
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

function bindingKey(solutionPath: string | undefined, folder: vscode.WorkspaceFolder | undefined): string {
    return solutionPath ?? folder?.uri.fsPath ?? '<default>';
}

async function stopClient(): Promise<void> {
    const running = [...clientsBySolution.values()];
    clientsBySolution.clear();
    solutionByFolder.clear();
    client = undefined; // clear first: a failed stop must not wedge future restarts

    for (const current of running) {
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
}

/**
 * Points `client` at the solution owning the focused editor, starting that solution's client the
 * first time it is needed.
 *
 * A multi-root workspace with two solutions has two daemons — the server side is per solution
 * already — so the only question is which one a command should talk to. The answer is whichever
 * one owns the file the user is looking at.
 */
async function bindActiveEditor(
    context: vscode.ExtensionContext,
    document: vscode.TextDocument | undefined
): Promise<void> {
    if (document?.uri.scheme !== 'file') {
        return;
    }
    const folder = vscode.workspace.getWorkspaceFolder(document.uri);
    if (!folder) {
        return;
    }

    const solutionPath = await solutionForFolder(folder);
    const key = bindingKey(solutionPath, folder);
    const existing = clientsBySolution.get(key);

    if (existing) {
        if (client !== existing) {
            client = existing;
            activeSolutionPath = solutionPath;
            updateStatusText(solutionPath);
        }
        return;
    }

    // Only the first root starts eagerly; the rest start when the user opens something in them,
    // so a workspace with five roots does not spawn five daemons on activation.
    await startClient(context, { solutionPath, folder });
}

function updateStatusText(solutionPath: string | undefined): void {
    if (statusItem) {
        statusItem.text = solutionPath
            ? `RoslynSense: ${vscode.workspace.asRelativePath(solutionPath)}`
            : 'RoslynSense: running';
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

async function runTestFromLens(
    fullyQualifiedName: string,
    projectPath: string,
    mode: 'run' | 'debug'
): Promise<void> {
    const ran = await runTestById(fullyQualifiedName, projectPath, mode);
    if (ran) {
        return;
    }
    // The Test Explorer has not discovered this project yet (nothing expanded it). Falling
    // back to a terminal keeps the lens useful instead of doing nothing.
    const terminal = vscode.window.createTerminal('RoslynSense Test');
    terminal.show();
    const project = projectPath ? ` "${projectPath}"` : '';
    terminal.sendText(`dotnet test${project} --filter "FullyQualifiedName~${fullyQualifiedName}"`);
}

function registerLensCommands(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        // CodeLens "▶ Run test" / "Debug test": route into the Test Explorer so results land
        // in the test UI with pass/fail decorations, rather than as terminal scrollback.
        vscode.commands.registerCommand(
            'roslynSense.runTest',
            (fullyQualifiedName: string, projectPath: string) =>
                runTestFromLens(fullyQualifiedName, projectPath, 'run')
        ),
        vscode.commands.registerCommand(
            'roslynSense.debugTest',
            (fullyQualifiedName: string, projectPath: string) =>
                runTestFromLens(fullyQualifiedName, projectPath, 'debug')
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

// ---- Debug bridge -------------------------------------------------------------------
//
// Two directions:
// 1. LLM-owned debug sessions (netcoredbg/ICorDebug in an MCP chat process) are mirrored
//    here: paused line revealed + highlighted, status bar entry with continue/step/
//    evaluate controls (custom roslynSense/debugSessions + debugCommand).
// 2. The user's own VSCode debug session is tracked via a DebugAdapterTracker and reported
//    to the server (roslynSense/editorDebugState), so MCP chats know the user is paused at
//    a breakpoint — and can drive the session via roslynSense/editorDebugCommand (DAP).

interface DebugBreakpointInfo {
    id: number;
    file: string;
    line: number; // 1-based
    condition: string | null;
}

interface DebugSessionInfo {
    ownerPid: number;
    kind: string;
    target: string;
    state: string; // running | stopped | exited
    reason: string | null;
    function: string | null;
    filePath: string | null;
    line: number; // 1-based
    updatedAtUtc: string;
    breakpoints: DebugBreakpointInfo[];
}

interface DebugCommandResult {
    ok: boolean;
    result: string;
}

let debugStatusItem: vscode.StatusBarItem | undefined;
let debugPollTimer: NodeJS.Timeout | undefined;
let pausedLineDecoration: vscode.TextEditorDecorationType | undefined;
let lastRevealedFrame: string | undefined;
let debugOutput: vscode.OutputChannel | undefined;

function debugChannel(): vscode.OutputChannel {
    debugOutput ??= vscode.window.createOutputChannel('RoslynSense Debug');
    return debugOutput;
}

function applyPausedDecoration(session: DebugSessionInfo | undefined): void {
    if (!pausedLineDecoration) {
        return;
    }
    for (const editor of vscode.window.visibleTextEditors) {
        if (
            session?.state === 'stopped' &&
            session.filePath &&
            editor.document.uri.fsPath.toLowerCase() === session.filePath.toLowerCase()
        ) {
            const line = Math.min(Math.max(session.line - 1, 0), editor.document.lineCount - 1);
            editor.setDecorations(pausedLineDecoration, [editor.document.lineAt(line).range]);
        } else {
            editor.setDecorations(pausedLineDecoration, []);
        }
    }
}

// ---- Native breakpoint sync ----------------------------------------------------------
//
// One shared breakpoint set, Rider-style: chat-placed breakpoints appear as REAL VSCode
// breakpoints (red dots + Breakpoints pane, no debug session needed), and breakpoints the
// user toggles in the gutter are forwarded into the AI session. Suppression sets stop the
// resulting onDidChangeBreakpoints echoes from bouncing back to the server.

const suppressedAdds = new Set<string>();
const suppressedRemovals = new Set<string>();
let serverBpIds = new Map<string, number>(); // key -> server breakpoint id
let currentAiOwnerPid: number | undefined;

function bpKey(file: string, line1Based: number): string {
    return `${file.toLowerCase()}:${line1Based}`;
}

function vscodeBpKey(bp: vscode.Breakpoint): string | undefined {
    if (!(bp instanceof vscode.SourceBreakpoint) || bp.location.uri.scheme !== 'file') {
        return undefined;
    }
    return bpKey(bp.location.uri.fsPath, bp.location.range.start.line + 1);
}

async function sendBpCommand(action: string, extra: Record<string, unknown>): Promise<void> {
    if (!client || currentAiOwnerPid === undefined) {
        return;
    }
    try {
        await client.sendRequest<DebugCommandResult>('roslynSense/debugCommand', {
            ownerPid: currentAiOwnerPid,
            action,
            ...extra,
        });
    } catch {
        // Session raced away — the next poll reconciles.
    }
}

function syncNativeBreakpoints(sessions: DebugSessionInfo[]): void {
    const session = sessions[0];
    currentAiOwnerPid = session?.ownerPid;
    const serverBps = new Map<string, DebugBreakpointInfo>();
    for (const bp of session?.breakpoints ?? []) {
        serverBps.set(bpKey(bp.file, bp.line), bp);
    }

    const editorKeys = new Set<string>();
    for (const bp of vscode.debug.breakpoints) {
        const key = vscodeBpKey(bp);
        if (key) {
            editorKeys.add(key);
        }
    }

    // Server → editor: chat-placed breakpoints become native dots.
    const toAdd: vscode.Breakpoint[] = [];
    for (const [key, bp] of serverBps) {
        if (!editorKeys.has(key)) {
            suppressedAdds.add(key);
            toAdd.push(new vscode.SourceBreakpoint(
                new vscode.Location(
                    vscode.Uri.file(bp.file),
                    new vscode.Position(Math.max(bp.line - 1, 0), 0)),
                true,
                bp.condition ?? undefined));
        }
    }
    if (toAdd.length > 0) {
        vscode.debug.addBreakpoints(toAdd);
    }

    // Server-side removals (the chat removed one) → drop the matching dot.
    const toRemove: vscode.Breakpoint[] = [];
    for (const [key] of serverBpIds) {
        if (!serverBps.has(key)) {
            for (const bp of vscode.debug.breakpoints) {
                if (vscodeBpKey(bp) === key) {
                    suppressedRemovals.add(key);
                    toRemove.push(bp);
                }
            }
        }
    }
    if (toRemove.length > 0) {
        vscode.debug.removeBreakpoints(toRemove);
    }

    serverBpIds = new Map(
        [...serverBps].filter(([, bp]) => bp.id > 0).map(([key, bp]) => [key, bp.id]));
}

// Mirrors the editor's full C# breakpoint set to the server's per-solution store, so
// breakpoint edits made with NO AI session running still shape the next session the chat
// starts (DebugStartTool folds the store into its initial breakpoints).
function sendBreakpointSnapshot(): void {
    if (!client) {
        return;
    }
    const solutionPath =
        activeSolutionPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!solutionPath) {
        return;
    }
    const breakpoints = vscode.debug.breakpoints
        .filter((bp): bp is vscode.SourceBreakpoint =>
            bp instanceof vscode.SourceBreakpoint &&
            bp.location.uri.scheme === 'file' &&
            bp.location.uri.fsPath.toLowerCase().endsWith('.cs'))
        .map((bp) => ({
            file: bp.location.uri.fsPath,
            line: bp.location.range.start.line + 1,
            condition: bp.condition,
        }));
    void client
        .sendNotification('roslynSense/syncBreakpoints', { solutionPath, breakpoints })
        .then(undefined, () => undefined);
}

function registerBreakpointForwarding(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.debug.onDidChangeBreakpoints((e) => {
            sendBreakpointSnapshot();
            // While a DAP session owns breakpoints — the AI mirror via its setBreakpoints, or
            // a real netcoredbg session natively — forwarding here would double them. The
            // shared snapshot above still goes out, so the persisted set stays correct.
            const activeType = vscode.debug.activeDebugSession?.type;
            const adapterAttached = activeType === 'roslynsense-ai' || activeType === DEBUG_TYPE;
            for (const bp of e.added) {
                const key = vscodeBpKey(bp);
                if (!key || suppressedAdds.delete(key) || adapterAttached) {
                    continue;
                }
                if (bp instanceof vscode.SourceBreakpoint && !serverBpIds.has(key)) {
                    void sendBpCommand('set_breakpoint', {
                        file: bp.location.uri.fsPath,
                        line: bp.location.range.start.line + 1,
                        condition: bp.condition,
                    });
                }
            }
            for (const bp of e.removed) {
                const key = vscodeBpKey(bp);
                if (!key || suppressedRemovals.delete(key) || adapterAttached) {
                    continue;
                }
                const id = serverBpIds.get(key);
                if (id !== undefined) {
                    serverBpIds.delete(key);
                    void sendBpCommand('remove_breakpoint', { breakpointId: id });
                }
            }
        })
    );
}

async function revealLlmFrame(session: DebugSessionInfo): Promise<void> {
    if (!session.filePath || session.state !== 'stopped') {
        return;
    }
    const key = `${session.ownerPid}:${session.filePath}:${session.line}:${session.reason}`;
    if (key === lastRevealedFrame) {
        return;
    }
    lastRevealedFrame = key;
    try {
        const doc = await vscode.workspace.openTextDocument(session.filePath);
        const editor = await vscode.window.showTextDocument(doc, {
            preserveFocus: true,
            preview: true,
        });
        const line = Math.min(Math.max(session.line - 1, 0), doc.lineCount - 1);
        editor.revealRange(
            doc.lineAt(line).range,
            vscode.TextEditorRevealType.InCenterIfOutsideViewport
        );
    } catch {
        // File outside the workspace (e.g. decompiled) — the status bar still shows it.
    }
}

function registerDebugBridge(context: vscode.ExtensionContext): void {
    debugStatusItem = vscode.window.createStatusBarItem(
        'roslynSense.debug', vscode.StatusBarAlignment.Left, 89);
    debugStatusItem.name = 'RoslynSense AI Debugger';
    debugStatusItem.command = 'roslynSense.showDebugSession';
    context.subscriptions.push(debugStatusItem);

    pausedLineDecoration = vscode.window.createTextEditorDecorationType({
        isWholeLine: true,
        backgroundColor: new vscode.ThemeColor('editor.stackFrameHighlightBackground'),
        overviewRulerColor: new vscode.ThemeColor('editorOverviewRuler.warningForeground'),
        overviewRulerLane: vscode.OverviewRulerLane.Full,
    });
    context.subscriptions.push(pausedLineDecoration);

    registerBreakpointForwarding(context);

    let sessions: DebugSessionInfo[] = [];

    const poll = async (): Promise<void> => {
        if (!client) {
            debugStatusItem?.hide();
            return;
        }
        try {
            sessions = await client.sendRequest<DebugSessionInfo[]>('roslynSense/debugSessions');
        } catch {
            debugStatusItem?.hide();
            return;
        }
        syncNativeBreakpoints(sessions);
        const active = sessions.find((s) => s.state === 'stopped') ?? sessions[0];
        if (!active || active.state === 'exited') {
            debugStatusItem?.hide();
            applyPausedDecoration(undefined);
            lastRevealedFrame = undefined;
            return;
        }
        if (debugStatusItem) {
            const where =
                active.state === 'stopped' && active.filePath
                    ? `${active.filePath.split(/[\\/]/).pop()}:${active.line}`
                    : active.state;
            debugStatusItem.text = `$(debug-alt) AI debug: ${where}`;
            debugStatusItem.tooltip =
                `RoslynSense: AI-driven debug session (${active.kind} ${active.target})\n` +
                `State: ${active.state}${active.reason ? ` (${active.reason})` : ''}\n` +
                'Click for controls.';
            debugStatusItem.show();
        }
        applyPausedDecoration(active);
        if (active.state === 'stopped') {
            void revealLlmFrame(active);
        }
    };
    debugPollTimer = setInterval(() => void poll(), 2000);
    context.subscriptions.push({ dispose: () => clearInterval(debugPollTimer) });
    void poll();

    context.subscriptions.push(
        vscode.window.onDidChangeVisibleTextEditors(() =>
            applyPausedDecoration(sessions.find((s) => s.state === 'stopped'))
        ),
        vscode.commands.registerCommand('roslynSense.showDebugSession', async () => {
            if (!client || sessions.length === 0) {
                void vscode.window.showInformationMessage('RoslynSense: no AI debug session.');
                return;
            }
            const session = sessions.find((s) => s.state === 'stopped') ?? sessions[0];
            const actions: { label: string; action: string }[] = [
                { label: '$(debug-alt) Open in VSCode debugger', action: 'open_debugger' },
                { label: '$(debug-continue) Continue', action: 'continue' },
                { label: '$(debug-step-over) Step Over', action: 'step_over' },
                { label: '$(debug-step-into) Step In', action: 'step_in' },
                { label: '$(debug-step-out) Step Out', action: 'step_out' },
                { label: '$(search) Evaluate…', action: 'evaluate' },
                { label: '$(symbol-variable) Locals', action: 'locals' },
                { label: '$(list-tree) Stack Trace', action: 'stacktrace' },
                { label: '$(info) Status', action: 'status' },
                { label: '$(debug-stop) Stop Session', action: 'stop' },
            ];
            const picked = await vscode.window.showQuickPick(actions, {
                placeHolder:
                    `AI debug session (${session.kind} ${session.target}) — ` +
                    `${session.state}${session.filePath ? ` at ${session.filePath}:${session.line}` : ''}`,
            });
            if (!picked) {
                return;
            }
            if (picked.action === 'open_debugger') {
                await vscode.debug.startDebugging(vscode.workspace.workspaceFolders?.[0], {
                    type: 'roslynsense-ai',
                    request: 'attach',
                    name: 'AI Debug Session',
                    ownerPid: session.ownerPid,
                });
                return;
            }
            let expression: string | undefined;
            if (picked.action === 'evaluate') {
                expression = await vscode.window.showInputBox({
                    prompt: 'Expression to evaluate in the AI debug session',
                });
                if (!expression) {
                    return;
                }
            }
            try {
                const result = await client.sendRequest<DebugCommandResult>(
                    'roslynSense/debugCommand',
                    { ownerPid: session.ownerPid, action: picked.action, expression }
                );
                const channel = debugChannel();
                channel.appendLine(`> ${picked.action}${expression ? ` ${expression}` : ''}`);
                channel.appendLine(result.result);
                if (picked.action === 'evaluate' || picked.action === 'status' || !result.ok) {
                    channel.show(true);
                } else {
                    void vscode.window.setStatusBarMessage(
                        `RoslynSense: ${picked.action} sent`, 3000);
                }
                void poll();
            } catch (err) {
                void vscode.window.showErrorMessage(`RoslynSense debug command failed: ${err}`);
            }
        })
    );

    registerEditorDebugTracker(context);
}

// ---- Native VSCode debugging of the AI session (inline DAP adapter) ------------------
//
// Presents the chat-owned debug session as a real VSCode debug session: debug toolbar
// (continue/step), variables pane, call stack, watch, and gutter breakpoints — all
// forwarded over roslynSense/debugCommand to the owning chat process. Frame 0 carries a
// full path (from the state store); deeper frames only have file names, so they render
// without sources.

/// Scope references are frame ids offset into their own band so a scope is never mistaken for
/// a variable handle (the backend hands those out from 1000).
const SCOPE_BASE = 1;
const SCOPE_RANGE = 999;

interface StructuredFrame {
    id: number;
    name: string;
    filePath: string;
    line: number;
    column: number;
    isExternal: boolean;
}

interface StructuredVariable {
    name: string;
    value: string;
    type: string;
    variablesReference: number;
    namedChildCount: number;
    indexedChildCount: number;
    evaluable: boolean;
}

function parseJson<T>(text: string): T | undefined {
    try {
        return JSON.parse(text) as T;
    } catch {
        return undefined;
    }
}

class AiDebugAdapter implements vscode.DebugAdapter {
    private readonly emitter = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
    readonly onDidSendMessage = this.emitter.event;

    private seq = 1;
    private ownerPid: number | undefined;
    private lastState: string | undefined;
    private pollTimer: NodeJS.Timeout | undefined;
    private disposed = false;

    constructor(private readonly configOwnerPid?: number) {}

    dispose(): void {
        this.disposed = true;
        if (this.pollTimer) {
            clearInterval(this.pollTimer);
        }
        this.emitter.dispose();
    }

    private send(message: Record<string, unknown>): void {
        if (!this.disposed) {
            this.emitter.fire({ ...message, seq: this.seq++ } as vscode.DebugProtocolMessage);
        }
    }

    private respond(request: any, body?: unknown, success = true, message?: string): void {
        this.send({
            type: 'response',
            request_seq: request.seq,
            command: request.command,
            success,
            message,
            body,
        });
    }

    private event(event: string, body?: unknown): void {
        this.send({ type: 'event', event, body });
    }

    private async command(action: string, extra?: Record<string, unknown>): Promise<DebugCommandResult> {
        if (!client || this.ownerPid === undefined) {
            return { ok: false, result: 'No connection to the AI debug session.' };
        }
        return client.sendRequest<DebugCommandResult>('roslynSense/debugCommand', {
            ownerPid: this.ownerPid,
            action,
            ...extra,
        });
    }

    /// Runs a command whose result is a JSON payload, returning undefined when it failed or
    /// came back as something other than JSON.
    private async structured<T>(action: string, extra?: Record<string, unknown>): Promise<T | undefined> {
        const result = await this.command(action, extra);
        return result.ok ? parseJson<T>(result.result) : undefined;
    }

    private async currentSession(): Promise<DebugSessionInfo | undefined> {
        if (!client) {
            return undefined;
        }
        try {
            const sessions = await client.sendRequest<DebugSessionInfo[]>('roslynSense/debugSessions');
            return this.ownerPid === undefined
                ? sessions[0]
                : sessions.find((s) => s.ownerPid === this.ownerPid);
        } catch {
            return undefined;
        }
    }

    async handleMessage(message: any): Promise<void> {
        if (message.type !== 'request') {
            return;
        }
        try {
            await this.handleRequest(message);
        } catch (err) {
            this.respond(message, undefined, false, String(err));
        }
    }

    private async handleRequest(request: any): Promise<void> {
        switch (request.command) {
            case 'initialize':
                this.respond(request, {
                    supportsConfigurationDoneRequest: true,
                    supportsEvaluateForHovers: true,
                    supportsSetVariable: true,
                    supportsTerminateRequest: true,
                    supportTerminateDebuggee: false,
                    supportsConditionalBreakpoints: true,
                    // All three are emulated server-side; neither engine implements them.
                    supportsHitConditionalBreakpoints: true,
                    supportsLogPoints: true,
                    supportsDataBreakpoints: true,
                    supportsExceptionInfoRequest: true,
                    supportsExceptionFilterOptions: true,
                    exceptionBreakpointFilters: [
                        { filter: 'all', label: 'All Exceptions', default: false },
                        { filter: 'user-unhandled', label: 'User-Unhandled Exceptions', default: true },
                    ],
                });
                this.event('initialized');
                return;

            case 'attach': {
                this.ownerPid = this.configOwnerPid ?? request.arguments?.ownerPid;
                const session = await this.currentSession();
                if (!session) {
                    this.respond(request, undefined, false,
                        'No AI debug session found. Ask the chat to start one (debug_start_test / debug_attach).');
                    this.event('terminated');
                    return;
                }
                this.ownerPid = session.ownerPid;
                this.respond(request);
                this.startPolling();
                if (session.state === 'stopped') {
                    this.event('stopped', {
                        reason: session.reason ?? 'breakpoint',
                        threadId: 1,
                        allThreadsStopped: true,
                    });
                    this.lastState = 'stopped';
                } else {
                    this.lastState = session.state;
                }
                return;
            }

            case 'configurationDone':
                this.respond(request);
                return;

            case 'setExceptionBreakpoints': {
                const filters: string[] = request.arguments?.filters ??
                    (request.arguments?.filterOptions ?? []).map((o: { filterId: string }) => o.filterId);
                const result = await this.command('exception_filters', { filters });
                this.respond(request, undefined, result.ok, result.ok ? undefined : result.result);
                return;
            }

            case 'threads': {
                const threads = await this.structured<{ id: number; name: string }[]>('threads');
                this.respond(request, {
                    threads: threads?.length
                        ? threads.map((t) => ({ id: t.id, name: t.name }))
                        : [{ id: 1, name: 'AI Debug Session' }],
                });
                return;
            }

            case 'stackTrace': {
                const frames = await this.structured<StructuredFrame[]>('frames');
                if (frames?.length) {
                    this.respond(request, {
                        stackFrames: frames.map((f) => ({
                            id: f.id,
                            name: f.name,
                            source: f.filePath
                                ? { name: f.filePath.split(/[\\/]/).pop(), path: f.filePath }
                                : undefined,
                            line: f.line,
                            column: f.column || 1,
                            presentationHint: f.isExternal ? 'subtle' : undefined,
                        })),
                        totalFrames: frames.length,
                    });
                    return;
                }

                // No structured stack (an engine that cannot walk it, or a session that just
                // exited) — the published stop location is still worth showing.
                const session = await this.currentSession();
                const fallback: unknown[] = [];
                if (session?.state === 'stopped' && session.filePath) {
                    fallback.push({
                        id: 0,
                        name: session.function ?? 'current frame',
                        source: { name: session.filePath.split(/[\\/]/).pop(), path: session.filePath },
                        line: session.line,
                        column: 1,
                    });
                }
                this.respond(request, { stackFrames: fallback, totalFrames: fallback.length });
                return;
            }

            case 'scopes':
                // Frame ids are the backend's own frame indices, so the scope reference has to
                // carry the frame with it; SCOPE_BASE keeps it clear of variable references.
                this.respond(request, {
                    scopes: [{
                        name: 'Locals',
                        variablesReference: SCOPE_BASE + (request.arguments?.frameId ?? 0),
                        expensive: false,
                    }],
                });
                return;

            case 'variables': {
                const reference: number = request.arguments?.variablesReference ?? SCOPE_BASE;
                const variables = reference >= SCOPE_BASE && reference < SCOPE_BASE + SCOPE_RANGE
                    ? await this.structured<StructuredVariable[]>('variables', {
                        frameId: reference - SCOPE_BASE,
                    })
                    : await this.structured<StructuredVariable[]>('children', {
                        variablesReference: reference,
                    });

                this.respond(request, {
                    variables: (variables ?? []).map((v) => ({
                        name: v.name,
                        value: v.value,
                        type: v.type || undefined,
                        variablesReference: v.variablesReference,
                        namedVariables: v.namedChildCount || undefined,
                        indexedVariables: v.indexedChildCount || undefined,
                        evaluateName: v.name,
                    })),
                });
                return;
            }

            case 'setVariable': {
                const result = await this.command('set_variable', {
                    expression: request.arguments?.name,
                    value: request.arguments?.value,
                    frameId: 0,
                });
                if (!result.ok) {
                    this.respond(request, undefined, false, result.result);
                    return;
                }
                const parsed = parseJson<{ ok: boolean; value: string; error: string }>(result.result);
                this.respond(
                    request,
                    { value: parsed?.value ?? request.arguments?.value },
                    parsed?.ok !== false,
                    parsed?.ok === false ? parsed.error : undefined);
                return;
            }

            case 'exceptionInfo': {
                const detail = await this.structured<{
                    typeName: string; message: string; breakMode: string;
                }>('exception_info');
                if (!detail) {
                    this.respond(request, undefined, false, 'The session did not stop on an exception.');
                    return;
                }
                this.respond(request, {
                    exceptionId: detail.typeName,
                    description: detail.message,
                    breakMode: detail.breakMode,
                    details: { message: detail.message, typeName: detail.typeName },
                });
                return;
            }

            case 'continue':
            case 'next':
            case 'stepIn':
            case 'stepOut': {
                const action =
                    request.command === 'continue' ? 'continue' :
                    request.command === 'next' ? 'step_over' :
                    request.command === 'stepIn' ? 'step_in' : 'step_out';
                this.lastState = 'running';
                this.event('continued', { threadId: 1, allThreadsContinued: true });
                const result = await this.command(action); // blocks until the next stop
                this.respond(request, request.command === 'continue' ? { allThreadsContinued: true } : undefined);
                const session = await this.currentSession();
                if (session?.state === 'stopped') {
                    this.lastState = 'stopped';
                    // A watched value changing outranks the reason the resume started with: the
                    // user pressed Continue, but the watch is what stopped them.
                    const hit = await this.structured<{ description: string }>('data_hit');
                    this.event('stopped', {
                        reason: hit
                            ? 'data breakpoint'
                            : session.reason ?? (action === 'continue' ? 'breakpoint' : 'step'),
                        description: hit?.description,
                        text: hit?.description,
                        threadId: 1,
                        allThreadsStopped: true,
                    });
                } else if (!session || session.state === 'exited' || !result.ok) {
                    this.event('terminated');
                }
                return;
            }

            case 'evaluate': {
                const result = await this.command('evaluate', {
                    expression: request.arguments?.expression,
                });
                this.respond(request, { result: result.result, variablesReference: 0 }, result.ok,
                    result.ok ? undefined : result.result);
                return;
            }

            case 'setBreakpoints': {
                // VSCode sends the FULL wanted list for the file — diff against the
                // server's list so a breakpoint removed in this UI is removed in the chat's
                // debugger too (and other editors' glyphs follow on their next poll).
                const source = request.arguments?.source;
                const wanted: {
                    line: number;
                    condition?: string;
                    hitCondition?: string;
                    logMessage?: string;
                }[] = request.arguments?.breakpoints ?? [];
                const filePath: string | undefined = source?.path;
                const existing = (await this.currentSession())?.breakpoints?.filter(
                    (b) => filePath && b.file.toLowerCase() === filePath.toLowerCase()
                ) ?? [];

                for (const bp of existing) {
                    if (bp.id > 0 && !wanted.some((w) => w.line === bp.line)) {
                        await this.command('remove_breakpoint', { breakpointId: bp.id });
                    }
                }
                const verified: unknown[] = [];
                for (const bp of wanted) {
                    // A hit condition or log message has to reach the backend even when the line
                    // already carries a breakpoint, since that is where both are emulated.
                    if (existing.some((e) => e.line === bp.line) && !bp.hitCondition && !bp.logMessage) {
                        verified.push({ verified: true, line: bp.line });
                        continue;
                    }
                    const result = await this.command('set_breakpoint', {
                        file: filePath,
                        line: bp.line,
                        condition: bp.condition,
                        hitCondition: bp.hitCondition,
                        logMessage: bp.logMessage,
                    });
                    verified.push({ verified: result.ok, line: bp.line });
                }
                this.respond(request, { breakpoints: verified });
                return;
            }

            case 'dataBreakpointInfo': {
                // The id has to survive a round trip through VSCode, so it carries the frame the
                // name was read in rather than a handle the server would have to remember.
                const name: string = request.arguments?.name ?? '';
                const frameId: number = request.arguments?.frameId ?? 0;
                this.respond(request, name.length === 0
                    ? { dataId: null, description: 'Break on value change needs a named value.' }
                    : {
                        dataId: `${frameId}:${name}`,
                        description: `${name} (break when the value changes)`,
                        // A read leaves the value alone, so it cannot be seen by comparing one.
                        accessTypes: ['write'],
                        canPersist: false,
                    });
                return;
            }

            case 'setDataBreakpoints': {
                const wanted: { dataId: string; accessType?: string; condition?: string; hitCondition?: string }[] =
                    request.arguments?.breakpoints ?? [];

                const result = await this.command('set_data_breakpoints', {
                    dataBreakpoints: wanted.map((bp) => ({
                        dataId: bp.dataId,
                        expression: bp.dataId.slice(bp.dataId.indexOf(':') + 1),
                        accessType: bp.accessType ?? 'write',
                        condition: bp.condition,
                        hitCondition: bp.hitCondition,
                    })),
                });

                const statuses = result.ok
                    ? parseJson<{ verified: boolean; message: string }[]>(result.result) ?? []
                    : [];
                this.respond(request, {
                    breakpoints: wanted.map((_, i) => ({
                        verified: statuses[i]?.verified ?? false,
                        message: statuses[i]?.message || undefined,
                    })),
                }, result.ok, result.ok ? undefined : result.result);
                return;
            }

            case 'pause': {
                const result = await this.command('pause');
                this.respond(request, undefined, result.ok, result.ok ? undefined : result.result);
                if (result.ok) {
                    this.lastState = 'stopped';
                    this.event('stopped', { reason: 'pause', threadId: 1, allThreadsStopped: true });
                }
                return;
            }

            case 'terminate': {
                // The chat's session is the chat's to keep, but the user asked for this one.
                const result = await this.command('stop');
                this.respond(request, undefined, result.ok, result.ok ? undefined : result.result);
                this.event('terminated');
                return;
            }

            case 'disconnect':
                // Leave the chat's session alive — disconnecting the UI must not kill it.
                this.respond(request);
                this.event('terminated');
                this.dispose();
                return;

            default:
                this.respond(request, undefined, false, `Unsupported request '${request.command}'.`);
                return;
        }
    }

    /// Watches for state changes made OUTSIDE this adapter (the chat stepping/continuing).
    private startPolling(): void {
        this.pollTimer = setInterval(async () => {
            const session = await this.currentSession();
            if (this.disposed) {
                return;
            }

            // Logpoints are resumed through server-side, so their output only surfaces here.
            const log = await this.structured<string[]>('drain_log');
            for (const line of log ?? []) {
                this.event('output', { category: 'console', output: line + '\n' });
            }
            if (!session || session.state === 'exited') {
                this.event('terminated');
                this.dispose();
                return;
            }
            if (session.state !== this.lastState) {
                this.lastState = session.state;
                if (session.state === 'stopped') {
                    this.event('stopped', {
                        reason: session.reason ?? 'breakpoint',
                        threadId: 1,
                        allThreadsStopped: true,
                        description: 'Paused by the AI chat',
                    });
                } else {
                    this.event('continued', { threadId: 1, allThreadsContinued: true });
                }
            }
        }, 1500);
    }
}

function registerAiDebugAdapter(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory('roslynsense-ai', {
            createDebugAdapterDescriptor(session: vscode.DebugSession) {
                return new vscode.DebugAdapterInlineImplementation(
                    new AiDebugAdapter(session.configuration.ownerPid));
            },
        }),
        vscode.commands.registerCommand('roslynSense.openAiDebugSession', async () => {
            await vscode.debug.startDebugging(vscode.workspace.workspaceFolders?.[0], {
                type: 'roslynsense-ai',
                request: 'attach',
                name: 'AI Debug Session',
            });
        })
    );
}

// ---- Editor debug session → server (and LLM commands back into it) -------------------

interface TrackedEditorDebug {
    session: vscode.DebugSession;
    threadId: number | undefined;
    frameId: number | undefined;
    state: 'running' | 'stopped';
    reason: string | undefined;
    filePath: string | undefined;
    line: number; // 1-based
}

let trackedEditorDebug: TrackedEditorDebug | undefined;

function reportEditorDebugState(): void {
    if (!client) {
        return;
    }
    const solutionPath =
        activeSolutionPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!solutionPath) {
        return;
    }
    const t = trackedEditorDebug;
    void client
        .sendNotification('roslynSense/editorDebugState', {
            solutionPath,
            active: t !== undefined,
            sessionName: t?.session.name,
            adapterType: t?.session.type,
            executionState: t?.state ?? 'running',
            reason: t?.reason,
            filePath: t?.filePath,
            line: t?.line ?? 0,
        })
        .then(undefined, () => undefined);
}

function registerEditorDebugTracker(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterTrackerFactory('*', {
            createDebugAdapterTracker(session: vscode.DebugSession) {
                // The AI-session mirror is not the user's own debugging — reporting it back
                // to the server would make the chat see its own session as "the user's".
                if (session.type === 'roslynsense-ai') {
                    return {};
                }
                return {
                    onDidSendMessage: (message: any) => {
                        if (message.type !== 'event') {
                            return;
                        }
                        if (message.event === 'stopped') {
                            const threadId: number | undefined = message.body?.threadId;
                            trackedEditorDebug = {
                                session,
                                threadId,
                                frameId: undefined,
                                state: 'stopped',
                                reason: message.body?.reason,
                                filePath: undefined,
                                line: 0,
                            };
                            void captureTopFrame(session, threadId);
                        } else if (message.event === 'continued') {
                            if (trackedEditorDebug?.session === session) {
                                trackedEditorDebug.state = 'running';
                                trackedEditorDebug.frameId = undefined;
                                reportEditorDebugState();
                            }
                        }
                    },
                };
            },
        }),
        vscode.debug.onDidStartDebugSession((started) => {
            if (started.type === 'roslynsense-ai') {
                return;
            }
            // A session with no stop yet is still worth announcing as running.
            if (!trackedEditorDebug && vscode.debug.activeDebugSession) {
                trackedEditorDebug = {
                    session: vscode.debug.activeDebugSession,
                    threadId: undefined,
                    frameId: undefined,
                    state: 'running',
                    reason: undefined,
                    filePath: undefined,
                    line: 0,
                };
            }
            reportEditorDebugState();
        }),
        vscode.debug.onDidTerminateDebugSession((session) => {
            if (trackedEditorDebug?.session === session) {
                trackedEditorDebug = undefined;
            }
            reportEditorDebugState();
        })
    );
}

async function captureTopFrame(
    session: vscode.DebugSession, threadId: number | undefined
): Promise<void> {
    if (threadId === undefined) {
        reportEditorDebugState();
        return;
    }
    try {
        const stack = await session.customRequest('stackTrace', { threadId, levels: 1 });
        const frame = stack?.stackFrames?.[0];
        if (frame && trackedEditorDebug?.session === session) {
            trackedEditorDebug.frameId = frame.id;
            trackedEditorDebug.filePath = frame.source?.path;
            trackedEditorDebug.line = frame.line ?? 0;
        }
    } catch {
        // Adapter may refuse while transitioning — state still gets reported without a frame.
    }
    reportEditorDebugState();
}

// Server→client: an MCP chat drives the user's editor debug session via DAP.
// Returns null when this window has no active debug session (the daemon then reports
// failure to the chat).
function wireEditorDebugCommandHandler(c: LanguageClient): void {
    c.onRequest(
        'roslynSense/editorDebugCommand',
        async (p: {
            action: string;
            expression?: string;
            file?: string;
            line?: number;
            condition?: string;
        }): Promise<string | null> => {
            const t = trackedEditorDebug;

            // Breakpoint management works session-less via the vscode.debug API.
            if (p.action === 'set_breakpoint' && p.file && p.line) {
                const location = new vscode.Location(
                    vscode.Uri.file(p.file),
                    new vscode.Position(Math.max(p.line - 1, 0), 0)
                );
                vscode.debug.addBreakpoints([
                    new vscode.SourceBreakpoint(location, true, p.condition || undefined),
                ]);
                return `Breakpoint set in the editor at ${p.file}:${p.line}` +
                    (p.condition ? ` (condition: ${p.condition})` : '') + '.';
            }

            if (!t) {
                return null;
            }
            const session = t.session;
            try {
                switch (p.action) {
                    case 'continue':
                        await session.customRequest('continue', { threadId: t.threadId ?? 0 });
                        t.state = 'running';
                        reportEditorDebugState();
                        return 'Continued. The editor debugger is running.';
                    case 'step_over':
                        await session.customRequest('next', { threadId: t.threadId ?? 0 });
                        return 'Stepped over.';
                    case 'step_in':
                        await session.customRequest('stepIn', { threadId: t.threadId ?? 0 });
                        return 'Stepped in.';
                    case 'step_out':
                        await session.customRequest('stepOut', { threadId: t.threadId ?? 0 });
                        return 'Stepped out.';
                    case 'evaluate': {
                        if (!p.expression) {
                            return 'Error: no expression.';
                        }
                        const evalResult = await session.customRequest('evaluate', {
                            expression: p.expression,
                            frameId: t.frameId,
                            context: 'repl',
                        });
                        return `${p.expression} = ${evalResult?.result ?? '<no result>'}`;
                    }
                    case 'locals': {
                        if (t.frameId === undefined) {
                            return 'Error: not stopped at a frame.';
                        }
                        const scopes = await session.customRequest('scopes', { frameId: t.frameId });
                        const lines: string[] = [];
                        for (const scope of scopes?.scopes ?? []) {
                            if (scope.expensive) {
                                continue;
                            }
                            const vars = await session.customRequest('variables', {
                                variablesReference: scope.variablesReference,
                            });
                            for (const v of vars?.variables ?? []) {
                                lines.push(`${v.name} = ${v.value}`);
                            }
                        }
                        return lines.length > 0 ? lines.join('\n') : 'No locals.';
                    }
                    case 'stacktrace': {
                        if (t.threadId === undefined) {
                            return 'Error: not stopped.';
                        }
                        const stack = await session.customRequest('stackTrace', {
                            threadId: t.threadId,
                            levels: 20,
                        });
                        return (stack?.stackFrames ?? [])
                            .map(
                                (f: any, i: number) =>
                                    `#${i} ${f.name} at ${f.source?.path ?? '?'}:${f.line}`
                            )
                            .join('\n');
                    }
                    case 'status':
                        return (
                            `Editor debug session '${session.name}' (${session.type}): ${t.state}` +
                            (t.state === 'stopped' && t.filePath
                                ? ` at ${t.filePath}:${t.line}` +
                                  (t.reason ? ` (${t.reason})` : '')
                                : '') + '.'
                        );
                    case 'run_until': {
                        if (!p.file || !p.line) {
                            return 'Error: run_until needs file and line.';
                        }
                        const location = new vscode.Location(
                            vscode.Uri.file(p.file),
                            new vscode.Position(Math.max(p.line - 1, 0), 0)
                        );
                        vscode.debug.addBreakpoints([
                            new vscode.SourceBreakpoint(location, true, p.condition || undefined),
                        ]);
                        await session.customRequest('continue', { threadId: t.threadId ?? 0 });
                        t.state = 'running';
                        reportEditorDebugState();
                        return `Breakpoint set at ${p.file}:${p.line} and continued. ` +
                            'The breakpoint remains in the editor; check DebugStatus for the stop.';
                    }
                    default:
                        return `Error: unknown editor debug action '${p.action}'.`;
                }
            } catch (err) {
                return `Error from the editor debugger: ${err}`;
            }
        }
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
    registerConfigurationSync(context);
    registerLensCommands(context);
    registerOnAutoInsert(context);
    registerProcessStatusBar(context);
    registerInheritanceMarkers(context);
    registerDebugBridge(context);
    registerAiDebugAdapter(context);
    registerDebugLaunch(context, () => client);
    registerTestController(context, () => client);
    registerSolutionExplorer(context, () => client);
    registerVirtualDocuments(context, () => client);
    registerNuGetPanel(context, () => client);
    registerTaskProvider(context, () => client);
    registerHotReload(context, () => client);
    registerEditorContext(
        context,
        () => client,
        () => activeSolutionPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath
    );

    await startClient(context);

    // Multi-root: follow the focused editor to whichever solution owns it.
    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor((editor) =>
            void bindActiveEditor(context, editor?.document)),
        vscode.workspace.onDidChangeWorkspaceFolders(() => solutionByFolder.clear())
    );
}

export async function deactivate(): Promise<void> {
    statusItem?.dispose();
    statusItem = undefined;
    await stopClient();
}
