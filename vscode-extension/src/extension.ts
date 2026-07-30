import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const config = vscode.workspace.getConfiguration('roslynSense');
    const serverPath = config.get<string>('serverPath', 'roslyn-sense');
    const solutionPath = config.get<string>('solutionPath', '');

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
        synchronize: {
            fileEvents: vscode.workspace.createFileSystemWatcher('**/*.cs'),
        },
    };

    client = new LanguageClient(
        'roslynSense',
        'RoslynSense',
        serverOptions,
        clientOptions
    );

    try {
        await client.start();
    } catch (err) {
        void vscode.window.showErrorMessage(
            `RoslynSense failed to start: ${err}. Install with: dotnet tool install -g roslyn-sense, ` +
            `or set roslynSense.serverPath.`
        );
    }
}

export async function deactivate(): Promise<void> {
    if (client) {
        await client.stop();
        client = undefined;
    }
}
