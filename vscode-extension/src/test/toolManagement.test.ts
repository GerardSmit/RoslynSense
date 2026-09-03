import * as assert from 'assert';
import { describe, it } from 'node:test';

import {
    globalToolPath,
    installGlobalTool,
    ProcessResult,
    ToolCommandError,
    updateGlobalTool,
} from '../toolManagement';

const success: ProcessResult = { exitCode: 0, stdout: 'ok', stderr: '' };

describe('global tool management', () => {
    it('resolves the global tool shim on Windows and Unix', () => {
        assert.strictEqual(globalToolPath('C:\\Users\\test', 'win32'),
            'C:\\Users\\test\\.dotnet\\tools\\roslyn-sense.exe');
        assert.strictEqual(globalToolPath('/home/test', 'linux'),
            '/home/test/.dotnet/tools/roslyn-sense');
    });

    it('installs through dotnet without involving a shell', async () => {
        const calls: Array<[string, readonly string[]]> = [];
        await installGlobalTool(undefined, undefined, async (command, args) => {
            calls.push([command, args]);
            return success;
        });
        assert.deepStrictEqual(calls, [['dotnet', ['tool', 'install', '--global', 'RoslynSense']]]);
    });

    it('waits for daemon shutdown before updating', async () => {
        const calls: Array<[string, readonly string[]]> = [];
        await updateGlobalTool(undefined, undefined, async (command, args) => {
            calls.push([command, args]);
            return success;
        });
        assert.strictEqual(calls.length, 3);
        assert.strictEqual(calls[0][0], 'dotnet');
        assert.deepStrictEqual(calls[0][1].slice(0, 3), ['tool', 'install', '--tool-path']);
        assert.strictEqual(calls[0][1][4], 'RoslynSense');
        assert.match(calls[1][0], /roslyn-sense(?:\.exe)?$/);
        assert.deepStrictEqual(calls[1][1], ['--stop-daemons']);
        assert.deepStrictEqual(calls[2], ['dotnet', ['tool', 'update', '--global', 'RoslynSense']]);
    });

    it('does not update while daemon shutdown failed', async () => {
        let calls = 0;
        await assert.rejects(
            updateGlobalTool(undefined, undefined, async () => {
                calls++;
                return calls === 1 ? success : { exitCode: 7, stdout: '', stderr: 'still locked' };
            }),
            (error: unknown) => error instanceof ToolCommandError && /still locked/.test(error.message)
        );
        assert.strictEqual(calls, 2);
    });

    it('streams command output for progress logs', async () => {
        let output = '';
        await installGlobalTool((text) => output += text, undefined, async (_command, _args, write) => {
            write?.('installed\n');
            return success;
        });
        assert.match(output, /^> dotnet tool install --global RoslynSense\ninstalled\n$/);
    });
});
