import * as assert from 'assert';
import { describe, it } from 'node:test';

import { restartDelayMs, waitToRestart } from '../serverRestart';

const serverPath = process.platform === 'win32'
    ? 'C:\\Users\\test\\.dotnet\\tools\\roslyn-sense.exe'
    : '/home/test/.dotnet/tools/roslyn-sense';

describe('server restart backoff', () => {
    it('restarts the first exit at once and backs off after that', () => {
        assert.strictEqual(restartDelayMs(1), 0);
        assert.strictEqual(restartDelayMs(2), 2_000);
        assert.strictEqual(restartDelayMs(3), 4_000);
        assert.strictEqual(restartDelayMs(4), 8_000);
        assert.strictEqual(restartDelayMs(6), 30_000);
        assert.strictEqual(restartDelayMs(20), 30_000);
    });

    it('waits the delay and no longer when the binary is there', async () => {
        const slept: number[] = [];
        await waitToRestart(serverPath, 4_000, {
            exists: () => true,
            sleep: async (ms) => { slept.push(ms); },
        });
        assert.deepStrictEqual(slept, [4_000]);
    });

    it('keeps waiting while the binary is being replaced', async () => {
        // What a tool reinstall looks like from here: the shim is gone for a while, then back.
        let polls = 0;
        const slept: number[] = [];
        await waitToRestart(serverPath, 0, {
            exists: () => ++polls > 3,
            sleep: async (ms) => { slept.push(ms); },
        });
        assert.strictEqual(polls, 4);
        assert.deepStrictEqual(slept, [2_000, 2_000, 2_000]);
    });

    it('gives up waiting for a binary that never returns', async () => {
        let polls = 0;
        await waitToRestart(serverPath, 0, {
            exists: () => { polls++; return false; },
            sleep: async () => {},
            limitMs: 6_000,
        });
        assert.strictEqual(polls, 4);
    });

    it('leaves a bare command name to PATH', async () => {
        let polls = 0;
        await waitToRestart('roslyn-sense', 0, {
            exists: () => { polls++; return false; },
            sleep: async () => {},
        });
        assert.strictEqual(polls, 0);
    });
});
