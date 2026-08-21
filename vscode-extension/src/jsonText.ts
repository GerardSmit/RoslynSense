/**
 * Reading JSON off disk, past the byte-order mark a Windows editor leaves on it.
 *
 * Node hands a `utf8` read the U+FEFF verbatim, and every JSON parser there is — `JSON.parse`,
 * `jsonc-parser` — reads it as a character with no business being there. .NET's `File.ReadAllText`
 * strips it, so a `roslynsense.json` saved by Visual Studio, by Notepad, or by PowerShell's
 * `Set-Content` loads in the server and is skipped by the extension. That split is worse than
 * either half: the settings apply, and the page that shows them says they do not.
 */

const ByteOrderMark = '\uFEFF';

/**
 * The text without its leading mark, and the mark itself so a write can put it back.
 *
 * Kept rather than dropped, because the file belongs to whoever wrote it: a checkout whose
 * encoding is what the team's editor produces should not change because somebody moved a toggle
 * on the settings page.
 */
export function splitByteOrderMark(text: string): { mark: string; body: string } {
    return text.startsWith(ByteOrderMark)
        ? { mark: ByteOrderMark, body: text.slice(ByteOrderMark.length) }
        : { mark: '', body: text };
}

/** The text a parser should be handed. */
export function withoutByteOrderMark(text: string): string {
    return splitByteOrderMark(text).body;
}
