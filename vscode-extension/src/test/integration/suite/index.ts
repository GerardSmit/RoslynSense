import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';

async function eventually<T>(probe: () => Thenable<T | undefined>, description: string): Promise<T> {
    const deadline = Date.now() + 120_000;
    let lastError: unknown;
    while (Date.now() < deadline) {
        try {
            const value = await probe();
            if (value !== undefined) return value;
        } catch (error) {
            lastError = error;
        }
        await new Promise((resolve) => setTimeout(resolve, 500));
    }
    throw new Error(`Timed out waiting for ${description}.${lastError ? ` Last error: ${lastError}` : ''}`);
}

export async function run(): Promise<void> {
    const extension = vscode.extensions.getExtension('roslyn-sense.roslyn-sense');
    assert.ok(extension, 'The RoslynSense extension must be installed in the test host.');
    await extension.activate();
    assert.strictEqual(extension.isActive, true);

    const commands = await vscode.commands.getCommands(true);
    assert.ok(commands.includes('roslynSense.restartServer'));
    assert.ok(commands.includes('roslynSense.installServer'));
    assert.ok(commands.includes('roslynSense.updateServer'));

    const root = vscode.workspace.workspaceFolders?.[0];
    assert.ok(root, 'The integration fixture must be opened as a workspace.');
    const uri = vscode.Uri.file(path.join(root.uri.fsPath, 'Greeter.cs'));
    const document = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(document);
    console.log(`Integration document language: ${document.languageId}`);

    const symbols = await eventually(
        async () => {
            const value = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>(
                'vscode.executeDocumentSymbolProvider', uri
            );
            const contains = (items: readonly vscode.DocumentSymbol[]): boolean =>
                items.some((symbol) => symbol.name === 'Greeter' || contains(symbol.children));
            return value && contains(value) ? value : undefined;
        },
        'RoslynSense document symbols'
    );
    assert.ok(symbols.length > 0);

    const hover = await eventually(
        async () => {
            const value = await vscode.commands.executeCommand<vscode.Hover[]>(
                'vscode.executeHoverProvider', uri, new vscode.Position(2, 21)
            );
            return value?.length ? value : undefined;
        },
        'RoslynSense hover'
    );
    assert.ok(hover.length > 0);
}
