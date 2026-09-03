/**
 * What the Solution Explorer looked like the last time each solution was open: which rows were
 * expanded, and which one was selected.
 *
 * VS Code keeps expansion for the lifetime of a window and no longer — a reload, or switching the
 * workspace to another solution and back, opens the tree fully collapsed. These two classes are
 * the state behind remembering it ourselves: `TreeMemory` follows one solution's tree while it is
 * on screen, `SolutionMemories` files a snapshot per solution in `workspaceState`. Both are pure
 * bookkeeping so they can be tested without the `vscode` API; the view wiring — the tree events
 * feeding `TreeMemory`, and the reveal walk that replays it — lives in solutionExplorer.ts.
 */

export interface ExplorerMemory {
    expanded: string[];
    selected: string | null;
}

/**
 * How many expanded rows one solution may remember, least recently expanded dropped first.
 *
 * Collapsing a row deliberately does not forget what was expanded inside it — re-expanding it
 * should look the way it was left, which is also what VS Code does within a session. The price is
 * that the set only ever grows while the user works, so it needs a ceiling somewhere; hundreds of
 * *deliberately opened* folders is well past anything a person keeps track of.
 */
const MAX_EXPANDED = 500;

/** How many solutions a workspace remembers, least recently shown dropped first. */
const MAX_SOLUTIONS = 20;

/** The expanded rows and selection of the solution currently on screen. */
export class TreeMemory {
    private readonly expanded = new Set<string>();
    private chosen: string | null = null;

    static from(memory: ExplorerMemory | undefined): TreeMemory {
        const tree = new TreeMemory();
        memory?.expanded.forEach((id) => tree.expanded.add(id));
        tree.chosen = memory?.selected ?? null;
        return tree;
    }

    get selected(): string | null {
        return this.chosen;
    }

    expand(id: string): void {
        // Re-adding moves the row to the end, so the cap in `snapshot` drops the row expanded
        // longest ago rather than whichever happened to be recorded first.
        this.expanded.delete(id);
        this.expanded.add(id);
    }

    collapse(id: string): void {
        this.expanded.delete(id);
    }

    isExpanded(id: string): boolean {
        return this.expanded.has(id);
    }

    select(id: string): void {
        this.chosen = id;
    }

    /**
     * Forgets everything under a row.
     *
     * For "collapse descendants", which VS Code only allows by refreshing the branch — and a
     * refresh fires no collapse events, so without this the memory would quietly re-expand a
     * subtree the user just asked to fold away.
     */
    forgetDescendants(ancestorId: string, parentOf: (id: string) => string | undefined): void {
        for (const id of [...this.expanded]) {
            if (this.isUnder(id, ancestorId, parentOf)) {
                this.expanded.delete(id);
            }
        }
    }

    private isUnder(
        id: string,
        ancestorId: string,
        parentOf: (id: string) => string | undefined
    ): boolean {
        // Bounded, because the parent map this walks is assembled from listings and nothing
        // guarantees it acyclic — and an infinite loop here would take the whole view with it.
        let current = parentOf(id);
        for (let steps = 0; current && steps < 100; steps++) {
            if (current === ancestorId) {
                return true;
            }
            current = parentOf(current);
        }
        return false;
    }

    snapshot(): ExplorerMemory {
        return { expanded: [...this.expanded].slice(-MAX_EXPANDED), selected: this.chosen };
    }
}

/** One remembered tree per solution, in the shape `workspaceState` stores. */
export class SolutionMemories {
    private readonly bySolution: Map<string, ExplorerMemory>;

    constructor(stored: Record<string, ExplorerMemory> | undefined) {
        this.bySolution = new Map(Object.entries(stored ?? {}));
    }

    recall(solution: string): ExplorerMemory | undefined {
        return this.bySolution.get(solution);
    }

    /** Files a snapshot and returns the whole record, ready to persist. */
    remember(solution: string, memory: ExplorerMemory): Record<string, ExplorerMemory> {
        // Deleting first makes insertion order mean "least recently shown first", which is what
        // the cap below trims by. A solution whose tree is fully folded up with nothing selected
        // needs no entry at all — remembering it and remembering nothing look identical.
        this.bySolution.delete(solution);
        if (memory.expanded.length > 0 || memory.selected) {
            this.bySolution.set(solution, memory);
        }
        while (this.bySolution.size > MAX_SOLUTIONS) {
            this.bySolution.delete(this.bySolution.keys().next().value as string);
        }
        return Object.fromEntries(this.bySolution);
    }
}
