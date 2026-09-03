/**
 * The wire format shared by the Search Everywhere panel's two halves.
 *
 * Ambient declarations rather than a module, for the same reason as NuGetMsg: the extension host
 * is CommonJS and the webview is one concatenated script, so a real import would work in exactly
 * one of them — declared here, a protocol change breaks both compilations at once.
 */
declare namespace SearchMsg {
    /** Rider's tab row. `all`, `classes`, `files` and `symbols` are one server search with a
     * kind filter; `text` is the literal scan; `actions` never leaves the extension host. */
    type Tab = 'all' | 'classes' | 'files' | 'symbols' | 'actions' | 'text';

    /** One row from roslynSense/searchEverywhere. */
    interface SymbolItem {
        kind: 'type' | 'member' | 'file';
        name: string;
        container: string | null;
        uri: string;
        path: string;
        line: number;
        character: number;
        symbolKind: number;
    }

    /** One row from roslynSense/searchText. */
    interface TextItem {
        uri: string;
        path: string;
        line: number;
        character: number;
        lineText: string;
    }

    /** A command contributed by any installed extension — the Actions tab. */
    interface ActionItem {
        command: string;
        title: string;
        category: string | null;
        keybinding: string | null;
    }

    /** One themed span of a preview line, produced by the host's TextMate engine. */
    interface PreviewToken {
        text: string;
        /** A concrete theme color, or null for the editor's default foreground. */
        color: string | null;
        /** Bit set: 1 italic, 2 bold, 4 underline, 8 strikethrough. */
        fontStyle: number;
    }

    /** An open editor, shown before anything is typed. */
    interface RecentItem {
        name: string;
        relativePath: string;
        uri: string;
    }

    type ToHost =
        | { type: 'ready' }
        | {
              type: 'search';
              id: number;
              tab: Tab;
              query: string;
              includeNonSolution: boolean;
          }
        | {
              type: 'preview';
              id: number;
              uri: string;
              line: number;
              /** A file hit at line 0: the host may skip the using block and start the preview
               * at the first type declaration. */
              skipPreamble: boolean;
          }
        | {
              type: 'open';
              uri: string;
              line: number;
              character: number;
              /** Rider's "Open In Right Split". */
              beside: boolean;
              /** Files land at the top without a selection jump; symbols land on their line. */
              isFile: boolean;
          }
        | { type: 'runAction'; command: string }
        | { type: 'close' };

    type ToView =
        | { type: 'boot'; recent: RecentItem[] }
        | {
              type: 'results';
              id: number;
              tab: Tab;
              items: SymbolItem[] | TextItem[] | ActionItem[];
              truncated: boolean;
              /** The solution was still loading, so these rows came from the server's name index
               * rather than from the workspace. The list is complete for solution source but knows
               * nothing of referenced assemblies; the panel says so, and asks again by itself once
               * the load lands. */
              loading?: boolean;
          }
        | {
              type: 'previewText';
              id: number;
              /** 0-based line number of `lines[0]` in the document. */
              startLine: number;
              targetLine: number;
              lines: string[];
              path: string;
              /** The document's language id, for the webview's fallback tokenizer. */
              languageId: string;
              /** Theme-colored spans per line from the host's TextMate engine, or null when no
               * grammar exists — the webview then falls back to its own tokenizer. */
              tokens: PreviewToken[][] | null;
          }
        | {
              type: 'error';
              /** Which lane failed: a search error replaces the result list, a preview error
               * only fills the preview pane — their request ids are separate counters. */
              scope: 'search' | 'preview';
              id: number;
              message: string;
          };
}
