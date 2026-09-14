import * as fs from 'fs';
import * as path from 'path';

/**
 * How long to wait before restarting a server that has just exited, given how many times it has
 * exited within the recent window, this exit included. The first exit restarts at once — a daemon
 * stopped for an update, a one-off crash — and each further one waits twice as long, up to half
 * a minute.
 *
 * Restarting immediately every time is what made a reinstall of the tool so expensive: the shim
 * was back before its store was, so every restart spawned a proxy against a half-replaced
 * install, which died, which restarted — until the crash budget was gone or five cold daemons
 * had been started in a minute.
 */
export function restartDelayMs(recentExits: number): number {
    if (recentExits <= 1) {
        return 0;
    }
    return Math.min(30_000, 1_000 * 2 ** (recentExits - 1));
}

export interface WaitOptions {
    /** The longest the binary is waited for once the delay has passed. */
    readonly limitMs?: number;
    readonly exists?: (file: string) => boolean;
    readonly sleep?: (ms: number) => Promise<void>;
}

const defaultSleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Resolves once `delayMs` has passed and the server binary is on disk — or once `limitMs` more
 * has passed without it, so a removed tool still surfaces as a failed start rather than silence.
 * A bare command name is left to PATH; only an absolute path is waited for.
 */
export async function waitToRestart(
    serverPath: string,
    delayMs: number,
    options: WaitOptions = {}
): Promise<void> {
    const sleep = options.sleep ?? defaultSleep;
    const exists = options.exists ?? fs.existsSync;
    const limitMs = options.limitMs ?? 120_000;

    if (delayMs > 0) {
        await sleep(delayMs);
    }
    if (!path.isAbsolute(serverPath)) {
        return;
    }

    let waited = 0;
    while (!exists(serverPath) && waited < limitMs) {
        await sleep(2_000);
        waited += 2_000;
    }
}
