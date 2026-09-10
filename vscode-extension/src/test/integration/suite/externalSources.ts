import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { externalSourceGlobs } from '../../../paths';

/** Exercise real VS Code provider dispatch for files outside the workspace, including TEMP aliases. */
export async function testExternalSources(root: vscode.WorkspaceFolder): Promise<void> {
    const temp = fs.realpathSync.native(os.tmpdir());
    const assembly = path.join(root.uri.fsPath, 'bin', 'Debug', 'net10.0', 'Fixture.dll');
    assert.ok(fs.existsSync(assembly), 'Build the integration fixture before running external-source tests.');
    const source = [
        'class ExternalReader {',
        '  private string text = "hello";',
        '  int Read() { var value = text; return value.Length; }',
        '}',
    ].join('\n');
    const selector = externalSourceGlobs(os.tmpdir()).map(pattern => ({ scheme: 'file', language: 'csharp', pattern }));
    for (const kind of ['ReferenceSource', 'Decompiled', 'SourceLink', 'EmbeddedSource']) {
        const cache = path.join(temp, 'RoslynMCP', kind);
        fs.mkdirSync(cache, { recursive: true });
        const directory = fs.mkdtempSync(path.join(cache, 'editor-integration-'));
        const file = path.join(directory, 'Decompiled.cs');
        fs.writeFileSync(file, source);
        fs.writeFileSync(kind === 'Decompiled'
            ? path.join(directory, 'RoslynMCP.decompiled.json') : file + '.roslynsense.json', JSON.stringify({
                AssemblyPath: assembly, SourceFilePath: file, TypeReflectionName: 'ExternalReader', Kind: kind,
            }));
        try {
            for (const spelling of new Set([file, path.join(os.tmpdir(), path.relative(temp, file))])) {
                const document = await vscode.workspace.openTextDocument(vscode.Uri.file(spelling));
                await vscode.window.showTextDocument(document);
                assert.ok(vscode.languages.match(selector, document) > 0, `${kind}: selector must match ${spelling}`);
                for (let visit = 0; visit < 2; visit++) {
                    const caret = document.positionAt(source.indexOf('text;', source.indexOf('int Read')));
                    const hover = await vscode.commands.executeCommand<vscode.Hover[]>(
                        'vscode.executeHoverProvider', document.uri, caret);
                    assert.ok(hover?.length, `${kind}: hover on private field, visit ${visit}`);
                    const definitions = await vscode.commands.executeCommand<(vscode.Location | vscode.LocationLink)[]>(
                        'vscode.executeDefinitionProvider', document.uri, caret);
                    assert.ok(definitions?.length, `${kind}: definition, visit ${visit}`);
                    const definition = definitions[0];
                    const targetUri = 'targetUri' in definition ? definition.targetUri : definition.uri;
                    const range = 'targetUri' in definition ? definition.targetSelectionRange ?? definition.targetRange : definition.range;
                    const target = await vscode.workspace.openTextDocument(targetUri);
                    assert.ok(target.lineAt(range.start.line).text.includes('private string text'), `${kind}: correct declaration`);
                    await vscode.window.showTextDocument(target);
                    const references = await vscode.commands.executeCommand<vscode.Location[]>(
                        'vscode.executeReferenceProvider', document.uri, document.positionAt(source.indexOf('value =')));
                    const usage = document.positionAt(source.lastIndexOf('value.Length'));
                    assert.ok(references?.some(r => r.range.contains(usage)), `${kind}: local usage reference`);
                    await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(
                        vscode.Uri.file(path.join(root.uri.fsPath, 'Greeter.cs'))));
                    await vscode.window.showTextDocument(document);
                }
            }
        } finally {
            // This directory was created by this test; no existing cache entries are removed.
            // Windows may keep a directory handle until the language server exits.
            try { fs.rmSync(directory, { recursive: true, force: true }); }
            catch (error) { console.warn(`External-source fixture cleanup deferred: ${error}`); }
        }
    }
}
