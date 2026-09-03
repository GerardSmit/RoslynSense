/// <reference path="./list.ts" />

/**
 * Where you have been in the panel.
 *
 * Dependencies are links now, and a link you cannot come back from is a trap: clicking one from a
 * package you were reading loses the package. The stack covers tab switches as well as selections,
 * because "back" after clicking through three dependencies from the Updates tab has to end up at
 * the Updates tab.
 *
 * Entries are (tab, package) pairs rather than DOM snapshots. Restoring one re-issues whatever the
 * tab normally issues and then selects the package — a list that has changed underneath since is
 * shown as it is now, which is the honest answer for a panel whose whole subject is remote state.
 *
 * There are no on-screen buttons. The header has four controls competing for a narrow row already,
 * and back is a gesture people bring with them: Alt+← / Alt+→ and the mouse's side buttons are what
 * they reach for first, and the only thing a pair of chevrons would have added is width.
 */
namespace NG {
    export interface NavEntry {
        tab: NuGetMsg.Tab;
        packageId: string | null;
    }

    /** Deep enough for any real click-through, shallow enough to stay a list and not a log. */
    const MaxEntries = 50;

    const entries: NavEntry[] = [];
    let index = -1;

    /** Set while a Back/Forward is being applied, so restoring an entry does not record one. */
    let moving = false;

    export function pushNav(entry: NavEntry): void {
        if (moving) {
            return;
        }

        const current = entries[index];
        if (current && current.tab === entry.tab && current.packageId === entry.packageId) {
            return;
        }

        // Selecting a package right after landing on a tab replaces the bare tab entry rather than
        // adding to it. Otherwise every click-through would need two Backs, the first of which
        // only deselects.
        if (current && current.tab === entry.tab && current.packageId === null) {
            entries[index] = entry;
                return;
        }

        entries.splice(index + 1);
        entries.push(entry);

        if (entries.length > MaxEntries) {
            entries.shift();
        }
        index = entries.length - 1;
    }

    export function navigate(delta: number): void {
        const target = index + delta;
        if (target < 0 || target >= entries.length) {
            return;
        }

        index = target;
        const entry = entries[index];
        moving = true;
        try {
            if (entry.tab !== state.tab) {
                // The list has to be rebuilt first; pendingSelect picks the package up once the
                // rows land, and falls back to a virtual row when the tab does not contain it.
                state.pendingSelect = entry.packageId;
                switchTab(entry.tab);
            } else if (entry.packageId) {
                openPackage(entry.packageId);
            } else {
                clearDetails();
            }
        } finally {
            moving = false;
        }
    }

    export function wireNavigation(): void {
        document.addEventListener('keydown', (event) => {
            if (!event.altKey || event.ctrlKey || event.metaKey) {
                return;
            }
            if (event.key === 'ArrowLeft') {
                event.preventDefault();
                navigate(-1);
            } else if (event.key === 'ArrowRight') {
                event.preventDefault();
                navigate(+1);
            }
        });

        // The mouse's thumb buttons, which is how most people go back in anything.
        document.addEventListener('auxclick', (event) => {
            if (event.button === 3) {
                event.preventDefault();
                navigate(-1);
            } else if (event.button === 4) {
                event.preventDefault();
                navigate(+1);
            }
        });

    }

}
