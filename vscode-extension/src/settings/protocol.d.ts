/**
 * The settings panel's message contract. Shared by the extension host and the webview script,
 * which are compiled separately and can only agree through this file.
 */
declare namespace SettingsMsg {
    /** One layer, as the panel needs to show it: where it is, and what it says. */
    interface Layer {
        readonly scope: Scope;
        readonly label: string;
        readonly filePath: string;
        readonly exists: boolean;
        /** The layer's own contents. Absent when the file does not exist or did not parse. */
        readonly json?: Record<string, unknown>;
        readonly parseError?: string;
        /**
         * Whether this layer is one the panel can write to. Only the four the scope selector
         * offers are; a `roslynsense.json` in some ancestor directory is shown as an origin but
         * edited where it lives.
         */
        readonly editable: boolean;
    }

    type Scope = 'global' | 'repo' | 'repoLocal' | 'personal';

    interface State {
        readonly type: 'state';
        /** The generated JSON Schema, which is what the form is built from. */
        readonly schema: unknown;
        readonly layers: readonly Layer[];
        /** Every layer merged, weakest first — what the server actually resolves to. */
        readonly effective: Record<string, unknown>;
        /** The directory the layers were resolved for. */
        readonly workingDirectory: string;
        /** Which scope the form is currently editing. */
        readonly scope: Scope;
        /** Set after a write, to say what happened. */
        readonly notice?: string;
    }

    /** Suggestions for a connection value being typed, as full replacement strings. */
    interface ConnectionCompletions {
        readonly type: 'connectionCompletions';
        /** The value the suggestions were computed for, so stale answers can be dropped. */
        readonly value: string;
        readonly items: readonly string[];
    }

    /** What a connection reference resolves to, or why it does not. Empty means nothing to show. */
    interface ConnectionPreview {
        readonly resolved?: string;
        readonly error?: string;
    }

    interface ConnectionsResolved {
        readonly type: 'connectionsResolved';
        readonly results: Readonly<Record<string, ConnectionPreview>>;
    }

    type ToView = State | ConnectionCompletions | ConnectionsResolved;

    /** Write one setting into the selected scope. `value: null` unsets it. */
    interface SetSetting {
        readonly type: 'set';
        readonly scope: Scope;
        readonly path: readonly string[];
        readonly value: unknown;
    }

    /** Switch which scope the form edits. */
    interface SelectScope {
        readonly type: 'selectScope';
        readonly scope: Scope;
    }

    /** Open a layer's file in an editor, for the settings the form does not render. */
    interface OpenFile {
        readonly type: 'openFile';
        readonly filePath: string;
    }

    /** Ask for suggestions for a connection value being typed. */
    interface CompleteConnection {
        readonly type: 'completeConnection';
        readonly value: string;
    }

    /** Ask what these connection values resolve to. */
    interface ResolveConnections {
        readonly type: 'resolveConnections';
        readonly values: readonly string[];
    }

    type ToHost = SetSetting | SelectScope | OpenFile | CompleteConnection | ResolveConnections;
}
