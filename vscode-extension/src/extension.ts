import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { PassThrough } from 'stream';
import {
    CloseAction,
    ErrorAction,
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    State,
    StreamInfo,
    TransportKind,
} from 'vscode-languageclient/node';
import { DEBUG_TYPE, registerDebugLaunch } from './debugLaunch';
import { additionalConfigGlobs, homeDirectory, loadLayers } from './roslynsenseConfig';
import { registerTestController, runTestById } from './testController';
import { registerImpactedTests } from './impactedTests';
import { registerCoverageMapProgress } from './coverageMapProgress';
import { registerProjectSet } from './projectSet';
import { registerSolutionReady } from './solutionReady';
import { registerCoverageExplorer } from './coverageExplorer';
import { registerChangedMembers } from './changedMembers';
import { registerSolutionExplorer } from './solutionExplorer';
import { registerSearchEverywhere } from './search';
import { registerSettingsPanel } from './settings';
import { registerVirtualDocuments } from './virtualDocuments';
import { registerEmbeddedLanguages } from './embeddedLanguages';
import { registerNuGetPanel } from './nuget';
import { createRedactingTraceChannel, wireNuGetCredentials } from './nuget/credentials';
import { registerTaskProvider } from './taskProvider';
import { registerEditorContext } from './editorContext';
import { registerHotReload } from './hotReload';
import { bindNestedCodeActions, registerNestedCodeActions } from './nestedCodeActions';
import { lensesToPreResolve } from './codeLensPrewarm';

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

/** A language the server handles beyond C#, mirroring one language pack on the server side. */
export interface ExtraLanguage {
    /**
     * The server's pack id, VS Code's language id and the `roslynSense.languages.<id>` setting
     * key, deliberately all one string so a new language is one entry here and one contributed
     * setting rather than a match to keep in step across three lists.
     */
    readonly id: string;
    /**
     * The file extensions the language owns, leading dot included. Both the watcher glob and the
     * breakpoint filter derive from this rather than repeating it, because the two drifting apart
     * means a file the server tracks that the gutter refuses, or the reverse.
     */
    readonly extensions: readonly string[];
    /**
     * Whole file names the language owns, for the file types whose extension says less than their
     * name does — `packages.config` is NuGet's while `web.config` beside it is not, and no
     * extension distinguishes them.
     */
    readonly filenames?: readonly string[];
    /** Whether the gutter offers breakpoints in these documents. */
    readonly breakpoints: boolean;
    /**
     * Extensions and name globs already covered by a watcher outside this table, and so left out
     * of this language's own glob. Two watchers over one pattern means two
     * `didChangeWatchedFiles` for every save, and the server does its reload work per event.
     */
    readonly watchedElsewhere?: readonly string[];
    /**
     * Name globs the language owns inside a file type it does not — `appsettings*.json` is this
     * server's while `package.json` beside it stays the editor's. A row with patterns selects
     * documents by glob under `patternLanguages` instead of by its own language id, because the
     * files keep the host language's id and its highlighting.
     */
    readonly patterns?: readonly string[];
    /** The VS Code language ids the patterned files open under. */
    readonly patternLanguages?: readonly string[];
}

/** Files whose content the server tracks even while no editor has them open. */
function watchGlobs(language: ExtraLanguage): string[] {
    const globs: string[] = [];
    const own = language.extensions.filter(
        (extension) => !(language.watchedElsewhere ?? []).includes(extension),
    );

    if (own.length > 0) {
        globs.push(`**/*.{${own.map((extension) => extension.slice(1)).join(',')}}`);
    }

    // Case-insensitively, because NuGet writes `NuGet.Config` and the CLI writes `nuget.config`,
    // and a tree can contain both. A VS Code glob has no case-insensitive flag, so each letter
    // that varies becomes a class.
    for (const name of language.filenames ?? []) {
        globs.push(
            `**/${[...name].map((c) => (/[a-z]/i.test(c) ? `[${c.toLowerCase()}${c.toUpperCase()}]` : c)).join('')}`,
        );
    }

    globs.push(
        ...(language.patterns ?? []).filter(
            (pattern) => !(language.watchedElsewhere ?? []).includes(pattern),
        ),
    );

    return globs;
}

/**
 * The two framework config files, in the casings that occur on disk.
 *
 * Named here rather than only inside the `webconfig` row because they are claimed twice: by that
 * pack, for its `<appSettings>` reference counts, and unconditionally by the selector, for the
 * binding redirects the server answers about whether or not the pack is on.
 */
const CONFIG_FILE_PATTERNS = ['**/[wW]eb.config', '**/[aA]pp.config'] as const;

/**
 * The extra names `webConfig.additionalFiles` claims, as globs — DotNetNuke's `release.config` and
 * `development.config`, and anything else a framework keeps beside its `web.config`.
 *
 * Read from the file here rather than asked of the server: a document selector is fixed when the
 * client is constructed, which is before there is a server to ask. A name that is a path or a glob
 * is dropped silently — the server warns about it once, and warning twice for the same line in the
 * same file is noise.
 */
function additionalConfigPatterns(folder?: vscode.WorkspaceFolder): string[] {
    const directory = folder?.uri.fsPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!directory) {
        return [];
    }

    try {
        return additionalConfigGlobs(loadLayers(directory).merged);
    } catch {
        return [];
    }
}

/**
 * The non-C# languages this extension activates. Adding one means adding a row here and
 * contributing `roslynSense.languages.<id>`; the document selector, the file watchers and the
 * settings sent to the server all read this table rather than repeating the list.
 */
export const EXTRA_LANGUAGES: readonly ExtraLanguage[] = [
    {
        id: 'webforms',
        extensions: ['.aspx', '.ascx', '.master', '.asax', '.ashx', '.asmx'],
        breakpoints: true,
    },
    {
        // Contributed as its own language rather than left as XML: without the `resx` id in
        // `contributes.languages` VS Code opens the file as `xml`, the document selector below
        // never matches it, and the server is never told the buffer was opened.
        id: 'resx',
        extensions: ['.resx'],
        breakpoints: false,
    },
    {
        // No breakpoints: a .proto declares shapes and service signatures, and the generated C#
        // the debugger does bind to is a real compiled document Roslyn already owns.
        id: 'proto',
        extensions: ['.proto'],
        breakpoints: false,
    },
    {
        // No files of its own. A mediator send and the handler it reaches are both C#, which the
        // selector already covers unconditionally, so this row exists only to carry the id into
        // `serverSettings().languages` — the one thing that can switch the pack off for a window.
        id: 'mediator',
        extensions: [],
        breakpoints: false,
    },
    {
        // No files of its own either. A message template lives inside a C# string literal, so the
        // selector already covers it; this row exists only to carry the id into
        // `serverSettings().languages`.
        id: 'logging',
        extensions: [],
        breakpoints: false,
    },
    {
        // Nor this one. A composite format string and an interpolation's format clause are both
        // inside C#, and the markup half rides the WebForms selector.
        id: 'formatting',
        extensions: [],
        breakpoints: false,
    },
    {
        // Nor this one: a value set is a fact about a string literal, so the C# selector already
        // covers every file it has anything to say about.
        id: 'valuesets',
        extensions: [],
        breakpoints: false,
    },
    {
        // Nor this one: a schedule is a fact about a string literal too, and the Cron Jobs section
        // of the Solution Explorer hangs off the solution rather than off any file.
        id: 'cron',
        extensions: [],
        breakpoints: false,
    },
    {
        // Project files. Contributed as their own language for the same reason as resx: without
        // the id in `contributes.languages` VS Code opens a .csproj as `xml`, the selector below
        // never matches it, and the server is never told the buffer was opened.
        //
        // Taking .csproj away from `xml` costs the XML extension's formatting and folding, which
        // `contributes.xmlLanguageParticipants` in package.json hands back — it tells
        // redhat.vscode-xml to treat these documents as XML as well.
        id: 'msbuild',
        extensions: ['.csproj', '.fsproj', '.vbproj', '.props', '.targets'],
        // packages.config and nuget.config are NuGet's; web.config and app.config beside them
        // belong to the binding-redirect handler, and claiming `.config` would take all four.
        filenames: ['packages.config', 'nuget.config'],
        breakpoints: false,
        // Every project extension is already in the workspace watcher list below, which predates
        // this pack and is not gated on it being enabled.
        watchedElsewhere: ['.csproj', '.fsproj', '.vbproj', '.props', '.targets'],
    },
    {
        // LINQ to SQL models. Contributed as their own language for the same reason as resx and
        // msbuild: as `xml` the selector never matches and the server is never told the buffer
        // was opened.
        //
        // No breakpoints: a .dbml declares a schema mapping, and the generated designer the
        // debugger does bind to is a real compiled document Roslyn already owns.
        id: 'dbml',
        extensions: ['.dbml'],
        breakpoints: false,
    },
    {
        // Application configuration. Not a language of its own: the files are JSON and stay
        // JSON — the selector matches them by name shape so the server hears about exactly the
        // ones that feed IConfiguration, and package.json beside them stays untouched.
        //
        // package.json maps the same name shape to `jsonc`, because the configuration host
        // parses these with comments and trailing commas allowed and the plain `json` service
        // marks both as errors. `json` stays in the list: a `files.associations` entry, or a
        // user who picks the language by hand, puts the buffer back under it, and the server
        // should still hear about the file.
        id: 'appsettings',
        extensions: [],
        patterns: ['**/appsettings*.json', '**/secrets.json'],
        patternLanguages: ['json', 'jsonc'],
        breakpoints: false,
    },
    {
        // Framework configuration. Not a language of its own either: the files are XML and stay
        // XML, and the selector matches them by name so that `packages.config` and `nuget.config`
        // beside them stay the project-file pack's.
        //
        // Only `web.config` and `app.config` themselves. A `Web.Release.config` is an XDT
        // transform — its `<add>` elements are edits to apply at publish, not settings that
        // exist — and matching it would put reference counts on entries nothing ever reads.
        id: 'webconfig',
        extensions: [],
        patterns: [...CONFIG_FILE_PATTERNS],
        patternLanguages: ['xml'],
        breakpoints: false,
        // web.config already has a watcher below, ungated on purpose because the tag prefixes it
        // registers re-bind every control whether or not this pack is on.
        watchedElsewhere: ['**/[wW]eb.config'],
    },
];

/**
 * The entries this window has switched on. Absent means on: the settings only ever narrow what
 * the server registered, and a language the user has never had an opinion about should work.
 */
function enabledLanguages(): readonly ExtraLanguage[] {
    const config = vscode.workspace.getConfiguration('roslynSense.languages');
    return EXTRA_LANGUAGES.filter((language) => config.get<boolean>(language.id) !== false);
}

/**
 * The enabled entries that own files. A pack contributing only to answers about C# has no document
 * to select and no file to watch — it would contribute a language id VS Code has never heard of and
 * a glob of `**\/*.{}` — but it still belongs in `serverSettings().languages`, which is what
 * switches it off.
 */
function enabledFileLanguages(): readonly ExtraLanguage[] {
    return enabledLanguages().filter(
        (language) => language.extensions.length > 0 || (language.patterns?.length ?? 0) > 0,
    );
}

/**
 * One client per bound solution, keyed by solution path (or by workspace folder when the server
 * resolves the solution itself). A multi-root workspace with two solutions gets two daemons —
 * which is what already happens on the server side, since the daemon is per solution.
 */
const clientsBySolution = new Map<string, LanguageClient>();

/** One trace channel for every client; created lazily so a session that never traces has none. */
let redactingTrace: vscode.OutputChannel | undefined;

/** Which solution each workspace folder is bound to, resolved once per folder. */
const solutionByFolder = new Map<string, string | undefined>();

/** Finds the solution a file belongs to: the setting for its folder, else the nearest one. */
/**
 * What a repository checks out beside its own source and is never the solution being worked on:
 * package caches, build output, and the worktrees agent tooling leaves behind — each of which
 * holds a *copy* of the very solution being looked for.
 */
const SOLUTION_SEARCH_EXCLUDE = '**/{node_modules,bin,obj,artifacts,.git,.claude,.worktrees}/**';

/**
 * The solution a root belongs to.
 *
 * @param allowPrompt Whether an ambiguous root may ask the user. True only for the root being
 * opened: a background binding must not put a dialog on screen. When it is false and the answer is
 * ambiguous the caller gets nothing, which leaves the server to resolve from its working directory.
 */
async function solutionForFolder(
    folder: vscode.WorkspaceFolder,
    allowPrompt = false
): Promise<string | undefined> {
    const key = folder.uri.fsPath;
    if (solutionByFolder.has(key)) {
        return solutionByFolder.get(key);
    }

    // Folder-scoped so each root of a multi-root workspace can name its own solution.
    const configured = vscode.workspace
        .getConfiguration('roslynSense', folder.uri)
        .get<string>('solutionPath', '');

    let resolved: string | undefined = configured || undefined;
    resolved ??= await solutionAtRoot(folder);

    if (!resolved) {
        const found = await vscode.workspace.findFiles(
            new vscode.RelativePattern(folder, '**/*.{sln,slnx}'),
            SOLUTION_SEARCH_EXCLUDE,
            2
        );

        // Exactly one is unambiguous. More than one has to be chosen, and choosing is why the
        // answer is cached: the binding key every later client is matched against is built from
        // it, so resolving it twice and differently starts a second client over the same files.
        resolved =
            found.length === 1 ? found[0].fsPath : allowPrompt ? await pickSolution() : undefined;
    }

    solutionByFolder.set(key, resolved);
    return resolved;
}

/**
 * The single solution file sitting directly in the root, or nothing.
 *
 * One directory read, ahead of the recursive `findFiles` below it, because that is where a solution
 * almost always is and because `findFiles` is not cheap on the repositories where startup latency
 * is actually felt: it walks the tree — or waits on VS Code's file index to finish being built —
 * and activation was awaiting it before the language client had been started at all. A
 * `readDirectory` is a single syscall against a directory the editor has open anyway.
 *
 * Silent on anything other than exactly one match. Zero means look harder; more than one is
 * genuinely ambiguous and the recursive search reaches the same conclusion by the same rule, so
 * answering here would only duplicate the decision in two places.
 */
async function solutionAtRoot(folder: vscode.WorkspaceFolder): Promise<string | undefined> {
    try {
        const entries = await vscode.workspace.fs.readDirectory(folder.uri);
        const solutions = entries
            .filter(([name, type]) => type === vscode.FileType.File && /\.slnx?$/i.test(name))
            .map(([name]) => name);

        return solutions.length === 1
            ? vscode.Uri.joinPath(folder.uri, solutions[0]).fsPath
            : undefined;
    } catch {
        // Unreadable root (a virtual or disconnected workspace) — let the search below decide.
        return undefined;
    }
}

async function pickSolution(): Promise<string | undefined> {
    const config = vscode.workspace.getConfiguration('roslynSense');
    const configured = config.get<string>('solutionPath', '');
    if (configured) {
        return configured;
    }

    const solutions = await vscode.workspace.findFiles(
        '**/*.{sln,slnx}', SOLUTION_SEARCH_EXCLUDE, 25);
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
function serverSettings(registerCommands = true): Record<string, unknown> {
    const config = vscode.workspace.getConfiguration('roslynSense');
    const enabled = enabledLanguages();

    /**
     * A setting's value only when somebody actually set it, otherwise undefined.
     *
     * `get` cannot tell those apart — it answers with the contributed default, so a push would
     * carry a value the user never chose. For a setting that also lives in roslynsense.json that
     * is not harmless: the push would overwrite the file's value with the editor's default every
     * time any setting in the section changed.
     */
    const chosen = <T>(key: string): T | undefined => {
        const values = config.inspect<T>(key);
        return values?.workspaceFolderValue ?? values?.workspaceValue ?? values?.globalValue;
    };
    return {
        // Whether this connection may advertise the server's executeCommand ids. Exactly one
        // client per window may: vscode-languageclient turns every id the server advertises into
        // a VS Code command, and registering an id twice throws — which killed the second client
        // outright and reported itself as a missing server binary. Answered at initialize rather
        // than by suppressing the registration here, because the feature that does the
        // registering is built into the client and reads the capability directly.
        registerCommands,
        // This extension implements the picker command, so the server may collapse a Roslyn
        // action group — "Configure IDE0074 severity" and its five severities — into one
        // lightbulb entry rather than flattening it into five siblings. Unconditional: unlike
        // registerCommands this is a property of the client, not of which client came first,
        // and the command is registered once for the window whatever connections exist.
        nestedCodeActions: true,
        analyzerDiagnostics: config.get('analyzerDiagnostics'),
        codeStyleDiagnostics: config.get('codeStyleDiagnostics'),
        analyzerTimeoutSeconds: config.get('analyzerTimeoutSeconds'),
        workspaceDiagnostics: config.get('workspaceDiagnostics'),
        externalSource: config.get('externalSource'),
        sourceLink: config.get('sourceLink'),
        symbolServer: config.get('symbolServer'),
        referenceSource: config.get('referenceSource'),
        fileNesting: { rules: config.get('fileNesting.rules') },
        // Sent as a section for the same reason the debugger block is: a server that predates it
        // ignores one property rather than a name it has no meaning for.
        webforms: { codeLens: config.get('webforms.codeLens') },
        // Which System.Diagnostics debugger attributes the engines honour. Sent as a section so
        // an older server, which reads none of it, ignores one property instead of six.
        debugger: {
            debuggerDisplay: config.get('debugger.debuggerDisplay'),
            typeProxy: config.get('debugger.typeProxy'),
            browsable: config.get('debugger.browsable'),
            justMyCode: config.get('debugger.justMyCode'),
            rawView: config.get('debugger.rawView'),
            maxChildren: config.get('debugger.maxChildren'),
            symbolInclude: config.get('debugger.symbolInclude'),
            symbolExclude: config.get('debugger.symbolExclude'),
            // Only when set in the editor: this one also lives in roslynsense.json, and sending
            // the contributed default would reset a choice made there on every push.
            coreClrEngine: chosen<string>('debugger.coreClrEngine'),
        },
        // Which language packs this connection wants. Per connection on the server too: the
        // daemon is shared, so another window — or an AI session on the same daemon — keeps
        // whatever it asked for.
        languages: Object.fromEntries(
            EXTRA_LANGUAGES.map((language) => [language.id, enabled.includes(language)])
        ),
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
            if (event.affectsConfiguration('roslynSense.languages')) {
                void promptReloadForLanguages();
                return;
            }
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

/**
 * Watches the `roslynsense.json` layers for the one thing in them the editor cannot apply live.
 *
 * The server reloads the file itself and applies most of it to a running host. What it cannot
 * reach from there is this window's document selector, which was fixed when the client was
 * constructed — so a name added to `webConfig.additionalFiles` leaves the file opening as plain
 * XML until the window reconnects. Everything else about the edit has already taken effect by the
 * time this asks.
 */
function registerConfigFileWatch(context: vscode.ExtensionContext): void {
    let current = additionalConfigPatterns().join('|');

    const onChanged = () => {
        const next = additionalConfigPatterns().join('|');
        if (next === current) {
            return;
        }
        current = next;
        void promptReloadForConfigFiles();
    };

    // Two watchers, because two of the four layers live outside the workspace entirely: a glob
    // with no base only ever matches inside the open folders, so a global or personal file — the
    // ones the settings page writes for the "Global" and "Personal" tabs — would change without
    // this window ever hearing about it.
    for (const pattern of [
        '**/roslynsense*.json',
        homePattern(),
    ]) {
        if (!pattern) {
            continue;
        }

        const watcher = vscode.workspace.createFileSystemWatcher(pattern);
        watcher.onDidChange(onChanged, undefined, context.subscriptions);
        watcher.onDidCreate(onChanged, undefined, context.subscriptions);
        watcher.onDidDelete(onChanged, undefined, context.subscriptions);
        context.subscriptions.push(watcher);
    }
}

/** Both home layers under one recursive pattern, or undefined when there is no home directory. */
function homePattern(): vscode.RelativePattern | undefined {
    try {
        const home = homeDirectory();
        return home
            ? new vscode.RelativePattern(vscode.Uri.file(home), '**/roslynsense*.json')
            : undefined;
    } catch {
        return undefined;
    }
}

async function promptReloadForConfigFiles(): Promise<void> {
    const reload = 'Reload Window';
    const choice = await vscode.window.showInformationMessage(
        'RoslynSense: the configuration files it treats as web.config changed. Reload the window to apply it.',
        reload
    );
    if (choice === reload) {
        await vscode.commands.executeCommand('workbench.action.reloadWindow');
    }
}

/**
 * Turning a language on or off takes a new connection, so offer one.
 *
 * Not a `didChangeConfiguration` like every other setting: the document selector is fixed when
 * the client is constructed, and the server advertised this connection's capabilities — the
 * markup trigger characters, its commands, the semantic-token legend — at initialize, where
 * they cannot be withdrawn afterwards.
 */
async function promptReloadForLanguages(): Promise<void> {
    const reload = 'Reload Window';
    const choice = await vscode.window.showInformationMessage(
        'RoslynSense language support changed. Reload the window to apply it.',
        reload
    );
    if (choice === reload) {
        await vscode.commands.executeCommand('workbench.action.reloadWindow');
    }
}

/**
 * Which server binary to launch.
 *
 * An explicit `roslynSense.serverPath` wins. Otherwise `ROSLYNSENSE_SERVER` is honoured, which
 * is the same variable the MCP entry point uses to redirect to a development build: without it,
 * opening any folder that has no workspace setting silently falls back to the installed
 * `roslyn-sense` on PATH, and a change you just built appears not to have happened.
 */
function resolveServerPath(config: vscode.WorkspaceConfiguration): string {
    // inspect() rather than get(): get() returns the manifest's default when nothing is set,
    // so it cannot tell "the user chose roslyn-sense" from "nobody chose anything" — and the
    // environment variable would never get a look in.
    const setting = config.inspect<string>('serverPath');
    const chosen =
        setting?.workspaceFolderValue ?? setting?.workspaceValue ?? setting?.globalValue;
    if (chosen?.trim()) {
        return chosen.trim();
    }

    return process.env.ROSLYNSENSE_SERVER?.trim() || 'roslyn-sense';
}

let serverStderrChannel: vscode.OutputChannel | undefined;

/**
 * Spawns the server ourselves so the bytes the client's reader actually receives can be written
 * down verbatim.
 *
 * The server records its own copy of what it sent. If the two files differ, the stream was
 * damaged between the two processes; if they are identical, the server is exonerated and the
 * reader is what mis-framed them. Nothing else distinguishes those two cases, and they lead to
 * opposite fixes. Only used while `roslynSense.traceProtocolStream` is on.
 */
function capturingServerOptions(
    command: string,
    args: string[],
    cwd: string | undefined,
    env: NodeJS.ProcessEnv
): ServerOptions {
    return () =>
        new Promise<StreamInfo>((resolve, reject) => {
            const child = cp.spawn(command, args, { cwd, env });
            child.once('error', reject);

            const dir = path.join(os.tmpdir(), 'roslyn-mcp-lsp-diagnostics');
            let capture: fs.WriteStream | undefined;
            try {
                fs.mkdirSync(dir, { recursive: true });
                capture = fs.createWriteStream(path.join(dir, `client-in-${child.pid ?? 'unknown'}.bin`));
                // A writable that emits 'error' with nobody listening throws in the extension
                // host: without this, filling the temp disk mid-session would crash the very
                // thing the capture exists to diagnose.
                capture.on('error', () => { capture = undefined; });
            } catch {
                // A capture that cannot be written must not stop the server from starting.
            }

            // pipe() rather than a write() per chunk, so a client that stops reading still applies
            // backpressure to the server instead of growing a buffer here. The tee listens
            // alongside it and does not affect that flow control.
            const reader = new PassThrough();
            child.stdout.pipe(reader);
            child.stdout.on('data', (chunk: Buffer) => capture?.write(chunk));
            child.stdout.on('end', () => capture?.end());

            // The client only drains stderr when it owns the spawn; left unread, the server
            // blocks on its next diagnostic write once the pipe buffer fills.
            serverStderrChannel ??= vscode.window.createOutputChannel('RoslynSense Server');
            child.stderr.on('data', (chunk: Buffer) => serverStderrChannel!.append(chunk.toString('utf8')));

            resolve({ reader, writer: child.stdin });
        });
}

async function startClient(
    context: vscode.ExtensionContext,
    binding?: { solutionPath?: string; folder?: vscode.WorkspaceFolder }
): Promise<void> {
    const config = vscode.workspace.getConfiguration('roslynSense');
    const serverPath = resolveServerPath(config);
    const solutionPath = binding ? binding.solutionPath : await pickSolution();
    activeSolutionPath = solutionPath;

    // Claimed before the client is built and released when it leaves the map, so the second
    // solution in a window connects without its server commands rather than failing to connect.
    const ownsCommands = clientsBySolution.size === 0;

    const args = ['--lsp'];
    if (solutionPath) {
        args.push('--solution', solutionPath);
    }

    // The working directory is how the server resolves the solution when none was named, so a
    // second root has to start its client in its own folder.
    const cwd = binding?.folder?.uri.fsPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;

    // The server reads this from its environment because it has to act before any configuration
    // has been exchanged: the corruption it looks for can happen on the initialize response.
    const tracing =
        config.get<boolean>('traceProtocolStream', false) || process.env.ROSLYNSENSE_LSP_TRACE === '1';
    const env = tracing ? { ...process.env, ROSLYNSENSE_LSP_TRACE: '1' } : undefined;

    const serverOptions: ServerOptions = env
        ? capturingServerOptions(serverPath, args, cwd, env)
        : {
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
            // Not gated on the markup language being on for this window: web.config is a
            // project file, and the tag prefixes it registers re-bind every control for the
            // other sessions sharing this daemon.
            '**/[wW]eb.config',
            ...additionalConfigPatterns(binding?.folder),
            ...enabledFileLanguages().flatMap(watchGlobs),
        ].map((pattern) => vscode.workspace.createFileSystemWatcher(pattern));
    }
    const clientKey = bindingKey(solutionPath, binding?.folder);

    const clientOptions: LanguageClientOptions = {
        // Source-generated documents are C# too. Without them here VS Code sends the server
        // nothing for a generated file, so it opens as an inert buffer — no hover, no
        // navigation, no diagnostics — which is worse than not offering to open it at all.
        // Narrowed to the client's own root when it has one. Two clients whose selectors both
        // match a file both answer every request for it, and VS Code merges the answers rather
        // than picking one — two identical code lenses on a line, two entries in a definition
        // picker. Only a client with no root of its own claims everything.
        documentSelector: [
            fileFilter('csharp', binding?.folder),
            // Not folder-scoped: a generated document has no path under any root, and only one
            // client can serve the scheme, so it belongs to whichever client came first.
            ...(ownsCommands ? [{ scheme: 'roslynsense-generated', language: 'csharp' }] : []),
            // The other languages the same server serves — WebForms markup, whose controls,
            // properties and event handlers are C# symbols, and whose <% %> blocks are C#.
            // A language switched off still highlights, it just answers nothing.
            ...enabledFileLanguages().flatMap((language) =>
                language.patterns?.length
                    ? (language.patternLanguages ?? []).flatMap((hostLanguage) =>
                          language.patterns!.map((pattern) =>
                              patternFilter(hostLanguage, pattern, binding?.folder),
                          ),
                      )
                    : [fileFilter(language.id, binding?.folder)],
            ),
            // Not gated on the `webconfig` pack, for the same reason the watcher below is not:
            // what these files also carry is binding redirects, which the server answers about
            // whether or not any pack is on — a lens above the file, the shipped version on
            // hover, and a fix on each stale redirect. Turning off `<appSettings>` reference
            // counts must not take those with it. A duplicate filter is harmless: a selector is
            // a match test, so a file matching twice is still one document.
            ...CONFIG_FILE_PATTERNS.map((pattern) => patternFilter('xml', pattern, binding?.folder)),
            // The names `webConfig.additionalFiles` added. Gated on the pack, unlike the two
            // above: what makes those unconditional is the binding redirects they carry, and a
            // framework's own configuration file is claimed for its settings or not at all.
            ...(enabledLanguages().some((language) => language.id === 'webconfig')
                ? additionalConfigPatterns(binding?.folder).map((pattern) =>
                      patternFilter('xml', pattern, binding?.folder),
                  )
                : []),
        ],
        uriConverters: { code2Protocol, protocol2Code },
        middleware: {
            // A collapsed group's ids live in this connection's resolve cache and nowhere else,
            // so the entry has to say which connection it came from. Stamped here rather than
            // built into the server's payload because the key is the editor's idea of a client,
            // which the server has no name for.
            provideCodeActions: async (document, range, context, token, next) => {
                const actions = await next(document, range, context, token);
                bindNestedCodeActions(actions, clientKey);
                return actions;
            },
            // A refreshed lens list kills the previous list's command keys while the editor is
            // still drawing the previous list's anchors, so an unresolved lens is clickable and
            // dead for as long as its resolve takes. Resolving the ones on screen before handing
            // the list over closes that window. See codeLensPrewarm.ts.
            provideCodeLenses: async (document, token, next) => {
                const lenses = await next(document, token);
                return lenses ? preResolveVisibleLenses(document, lenses, token) : lenses;
            },
        },
        // Sent at initialize so the very first analyzer pass already runs under the user's
        // settings; changes afterwards go through workspace/didChangeConfiguration.
        initializationOptions: serverSettings(ownsCommands),
        // Verbose tracing logs server→client requests and their responses, and one of those
        // responses is a NuGet feed password.
        traceOutputChannel: redactingTrace ??= createRedactingTraceChannel(),
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
    clientsBySolution.set(clientKey, client);
    wireEditorDebugCommandHandler(client);
    wireNuGetCredentials(client, context);
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
        'roslynSense.status', [{ language: 'csharp' }, { language: 'webforms' }]);
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
        // restart/pick-solution paths start clean instead of rejecting forever. Out of the map as
        // well as out of `client`: a dead entry left in it holds the executeCommand claim above,
        // so every later client in the window would connect without its commands.
        clientsBySolution.delete(bindingKey(solutionPath, binding?.folder));
        void client.dispose().then(undefined, () => undefined);
        client = undefined;
        statusItem.busy = false;
        statusItem.severity = vscode.LanguageStatusSeverity.Error;
        statusItem.text = 'RoslynSense: failed to start';

        // The install advice only fits a spawn failure. Printing it for every failure sends the
        // reader to check their PATH for a problem that is in the extension.
        void vscode.window.showErrorMessage(
            /ENOENT|spawn|not recognized|cannot find/i.test(String(err))
                ? `RoslynSense failed to start: ${err}. Install with: dotnet tool install -g ` +
                  `RoslynSense, or set roslynSense.serverPath.`
                : `RoslynSense failed to start: ${err}`
        );
    }
}

/**
 * A document filter for one language, confined to the client's own root when it has one.
 *
 * The protocol's own filter takes a glob string rather than a `RelativePattern`, so the root is
 * spelled into the glob — with forward slashes, which is what the matcher expects on every
 * platform.
 */
function fileFilter(language: string, folder: vscode.WorkspaceFolder | undefined) {
    return folder
        ? { scheme: 'file', language, pattern: `${folder.uri.fsPath.replace(/\\/g, '/')}/**/*` }
        : { scheme: 'file', language };
}

/**
 * A filter for files a host language owns by name shape — `appsettings*.json` under `json`. The
 * glob is rooted under the client's folder when it has one, the way {@link fileFilter}'s is.
 */
function patternFilter(language: string, glob: string, folder: vscode.WorkspaceFolder | undefined) {
    return folder
        ? {
              scheme: 'file',
              language,
              pattern: `${folder.uri.fsPath.replace(/\\/g, '/')}/${glob}`,
          }
        : { scheme: 'file', language, pattern: glob };
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

/**
 * How long the lens list waits for the pre-resolve before going out unresolved.
 *
 * The failure this guards is worse than the one it fixes: a resolve that blocks on the project
 * load gate would hold back every lens in the document, so a file that showed stale-but-visible
 * lenses would show none at all. Past the deadline the list goes out as it came, the editor
 * resolves it the usual way, and the requests already in flight are not wasted — the server
 * memoizes a resolve, so the editor's own round trip finds the answer waiting.
 */
const LENS_PRE_RESOLVE_TIMEOUT_MS = 400;

/**
 * Resolve the lenses on screen before the editor is handed the list they belong to.
 *
 * See codeLensPrewarm.ts for why this exists and what it costs. The one rule that matters here is
 * that nothing in it may reject: a middleware that throws loses every lens in the document, which
 * is a far louder bug than the one being fixed.
 */
async function preResolveVisibleLenses(
    document: vscode.TextDocument,
    lenses: vscode.CodeLens[],
    token: vscode.CancellationToken,
): Promise<vscode.CodeLens[]> {
    const active = client;

    if (!active) {
        return lenses;
    }

    const visible = vscode.window.visibleTextEditors
        .filter((editor) => editor.document === document)
        .flatMap((editor) => editor.visibleRanges);

    const chosen = lensesToPreResolve(lenses, visible);

    if (chosen.length === 0) {
        return lenses;
    }

    const resolving = Promise.allSettled(
        chosen.map(async (index) => {
            // asCodeLens carries `data` across only for the client's own ProtocolCodeLens, which is
            // what `next` returned; anything else round-trips as a bare range and comes back
            // uncommanded, which the merge below then declines to take.
            const sent = active.code2ProtocolConverter.asCodeLens(lenses[index]);
            const answer = (await active.sendRequest('codeLens/resolve', sent, token)) as typeof sent;

            return [index, active.protocol2CodeConverter.asCodeLens(answer)] as const;
        }),
    );

    let expire: ReturnType<typeof setTimeout> | undefined;

    const outcomes = await Promise.race([
        resolving,
        new Promise<undefined>((resolve) => {
            expire = setTimeout(() => resolve(undefined), LENS_PRE_RESOLVE_TIMEOUT_MS);
        }),
    ]);

    if (expire !== undefined) {
        clearTimeout(expire);
    }

    if (outcomes === undefined) {
        return lenses;
    }

    const merged = lenses.slice();

    for (const outcome of outcomes) {
        // A lens that came back without a command is no better than the one already in the list,
        // and swapping it in would only discard the `data` the editor still needs to resolve it.
        if (outcome.status === 'fulfilled' && outcome.value[1]?.command) {
            merged[outcome.value[0]] = outcome.value[1];
        }
    }

    return merged;
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
        // CodeLens "N external references": the reads live in compiled assemblies, so the
        // server decompiles them on demand and the peek opens on the decompiled source.
        vscode.commands.registerCommand(
            'roslynSense.showExternalConfigReads',
            async (uri: string, line: number, character: number) => {
                if (!client) {
                    return;
                }
                let locations: LspLocation[];
                try {
                    locations = await client.sendRequest<LspLocation[]>(
                        'roslynSense/externalConfigReads',
                        { textDocument: { uri }, line, character }
                    );
                } catch {
                    return;
                }
                void vscode.commands.executeCommand(
                    'roslynSense.showReferences',
                    uri,
                    line,
                    character,
                    locations ?? []
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
        }),
        // CodeLens "Refresh table" on a .dbml <Table>. The id differs from the three server
        // commands for the reason above, and the flow lives here rather than on the server
        // because both of its decisions — which connection, and whether to drop columns — are
        // questions only the user can answer.
        vscode.commands.registerCommand(
            'roslynSense.dbmlRefreshTable',
            (uri: string, tableName: string) => refreshDbmlTable(uri, tableName)
        ),
        // CodeLens "Add from database" on a .dbml <Database>: everything the database has that the
        // model does not, picked and written in one go. Same client-side reasoning as the refresh —
        // choosing a connection and choosing the objects are questions for the user.
        vscode.commands.registerCommand('roslynSense.dbmlAddFromDatabase', (uri: string) =>
            addFromDatabase(uri)
        ),
        // Value sets are read once and kept, so this is how a migration that added a row reaches
        // the editor. The id differs from the server command for the reason above.
        vscode.commands.registerCommand('roslynSense.reloadValueSets', () => reloadValueSets())
    );
}

interface ValueSetRefreshResult {
    ok: boolean;
    problem?: string;
    sets: string[];
}

/**
 * Re-reads every value set from its database.
 *
 * No question to ask first: the sets are declared in `roslynsense.json`, the queries are read-only,
 * and reloading all of them is both what someone wants after a migration and cheaper than making
 * them pick one. The server re-pulls diagnostics on its own once the values are back.
 */
async function reloadValueSets(): Promise<void> {
    if (!client) {
        return;
    }

    const result = await client.sendRequest<ValueSetRefreshResult>('workspace/executeCommand', {
        command: 'roslynSense.refreshValueSets',
        arguments: [],
    });

    if (!result.ok) {
        void vscode.window.showWarningMessage(
            `RoslynSense: ${result.problem ?? 'the value sets could not be reloaded.'}`
        );
        return;
    }

    void vscode.window.showInformationMessage(
        result.sets.length === 0
            ? 'RoslynSense: no value sets are configured.'
            : `RoslynSense: reloaded ${result.sets.length} value set${result.sets.length === 1 ? '' : 's'}.`
    );
}

interface DbmlConnection {
    alias: string;
    provider: string;
}

interface DbmlConnectionList {
    connections: DbmlConnection[];
    unsupported: string[];
}

interface DbmlPlannedColumn {
    name: string;
    detail: string;
}

interface DbmlRefreshPlan {
    ok: boolean;
    message: string;
    table?: string;
    added?: DbmlPlannedColumn[];
    updated?: DbmlPlannedColumn[];
    removed?: DbmlPlannedColumn[];
    associations?: DbmlPlannedColumn[];
    notes?: string[];
}

interface DbmlRefreshResult {
    ok: boolean;
    message: string;
}

/**
 * Re-syncs one <Table> in a .dbml against a registered RoslynSense database connection.
 *
 * Three steps and two of them are questions. The connection is asked for rather than read from
 * the model's own <Connection> element, which commonly names a machine that no longer exists.
 * The removals are confirmed modally and separately from the rest, because dropping a <Column>
 * deletes a property the solution may be full of references to — the database knowing the column
 * is gone does not mean the model is finished being edited.
 */
/**
 * The connection a schema operation should run against: the only one registered, or the one the
 * user picks. Undefined means there is nothing usable or the user dismissed the picker, and the
 * error has already been shown.
 */
async function pickDbmlConnection(title: string): Promise<string | undefined> {
    if (!client) {
        return undefined;
    }

    const list = await client.sendRequest<DbmlConnectionList>('workspace/executeCommand', {
        command: 'roslynSense.dbmlConnections',
        arguments: [],
    });

    const available = list?.connections ?? [];

    if (available.length === 0) {
        const unsupported = (list?.unsupported ?? []).join(', ');
        void vscode.window.showErrorMessage(
            unsupported
                ? `RoslynSense: no SQL Server connection is registered. ${unsupported} cannot describe a schema.`
                : 'RoslynSense: no database connection is registered. Add one with db_add_connection or --db.'
        );
        return undefined;
    }

    if (available.length === 1) {
        return available[0].alias;
    }

    const picked = await vscode.window.showQuickPick(
        available.map((c) => ({ label: c.alias, description: c.provider })),
        { title, placeHolder: 'Database connection' }
    );
    return picked?.label;
}

async function refreshDbmlTable(uri: string, tableName: string): Promise<void> {
    if (!client) {
        return;
    }

    const alias = await pickDbmlConnection(`Refresh ${tableName} from…`);
    if (!alias) {
        return;
    }

    const plan = await client.sendRequest<DbmlRefreshPlan>('workspace/executeCommand', {
        command: 'roslynSense.dbmlPlanRefresh',
        arguments: [uri, tableName, alias],
    });

    if (!plan?.ok) {
        void vscode.window.showErrorMessage(`RoslynSense: ${plan?.message ?? 'the refresh failed.'}`);
        return;
    }

    for (const note of plan.notes ?? []) {
        void vscode.window.showWarningMessage(`RoslynSense: ${note}`);
    }

    const removed = plan.removed ?? [];
    const changes =
        (plan.added?.length ?? 0) + (plan.updated?.length ?? 0) + removed.length + (plan.associations?.length ?? 0);

    if (changes === 0) {
        void vscode.window.showInformationMessage(`RoslynSense: ${plan.message}`);
        return;
    }

    let includeRemovals = false;

    if (removed.length > 0) {
        const names = removed.map((c) => c.name).join(', ');
        const answer = await vscode.window.showWarningMessage(
            `${tableName}: the database no longer has ${names}.`,
            { modal: true, detail: 'Removing them deletes the generated properties too.' },
            'Remove them',
            'Keep them'
        );
        if (!answer) {
            return;
        }
        includeRemovals = answer === 'Remove them';
    }

    const result = await client.sendRequest<DbmlRefreshResult>('workspace/executeCommand', {
        command: 'roslynSense.dbmlApplyRefresh',
        arguments: [uri, tableName, alias, includeRemovals],
    });

    if (result?.ok) {
        void vscode.window.showInformationMessage(`RoslynSense: ${result.message}`);
    } else {
        void vscode.window.showErrorMessage(`RoslynSense: ${result?.message ?? 'the refresh failed.'}`);
    }
}

interface DbmlAddableObject {
    name: string;
    kind: string;
}

interface DbmlAddableList {
    ok: boolean;
    message: string;
    objects?: DbmlAddableObject[];
}

interface DbmlAddResult {
    ok: boolean;
    message: string;
    notes?: string[];
}

/**
 * Adds tables, views and functions the database has and the model does not.
 *
 * Additions need no removal confirmation — nothing existing is touched — so the flow is the two
 * questions only the user can answer (which connection, which objects) and then one write. The
 * picker is grouped by kind so a database with three hundred procedures does not bury its tables.
 */
async function addFromDatabase(uri: string): Promise<void> {
    if (!client) {
        return;
    }

    const alias = await pickDbmlConnection('Add from database…');
    if (!alias) {
        return;
    }

    const list = await client.sendRequest<DbmlAddableList>('workspace/executeCommand', {
        command: 'roslynSense.dbmlAddable',
        arguments: [uri, alias],
    });

    if (!list?.ok) {
        void vscode.window.showErrorMessage(
            `RoslynSense: ${list?.message ?? 'the database could not be listed.'}`
        );
        return;
    }

    const objects = list.objects ?? [];

    if (objects.length === 0) {
        void vscode.window.showInformationMessage(`RoslynSense: ${list.message}`);
        return;
    }

    const items: vscode.QuickPickItem[] = [];
    let previousKind: string | undefined;

    // The server sends the list grouped by kind already; the separators just name the groups.
    for (const o of objects) {
        if (o.kind !== previousKind) {
            items.push({ label: `${o.kind}s`, kind: vscode.QuickPickItemKind.Separator });
            previousKind = o.kind;
        }
        items.push({ label: o.name, description: o.kind });
    }

    const picked = await vscode.window.showQuickPick(items, {
        title: 'Add from database',
        placeHolder: 'Tables, views and functions not yet in the model',
        canPickMany: true,
        matchOnDescription: true,
    });

    if (!picked || picked.length === 0) {
        return;
    }

    const result = await client.sendRequest<DbmlAddResult>('workspace/executeCommand', {
        command: 'roslynSense.dbmlApplyAdd',
        arguments: [uri, alias, picked.map((item) => item.label)],
    });

    for (const note of result?.notes ?? []) {
        void vscode.window.showWarningMessage(`RoslynSense: ${note}`);
    }

    if (result?.ok) {
        void vscode.window.showInformationMessage(`RoslynSense: ${result.message}`);
    } else {
        void vscode.window.showErrorMessage(`RoslynSense: ${result?.message ?? 'the add failed.'}`);
    }
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

            // The member's own name, not its line. A decoration carrying a hoverMessage becomes a
            // part of whatever hover opens inside its range, and the widget highlights the union of
            // every part — so a line-wide range lit up the whole line and stapled this marker's
            // "implements" link onto a hover about some other identifier that shared the line. The
            // gutter icon is drawn per line whatever the range is, so narrowing costs nothing.
            const line = Math.min(marker.line, editor.document.lineCount - 1);
            const anchor = editor.document.validatePosition(new vscode.Position(line, marker.character));
            const range = editor.document.getWordRangeAtPosition(anchor) ?? new vscode.Range(anchor, anchor);
            return { range, hoverMessage: hover };
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

/**
 * Attach sessions this window owns, by the pid they are attached to.
 *
 * Kept here rather than derived from vscode.debug on demand: the API exposes only the *active*
 * session, and an app being debugged in the background is exactly the case the process list has
 * to get right.
 */
const attachedSessions = new Map<number, vscode.DebugSession>();

/**
 * Apps this window launched (F5, or Run from the solution explorer), by pid.
 *
 * Two jobs: the daemon is told about them so chats can see the app the user has running, and
 * "restart" on such an entry goes back through the debug session that owns it rather than
 * killing a process VS Code still believes it is supervising.
 */
const editorLaunches = new Map<number, vscode.DebugSession>();
let refreshProcessStatus: () => void = () => {};

function registerEditorProcess(session: vscode.DebugSession, pid: unknown): void {
    if (!client || typeof pid !== 'number' || !Number.isInteger(pid) || pid <= 0) {
        return;
    }
    const projectPath: string | undefined = session.configuration.projectPath;
    if (!projectPath) {
        return;
    }
    editorLaunches.set(pid, session);
    client
        .sendRequest('roslynSense/registerProcess', {
            pid,
            projectPath,
            url: session.configuration.appUrl ?? null,
        })
        .then(() => refreshProcessStatus(), () => {
            // Advisory: the app runs either way, so a daemon that is restarting is not an error
            // worth showing.
        });
}

/**
 * Sends one debug-console line to the daemon, tagged with the app's pid.
 *
 * Only the debuggee's own streams: adapter chatter ("console") is this window's business, and
 * mixing it into what a chat reads as the app's output invites the wrong diagnosis.
 */
function forwardProcessOutput(session: vscode.DebugSession, body: any): void {
    const category: string | undefined = body?.category;
    if (category !== 'stdout' && category !== 'stderr') {
        return;
    }
    const text: string | undefined = body?.output;
    if (!client || !text) {
        return;
    }
    for (const [pid, owner] of editorLaunches) {
        if (owner === session) {
            void client.sendNotification('roslynSense/processOutput', { pid, text });
            return;
        }
    }
}

function unregisterEditorProcess(session: vscode.DebugSession): void {
    for (const [pid, owner] of editorLaunches) {
        if (owner === session) {
            editorLaunches.delete(pid);
            void client?.sendRequest('roslynSense/unregisterProcess', { pid }).then(
                () => refreshProcessStatus(),
                () => {}
            );
        }
    }
}

function attachedPid(session: vscode.DebugSession): number | undefined {
    if (session.type !== DEBUG_TYPE || session.configuration.request !== 'attach') {
        return undefined;
    }
    const pid = Number(session.configuration.processId);
    return Number.isInteger(pid) && pid > 0 ? pid : undefined;
}

// Status bar counter for applications launched via the shared daemon's MCP chats
// (run_project). Click → list with attach / detach / kill / open-URL actions. Polls the server
// because launches happen in other processes (MCP chat clients), not this editor.
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
                const attached = processes.filter((p) => attachedSessions.has(p.pid)).length;
                processStatusItem.text = attached > 0
                    ? `$(rocket) ${processes.length} $(debug-alt) ${attached}`
                    : `$(rocket) ${processes.length}`;
                processStatusItem.tooltip =
                    'RoslynSense: running processes\n' +
                    processes
                        .map((p) => `${p.projectName} (pid ${p.pid})` +
                            (attachedSessions.has(p.pid) ? ' — debugger attached' : ''))
                        .join('\n');
                processStatusItem.show();
            }
        } catch {
            processStatusItem?.hide();
        }
    };
    processPollTimer = setInterval(() => void poll(), 5000);
    context.subscriptions.push({ dispose: () => clearInterval(processPollTimer) });
    refreshProcessStatus = () => void poll();
    void poll();

    // Attaching and detaching happen through the normal debug UI too (F5 on an attach
    // configuration, the stop button), so the map follows VS Code rather than only our commands.
    context.subscriptions.push(
        vscode.debug.onDidStartDebugSession((session) => {
            const pid = attachedPid(session);
            if (pid !== undefined) {
                attachedSessions.set(pid, session);
                void poll();
            }
        }),
        vscode.debug.onDidTerminateDebugSession((session) => {
            const pid = attachedPid(session);
            if (pid !== undefined && attachedSessions.get(pid) === session) {
                attachedSessions.delete(pid);
                void poll();
            }
            unregisterEditorProcess(session);
        })
    );

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
                    label: attachedSessions.has(p.pid)
                        ? `$(debug-alt) ${p.projectName}`
                        : `$(rocket) ${p.projectName}`,
                    description: `pid ${p.pid}${p.url ? ` — ${p.url}` : ''}` +
                        (attachedSessions.has(p.pid) ? ' — debugging' : ''),
                    detail: `${p.projectPath} — started ${new Date(p.startedAtUtc).toLocaleTimeString()}`,
                    process: p,
                })),
                { placeHolder: 'Running processes (launched by AI chats via RoslynSense)' }
            );
            if (!picked) {
                return;
            }

            const session = attachedSessions.get(picked.process.pid);
            // An app this window launched *under* the debugger already has one; a second
            // debugger cannot attach, so that entry offers neither action.
            const launchedDebugging =
                editorLaunches.get(picked.process.pid)?.configuration.noDebug !== true &&
                editorLaunches.has(picked.process.pid);
            const actions: {
                label: string;
                action: 'attach' | 'detach' | 'stop' | 'restart' | 'open';
            }[] = session
                ? [{ label: '$(debug-disconnect) Detach debugger', action: 'detach' }]
                : launchedDebugging
                    ? []
                    : [{ label: '$(debug-alt) Attach debugger', action: 'attach' }];
            if (picked.process.url) {
                actions.push({ label: '$(globe) Open URL', action: 'open' });
            }
            actions.push(
                { label: '$(debug-restart) Restart', action: 'restart' },
                { label: '$(debug-stop) Stop', action: 'stop' }
            );

            const action = await vscode.window.showQuickPick(actions, {
                placeHolder: `${picked.process.projectName} (pid ${picked.process.pid})`,
            });
            if (action?.action === 'restart') {
                await restartProcess(picked.process);
            } else if (action?.action === 'attach') {
                // projectPath is not used by the attach itself; it tells the adapter factory
                // whether this process needs the .NET Framework debugger instead of netcoredbg.
                const started = await vscode.debug.startDebugging(undefined, {
                    type: DEBUG_TYPE,
                    request: 'attach',
                    name: `C#: ${picked.process.projectName} (pid ${picked.process.pid})`,
                    processId: String(picked.process.pid),
                    projectPath: picked.process.projectPath,
                });
                if (!started) {
                    void vscode.window.showErrorMessage(
                        `RoslynSense: could not attach to ${picked.process.projectName} (pid ${picked.process.pid}).`
                    );
                }
            } else if (action?.action === 'detach' && session) {
                // Detach, not terminate: the app was launched by a chat and outlives the
                // debugger. stopDebugging disconnects without killing an attached debuggee.
                await vscode.debug.stopDebugging(session);
            } else if (action?.action === 'stop') {
                await stopProcess(picked.process);
            } else if (action?.action === 'open' && picked.process.url) {
                void vscode.env.openExternal(vscode.Uri.parse(picked.process.url));
            }
        })
    );
}

/**
 * Stops a running app, through whoever owns it.
 *
 * An app this window launched is stopped by ending its debug session: killing the pid behind
 * VS Code's back leaves the session hanging and the debug toolbar live. Everything else is a
 * chat's, and goes through the daemon — which also tells that chat why its app disappeared.
 */
async function stopProcess(process: RunningProcess): Promise<void> {
    const owned = editorLaunches.get(process.pid);
    if (owned) {
        await vscode.debug.stopDebugging(owned);
        return;
    }
    if (!client) {
        return;
    }
    const result = await client.sendRequest<string>('roslynSense/killProcess', {
        pid: process.pid,
    });
    void vscode.window.showInformationMessage(`RoslynSense: ${result}`);
}

/**
 * Stop, then start the same project again from this window.
 *
 * The restart is always an editor launch, even for a chat's app: this window cannot ask another
 * process to re-run its session. The chat is told its app was stopped (the daemon's kill does
 * that), and the new one is registered back, so it stays visible on both sides.
 */
async function restartProcess(process: RunningProcess): Promise<void> {
    const owned = editorLaunches.get(process.pid);
    const configuration = owned?.configuration;

    await stopProcess(process);

    // A web app that has not released its port yet fails the relaunch with a bind error, so the
    // restart waits for the process to actually be gone rather than assuming stop is synchronous.
    await waitForExit(process.pid);

    const started = await vscode.debug.startDebugging(
        undefined,
        configuration ?? {
            type: DEBUG_TYPE,
            request: 'launch',
            name: `C#: ${process.projectName}`,
            projectPath: process.projectPath,
        },
        { noDebug: configuration ? configuration.noDebug === true : true }
    );
    if (!started) {
        void vscode.window.showErrorMessage(
            `RoslynSense: could not restart ${process.projectName}.`
        );
    }
}

/** Polls the daemon until the pid leaves the registry, up to ~5s. */
async function waitForExit(pid: number): Promise<void> {
    for (let attempt = 0; attempt < 25; attempt++) {
        if (!client) {
            return;
        }
        try {
            const processes = await client.sendRequest<RunningProcess[]>(
                'roslynSense/runningProcesses');
            if (!processes.some((p) => p.pid === pid)) {
                return;
            }
        } catch {
            return;
        }
        await new Promise((resolve) => setTimeout(resolve, 200));
    }
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
    // Numbers the stops; 0 (or absent, from an older server) when the engine does not count.
    stopSequence?: number;
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

// Files the debugger can bind a breakpoint in. Markup belongs here because ASP.NET compiles a
// page into generated C# whose #line directives point back at the markup, so the document the
// PDB names — and the one the engine matches a breakpoint against — is the .aspx itself.
//
// Read per call and gated on the same setting as the document selector and the watchers: a
// language switched off must not have its breakpoints persisted into the shared store, where
// they would seed the next AI debug session for a pack this window never loaded.
function breakpointExtensions(): readonly string[] {
    return [
        '.cs',
        ...enabledLanguages()
            .filter((language) => language.breakpoints)
            .flatMap((language) => language.extensions),
    ];
}

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

// Mirrors the editor's full breakpoint set to the server's per-solution store, so breakpoint
// edits made with NO AI session running still shape the next session the chat starts
// (DebugStartTool folds the store into its initial breakpoints).
function sendBreakpointSnapshot(): void {
    if (!client) {
        return;
    }
    const solutionPath =
        activeSolutionPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!solutionPath) {
        return;
    }
    const extensions = breakpointExtensions();
    const breakpoints = vscode.debug.breakpoints
        .filter((bp): bp is vscode.SourceBreakpoint =>
            bp instanceof vscode.SourceBreakpoint &&
            bp.location.uri.scheme === 'file' &&
            extensions.some(
                (ext) => bp.location.uri.fsPath.toLowerCase().endsWith(ext)))
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
    /** Set when the file was resolved from external source: 'embedded', 'source link',
     *  'reference source' or 'decompiled'. */
    sourceOrigin?: string;
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
    // The last stop announced to VSCode, by the server's stop number — the state string alone
    // cannot tell two stops on the same line apart, and a chat-issued step lands on a new stop
    // faster than the poll can see the running state in between.
    private lastStopSeq = 0;
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
                this.lastStopSeq = session.stopSequence ?? 0;
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
                                ? {
                                      name: f.filePath.split(/[\\/]/).pop(),
                                      path: f.filePath,
                                      origin: f.sourceOrigin || undefined,
                                  }
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
                    // The poll may have announced this same stop already — skip the duplicate,
                    // which would otherwise re-enter the state VSCode just left.
                    const stopSeq = session.stopSequence ?? 0;
                    const announced = stopSeq !== 0 &&
                        stopSeq === this.lastStopSeq && this.lastState === 'stopped';
                    this.lastState = 'stopped';
                    this.lastStopSeq = stopSeq;
                    if (announced) {
                        return;
                    }
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
                    // Claim the stop before announcing it, or the next poll re-announces it.
                    this.lastStopSeq = (await this.currentSession())?.stopSequence ?? this.lastStopSeq;
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
            // The stop number catches what the state string cannot: a chat-issued step lands
            // on a new stop faster than the poll can see the running state in between, and the
            // string reads "stopped" on both sides of it.
            const stopSeq = session.stopSequence ?? 0;
            const newStop = session.state === 'stopped' &&
                stopSeq !== 0 && stopSeq !== this.lastStopSeq;
            if (session.state !== this.lastState || newStop) {
                this.lastState = session.state;
                this.lastStopSeq = stopSeq;
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
                        // The one place the debuggee's PID is knowable for a launch: the adapter
                        // reports it once the process exists. Announcing it puts the user's own
                        // F5 in the same registry as a chat's run_project, so chats can see it.
                        if (message.event === 'process' &&
                            session.type === DEBUG_TYPE &&
                            session.configuration.request === 'launch') {
                            registerEditorProcess(session, message.body?.systemProcessId);
                        }
                        // The app's own console. It exists only in this window otherwise, so a
                        // chat asked what the app printed has nothing to read.
                        if (message.event === 'output' && session.type === DEBUG_TYPE) {
                            forwardProcessOutput(session, message.body);
                        }
                        // How it ended, in the same log: the registry keeps only live processes,
                        // so this is the one place an exit code survives the exit.
                        if (message.event === 'exited' && session.type === DEBUG_TYPE) {
                            forwardProcessOutput(session, {
                                category: 'stdout',
                                output: `\n[roslyn-sense] the process exited with code ${message.body?.exitCode ?? '?'}.\n`,
                            });
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
    registerConfigFileWatch(context);
    registerLensCommands(context);
    registerOnAutoInsert(context);
    registerProcessStatusBar(context);
    registerInheritanceMarkers(context);
    registerDebugBridge(context);
    registerAiDebugAdapter(context);
    registerDebugLaunch(context, () => client);
    registerTestController(context, () => client);
    registerCoverageMapProgress(context, () => client);
    registerProjectSet(context, () => client);
    registerSolutionReady(context, () => client);
    registerImpactedTests(context, () => client);
    registerCoverageExplorer(context, () => client);
    registerChangedMembers(context, () => client);
    registerSolutionExplorer(context, () => client);
    registerVirtualDocuments(context, () => client);
    registerEmbeddedLanguages(context);
    registerSearchEverywhere(context, () => client);
    registerSettingsPanel(context, () => client);
    registerNuGetPanel(context, () => client);
    registerTaskProvider(context, () => client);
    registerHotReload(context, () => client);
    // Falls back to the most recent client for a group that predates the middleware's stamp —
    // and in the single-client window that is every window, they are the same object anyway.
    registerNestedCodeActions(context, (key) =>
        (key !== undefined ? clientsBySolution.get(key) : undefined) ?? client);
    registerEditorContext(
        context,
        () => client,
        () => activeSolutionPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath
    );

    // Bound to the first root rather than started loose. bindingKey() is what decides whether a
    // client already exists for a document's folder, and a loose client is keyed on whatever
    // pickSolution() happened to return — so when that disagreed with solutionForFolder() the
    // first file opened started a *second* client over the same files. Both then answered every
    // request, which is two of every code lens, two definitions, two of everything.
    const firstRoot = vscode.workspace.workspaceFolders?.[0];
    await startClient(
        context,
        firstRoot
            ? { solutionPath: await solutionForFolder(firstRoot, true), folder: firstRoot }
            : undefined
    );

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
