import * as vscode from 'vscode';
import type { WorkspaceEdit as LspWorkspaceEdit } from 'vscode-languageclient';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * Submenus for the lightbulb, which LSP does not have.
 *
 * Roslyn hands its configuration fixes over as groups — "Configure IDE0074 severity" with five
 * severities inside it, three such groups per diagnostic — and the protocol has no way to say
 * "these belong together", so the server used to flatten them into fifteen sibling entries that
 * buried the actual fix. Instead the server now collapses a group to one entry whose command is
 * this picker, and the picker walks the tree in quick picks until it reaches a leaf, then
 * resolves that leaf exactly like any other code action.
 *
 * The walk is a loop rather than a single pick because the tree's depth is Roslyn's to decide.
 */

export const PICK_NESTED_ACTION_COMMAND = 'roslynSense.pickCodeAction';

/** One node of the tree the server sends: `children` to descend, `id` to resolve. */
interface NestedCodeActionGroup {
    title: string;
    id?: number | null;
    children?: NestedCodeActionGroup[] | null;
}

interface ResolvedCodeAction {
    edit?: LspWorkspaceEdit | null;
}

/**
 * Registers the picker.
 *
 * `getClient` takes the key the middleware stamped onto the command's arguments. One editor has
 * one command table, so this id can only be registered once however many clients are running —
 * and the client that produced the group is the only one whose resolve cache holds its ids.
 */
export function registerNestedCodeActions(
    context: vscode.ExtensionContext,
    getClient: (clientKey?: string) => LanguageClient | undefined
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand(
            PICK_NESTED_ACTION_COMMAND,
            async (group: NestedCodeActionGroup, clientKey?: string) => {
                const client = getClient(clientKey);
                if (!client || !group) {
                    return;
                }

                const leaf = await descend(group);
                if (!leaf || leaf.id === undefined || leaf.id === null) {
                    return; // dismissed, or a group the server sent with nothing inside it
                }

                // The same request the editor would have sent had this been an ordinary entry.
                // `kind` is not read back by the server; it is here because the protocol's
                // CodeAction requires it.
                const resolved = await client.sendRequest<ResolvedCodeAction>('codeAction/resolve', {
                    title: leaf.title,
                    kind: 'quickfix',
                    data: { id: leaf.id },
                });

                if (!resolved?.edit) {
                    void vscode.window.showWarningMessage(
                        `'${leaf.title}' could not be applied. The editor may have moved on since the list was built — try again.`
                    );
                    return;
                }

                await vscode.workspace.applyEdit(
                    await client.protocol2CodeConverter.asWorkspaceEdit(resolved.edit)
                );
            }
        )
    );
}

/** Walks down through quick picks until the node has no children left. Undefined if dismissed. */
async function descend(group: NestedCodeActionGroup): Promise<NestedCodeActionGroup | undefined> {
    let current = group;

    while (current.children && current.children.length > 0) {
        const children = current.children;

        // A single child is not a choice — descend without asking. Roslyn produces these for a
        // rule whose only configurable target is itself.
        if (children.length === 1) {
            current = children[0];
            continue;
        }

        const picked = await vscode.window.showQuickPick(
            children.map((child) => ({
                label: child.title,
                // The arrow tells the user the list will not close on this one.
                description: child.children && child.children.length > 0 ? '…' : undefined,
                child,
            })),
            { title: current.title, placeHolder: current.title, matchOnDescription: false }
        );

        if (!picked) {
            return undefined; // Esc anywhere in the walk abandons the whole action
        }
        current = picked.child;
    }

    return current;
}

/**
 * Stamps `clientKey` onto every picker command a client returns.
 *
 * Without it the picker would have to guess which server to resolve against, and in a multi-root
 * window that guess is wrong exactly when it matters: the ids are per connection, so asking the
 * other client resolves to nothing and the entry silently does nothing.
 */
export function bindNestedCodeActions(
    actions: (vscode.Command | vscode.CodeAction)[] | null | undefined,
    clientKey: string
): void {
    for (const action of actions ?? []) {
        const command = 'command' in action && typeof action.command === 'object' ? action.command : undefined;
        if (command?.command === PICK_NESTED_ACTION_COMMAND) {
            command.arguments = [...(command.arguments ?? []), clientKey];
        }
    }
}
