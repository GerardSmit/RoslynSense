import * as assert from 'assert';
import { describe, it } from 'node:test';

import { SolutionMemories, TreeMemory } from '../explorerMemory';

/**
 * The bookkeeping behind remembering the Solution Explorer's shape per solution.
 *
 * Plain `node --test`: nothing here touches the `vscode` API. The view wiring these classes sit
 * behind is in solutionExplorer.ts; what is tested here is the part that decides what a solution
 * remembers — and, just as deliberately, what it forgets.
 */
describe('TreeMemory', () => {
    it('remembers what was expanded and what was selected', () => {
        const memory = new TreeMemory();
        memory.expand('project:C:\\App\\App.csproj');
        memory.expand('folder:C:\\App\\App.csproj|C:\\App\\Services');
        memory.select('file:C:\\App\\Services\\Mail.cs');

        assert.deepStrictEqual(memory.snapshot(), {
            expanded: [
                'project:C:\\App\\App.csproj',
                'folder:C:\\App\\App.csproj|C:\\App\\Services',
            ],
            selected: 'file:C:\\App\\Services\\Mail.cs',
        });
    });

    /**
     * Collapsing a row keeps what was expanded inside it, which is what VS Code itself does
     * within a session: re-expanding the row shows it the way it was left.
     */
    it('a collapsed row is forgotten, what was inside it is not', () => {
        const memory = new TreeMemory();
        memory.expand('project:P');
        memory.expand('folder:P|F');
        memory.collapse('project:P');

        assert.strictEqual(memory.isExpanded('project:P'), false);
        assert.strictEqual(memory.isExpanded('folder:P|F'), true);
    });

    it('round-trips through the stored shape', () => {
        const memory = new TreeMemory();
        memory.expand('project:P');
        memory.select('file:F');

        const revived = TreeMemory.from(memory.snapshot());
        assert.strictEqual(revived.isExpanded('project:P'), true);
        assert.strictEqual(revived.selected, 'file:F');
    });

    it('starts empty from an undefined store', () => {
        const revived = TreeMemory.from(undefined);
        assert.deepStrictEqual(revived.snapshot(), { expanded: [], selected: null });
    });

    /**
     * "Collapse descendants" collapses by refreshing the branch, which fires no collapse events —
     * the command tells the memory by hand, through this. The row itself stays: only what is
     * under it was folded away.
     */
    it('forgets the descendants of a row, and only them', () => {
        const parents = new Map([
            ['folder:P|F', 'project:P'],
            ['folder:P|F/G', 'folder:P|F'],
            ['project:Q', 'solution:S'],
        ]);
        const memory = new TreeMemory();
        ['project:P', 'folder:P|F', 'folder:P|F/G', 'project:Q'].forEach((id) => memory.expand(id));

        memory.forgetDescendants('project:P', (id) => parents.get(id));

        assert.deepStrictEqual(memory.snapshot().expanded, ['project:P', 'project:Q']);
    });

    it('survives a cycle in the parent record', () => {
        const parents = new Map([
            ['a', 'b'],
            ['b', 'a'],
        ]);
        const memory = new TreeMemory();
        memory.expand('a');

        memory.forgetDescendants('c', (id) => parents.get(id));

        assert.strictEqual(memory.isExpanded('a'), true);
    });

    /** The cap drops the row expanded longest ago, not an arbitrary one. */
    it('re-expanding a row moves it away from the chopping block', () => {
        const memory = new TreeMemory();
        for (let i = 0; i < 500; i++) {
            memory.expand(`folder:P|${i}`);
        }
        memory.expand('folder:P|0');
        memory.expand('folder:P|overflow');

        const { expanded } = memory.snapshot();
        assert.strictEqual(expanded.length, 500);
        assert.strictEqual(expanded.includes('folder:P|0'), true);
        assert.strictEqual(expanded.includes('folder:P|1'), false);
    });
});

describe('SolutionMemories', () => {
    it('files one memory per solution and recalls it', () => {
        const memories = new SolutionMemories(undefined);
        memories.remember('C:\\A\\A.sln', { expanded: ['project:P'], selected: null });
        const stored = memories.remember('C:\\B\\B.sln', { expanded: [], selected: 'file:F' });

        assert.deepStrictEqual(stored, {
            'C:\\A\\A.sln': { expanded: ['project:P'], selected: null },
            'C:\\B\\B.sln': { expanded: [], selected: 'file:F' },
        });
        assert.deepStrictEqual(new SolutionMemories(stored).recall('C:\\A\\A.sln'), {
            expanded: ['project:P'],
            selected: null,
        });
    });

    /** A fully folded tree with nothing selected and remembering nothing look identical. */
    it('drops a solution whose memory says nothing', () => {
        const memories = new SolutionMemories({
            'C:\\A\\A.sln': { expanded: ['project:P'], selected: null },
        });
        const stored = memories.remember('C:\\A\\A.sln', { expanded: [], selected: null });

        assert.deepStrictEqual(stored, {});
    });

    it('forgets the least recently shown solution beyond the cap', () => {
        const memories = new SolutionMemories(undefined);
        for (let i = 0; i < 20; i++) {
            memories.remember(`C:\\${i}.sln`, { expanded: ['project:P'], selected: null });
        }
        // Showing the oldest again renews it, so the next overflow claims 1.sln instead.
        memories.remember('C:\\0.sln', { expanded: ['project:P'], selected: null });
        const stored = memories.remember('C:\\20.sln', { expanded: ['project:P'], selected: null });

        assert.strictEqual(Object.keys(stored).length, 20);
        assert.strictEqual('C:\\0.sln' in stored, true);
        assert.strictEqual('C:\\1.sln' in stored, false);
    });
});
