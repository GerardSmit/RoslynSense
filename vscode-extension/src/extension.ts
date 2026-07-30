import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
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
        '**/*.{sln,slnx}', '**/node_modules/**', 25);
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
        await config.update('solutionPath', picked.fsPath, vscode.ConfigurationTarget.Workspace);
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

    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'csharp' }],
        uriConverters: { code2Protocol, protocol2Code },
        // No client-side file watcher: the server checks file freshness itself on every
        // request (mtime-based), so didChangeWatchedFiles traffic would be redundant.
    };

    client = new LanguageClient('roslynSense', 'RoslynSense', serverOptions, clientOptions);

    statusItem ??= vscode.languages.createLanguageStatusItem(
        'roslynSense.status', { language: 'csharp' });
    statusItem.name = 'RoslynSense';
    statusItem.busy = true;
    statusItem.text = 'RoslynSense: starting';
    statusItem.command = { title: 'Pick Solution', command: 'roslynSense.openSolution' };

    try {
        await client.start();
        statusItem.busy = false;
        statusItem.text = solutionPath
            ? `RoslynSense: ${vscode.workspace.asRelativePath(solutionPath)}`
            : 'RoslynSense: running';
    } catch (err) {
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
    if (client) {
        await client.stop();
        client = undefined;
    }
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

    await startClient(context);
}

export async function deactivate(): Promise<void> {
    statusItem?.dispose();
    statusItem = undefined;
    await stopClient();
}
