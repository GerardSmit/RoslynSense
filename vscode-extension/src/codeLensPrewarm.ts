/**
 * Which CodeLenses to resolve before handing a refreshed list to the editor.
 *
 * VS Code replaces any lens command that carries arguments with an internal delegate, keyed to the
 * lens list the command arrived in, and drops the key the moment a new list replaces that one. The
 * widget, meanwhile, keeps drawing the previous list's anchors until the new list has been
 * resolved. So a bare lens leaves a clickable anchor wired to a dead key for as long as its resolve
 * takes — and every refresh restarts the clock. Clicking in that window reports a command that does
 * not exist, in a toast raised by the editor itself, on a path where no extension code runs.
 *
 * Resolving in-viewport lenses before returning closes it: the list that kills the old keys is the
 * same one that carries live ones. Nothing here is extra work — the editor was about to resolve
 * exactly these lenses a tick later; it is the same work, done a message earlier.
 *
 * The viewport filter is the point of the module. Resolving the whole file would undo the
 * deliberate laziness the server relies on: a C# reference lens costs a workspace-wide symbol
 * search, and a large file's worth of those is not a thing to do on every scroll.
 */

/** Only the shape the selection needs, so the rule is testable without the `vscode` API. */
export interface LineRange {
    readonly start: { readonly line: number };
    readonly end: { readonly line: number };
}

/** How many lenses one pass will pre-resolve, whatever the viewport says. */
export const MAX_PRE_RESOLVED = 60;

/**
 * The indices of the lenses worth resolving now: those the editor is about to ask about anyway.
 *
 * A lens that already carries a command is skipped — it is already clickable, which is the whole
 * objective. Everything else is judged by lines rather than characters, because a lens is drawn
 * above its range and a viewport is reported in whole lines either way.
 *
 * The cap is a floor under the worst case rather than a tuning knob: a tall editor over a
 * generated file can have hundreds of lenses on screen at once, and the point of arriving a message
 * earlier is lost if arriving takes a second.
 */
export function lensesToPreResolve(
    lenses: readonly { readonly range: LineRange; readonly command?: unknown }[],
    visible: readonly LineRange[],
    limit: number = MAX_PRE_RESOLVED,
): number[] {
    if (visible.length === 0 || limit <= 0) {
        return [];
    }

    const chosen: number[] = [];

    for (let index = 0; index < lenses.length && chosen.length < limit; index++) {
        const lens = lenses[index];

        if (lens.command !== undefined && lens.command !== null) {
            continue;
        }

        if (visible.some((range) => intersects(lens.range, range))) {
            chosen.push(index);
        }
    }

    return chosen;
}

/** Whether two line ranges share a line. Touching at one line counts: a lens on the first visible
 * line is on screen. */
function intersects(a: LineRange, b: LineRange): boolean {
    return a.start.line <= b.end.line && b.start.line <= a.end.line;
}
