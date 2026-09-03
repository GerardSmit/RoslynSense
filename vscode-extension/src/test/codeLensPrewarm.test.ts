import * as assert from 'assert';
import { describe, it } from 'node:test';

import { lensesToPreResolve } from '../codeLensPrewarm';

/**
 * Which lenses get resolved before a refreshed list reaches the editor.
 *
 * Plain `node --test`: nothing here touches the `vscode` API. Both directions of the rule are
 * failures nobody would see happening. Choosing too few leaves the bug this exists for — a lens
 * drawn on screen, clickable, wired to a key the editor has already dropped. Choosing too many
 * turns every scroll of a large file into a workspace-wide symbol search per lens, which the
 * server's deferred resolve exists specifically to avoid.
 */
describe('lensesToPreResolve', () => {
    const range = (start: number, end: number = start) => ({
        start: { line: start },
        end: { line: end },
    });

    const lens = (line: number, command?: unknown) => ({ range: range(line), command });

    it('takes the lenses the viewport is showing and leaves the rest', () => {
        const lenses = [lens(0), lens(12), lens(20), lens(400)];

        assert.deepStrictEqual(lensesToPreResolve(lenses, [range(10, 30)]), [1, 2]);
    });

    /** Both edges count: a lens on the first visible line is on screen, not next to it. */
    it('counts a lens on the first and last visible line', () => {
        const lenses = [lens(9), lens(10), lens(30), lens(31)];

        assert.deepStrictEqual(lensesToPreResolve(lenses, [range(10, 30)]), [1, 2]);
    });

    /** A split editor, or one document open in two groups, reports more than one range. */
    it('takes every range the document is visible through', () => {
        const lenses = [lens(5), lens(50), lens(200)];

        assert.deepStrictEqual(lensesToPreResolve(lenses, [range(0, 10), range(190, 210)]), [0, 2]);
    });

    /**
     * A commanded lens is already clickable, which is the entire objective — resolving it again
     * would pay for an answer it already has.
     */
    it('skips a lens that already carries its command', () => {
        const lenses = [lens(1, { command: 'roslynSense.showReferences' }), lens(2)];

        assert.deepStrictEqual(lensesToPreResolve(lenses, [range(0, 10)]), [1]);
    });

    /** The cap is a floor under the worst case: a tall editor over a generated file. */
    it('stops at the limit rather than resolving a screenful of a generated file', () => {
        const lenses = Array.from({ length: 500 }, (_, line) => lens(line));

        assert.deepStrictEqual(lensesToPreResolve(lenses, [range(0, 499)], 3), [0, 1, 2]);
    });

    /**
     * A document with no editor showing it — the editor asks for lenses of documents that are
     * merely open. Nothing is on screen, so nothing is urgent, and the deferred resolve is right.
     */
    it('takes nothing when the document is not visible anywhere', () => {
        assert.deepStrictEqual(lensesToPreResolve([lens(1), lens(2)], []), []);
    });
});
