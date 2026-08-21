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

    /** One value a setting can take here, with a short note on what it means. */
    interface Choice {
        readonly value: string;
        readonly detail?: string;
    }

    /**
     * The values a setting can currently take. Answered by the server, because the list is a fact
     * about the solution rather than about the schema.
     */
    interface SettingChoices {
        readonly type: 'settingChoices';
        /** Echoed back so an answer that arrives after the form re-rendered is dropped. */
        readonly token: number;
        readonly items: readonly Choice[];
    }

    interface ShapeParameter {
        readonly name: string;
        readonly type: string;
    }

    /** One overload the configured class and member select. */
    interface ShapeMatch {
        readonly declaredBy: string;
        readonly name: string;
        readonly signature: string;
        readonly parameters: readonly ShapeParameter[];
        /** Whether the configured parameter list selects this one. */
        readonly matched: boolean;
    }

    /** What a class/member/signature triple resolves to in the loaded solution. */
    interface MemberShape {
        readonly type: 'memberShape';
        readonly token: number;
        readonly typeSuggestions: readonly string[];
        readonly memberSuggestions: readonly string[];
        readonly matches: readonly ShapeMatch[];
        readonly resolvedType?: string;
        readonly problem?: string;
    }

    /**
     * The server can answer things it could not a moment ago — it connected, or it finished
     * loading a solution. Every control that was told "nothing yet" asks again.
     */
    interface Resolvable {
        readonly type: 'resolvable';
    }

    type ToView =
        | State
        | ConnectionCompletions
        | ConnectionsResolved
        | SettingChoices
        | MemberShape
        | Resolvable;

    /**
     * The webview has loaded and holds nothing yet.
     *
     * A handshake rather than a post at wire time, because the page is built by a script that may
     * not have run when the panel is created — and because VS Code reloads it whenever it likes,
     * which used to leave an empty form behind.
     */
    interface Ready {
        readonly type: 'ready';
    }

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

    /** Ask what values a setting can take, for the solution as the page currently has it. */
    interface AskChoices {
        readonly type: 'askChoices';
        readonly token: number;
        /** The dotted path with item markers — `resources.lookups[].fallbacks`. */
        readonly path: string;
    }

    /** Ask what a class/member/signature triple selects. */
    interface AskMemberShape {
        readonly type: 'askMemberShape';
        readonly token: number;
        readonly containingType?: string;
        readonly memberName?: string;
        readonly parameterTypes?: readonly string[];
    }

    type ToHost =
        | Ready
        | SetSetting
        | SelectScope
        | OpenFile
        | CompleteConnection
        | ResolveConnections
        | AskChoices
        | AskMemberShape;
}
