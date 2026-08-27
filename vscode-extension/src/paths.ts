/**
 * Comparing file-system paths the way the file system does, rather than the way `===` does.
 *
 * Windows is the whole reason this exists. The same file reaches the extension spelled three
 * different ways: VS Code hands back `Uri.fsPath` with a lower-cased drive letter, MSBuild reports
 * the casing the .sln was written with, and the server echoes back whatever it was given. A
 * comparison that respects case therefore answers "different file" for two spellings of one file,
 * and the feature built on it silently never fires — which is worse than firing wrongly, because
 * nothing appears in the log to say it happened.
 *
 * `path.relative(...).startsWith('..')` is not the fix: it is case-sensitive on every platform, so
 * it carries exactly the same bug while looking like it does not.
 */

/** A path reduced to the form that every spelling of the same file shares. */
export function normalisePath(value: string): string {
    return value.split('\\').join('/').toLowerCase();
}

/**
 * Whether `path` sits inside `directory`, at any depth.
 *
 * The trailing separator on the parent is what stops a sibling whose name merely begins with the
 * directory's name — `Foo.Tests.Integration` against `Foo.Tests` — from counting as being inside
 * it. A directory is not under itself.
 */
export function isUnder(path: string, directory: string): boolean {
    const parent = normalisePath(directory).replace(/\/$/, '');
    return normalisePath(path).startsWith(parent + '/');
}

/**
 * The glob claiming the source the server fetched or decompiled, under its cache in
 * `tempDirectory`.
 *
 * Spelled the way a document's own path is spelled, or the filter never matches and the file is
 * silently served nothing: `Uri.fsPath` lower-cases the drive letter, `os.tmpdir()` reports it the
 * way the environment variable has it, and VS Code's glob matcher compares the two literally.
 */
export function externalSourceGlob(tempDirectory: string): string {
    const root = `${normaliseSeparators(tempDirectory).replace(/\/$/, '')}/RoslynMCP`;
    return `${/^[A-Za-z]:/.test(root) ? root[0].toLowerCase() + root.slice(1) : root}/**/*`;
}

function normaliseSeparators(value: string): string {
    return value.split('\\').join('/');
}
