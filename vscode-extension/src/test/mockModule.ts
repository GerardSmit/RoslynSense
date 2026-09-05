import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname } from 'node:path';
import { runInNewContext } from 'node:vm';

/** Exercise the compiled extension-host modules without launching an Electron instance. */
export function loadWithMocks<T>(filename: string, mocks: Record<string, unknown>): T {
    const localRequire = createRequire(filename);
    const loaded = { exports: {} };
    const wrapper = runInNewContext(
        `(function(require, module, exports, __filename, __dirname) {\n${readFileSync(filename, 'utf8')}\n})`,
        { setTimeout, clearTimeout, console }, { filename });
    wrapper((id: string) => Object.prototype.hasOwnProperty.call(mocks, id)
        ? mocks[id] : localRequire(id), loaded, loaded.exports, filename, dirname(filename));
    return loaded.exports as T;
}

export function deferred<T>() {
    let resolve!: (value: T) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}
