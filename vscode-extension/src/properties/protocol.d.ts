/**
 * The Properties panel's message contract. Shared by the extension host and the webview script,
 * which are compiled separately and can only agree through this file.
 *
 * The three payload shapes mirror `roslynSense/itemProperties` exactly, so the host forwards the
 * server's answer rather than translating it — a field the server learns to send is a field the
 * page can start showing without a second place to change.
 */
declare namespace PropsMsg {
    /** One file's MSBuild item, as the form needs it. */
    interface File {
        readonly itemType: string;
        /** What the build action may be set to, the file's own included even when unusual. */
        readonly itemTypes: readonly string[];
        readonly copyToOutputDirectory: string | null;
        readonly generator: string | null;
        readonly customToolNamespace: string | null;
        readonly link: string | null;
        readonly dependentUpon: string | null;
        /** True when a wildcard claimed the file rather than the project naming it. */
        readonly fromGlob: boolean;
        /** The file whose XML carried the item — an SDK targets file, for a globbed one. */
        readonly declaredIn: string | null;
        /** False for a file on disk that no item claims. */
        readonly inProject: boolean;
        readonly sdkStyle: boolean;
    }

    /** One folder's properties, which for now is the one property a folder has. */
    interface Folder {
        readonly namespaceProvider: boolean;
        /** What a new file here would be given, with the checkbox's effect already applied. */
        readonly namespace: string | null;
        readonly relativePath: string;
    }

    interface Properties {
        readonly path: string;
        readonly kind: 'file' | 'folder';
        readonly projectPath: string | null;
        readonly projectName: string | null;
        readonly file?: File | null;
        readonly folder?: Folder | null;
        /** Why there is nothing to show, when there is nothing. */
        readonly reason?: string | null;
    }

    interface State {
        readonly type: 'state';
        readonly properties: Properties;
        /** What the last write did, shown under the form until the next one. */
        readonly notice?: string;
        /** True while a write is in flight, which is what disables the controls. */
        readonly busy?: boolean;
    }

    interface Failed {
        readonly type: 'failed';
        readonly message: string;
    }

    type ToView = State | Failed;

    /**
     * One changed control. Everything but `path` is optional and what is absent is left alone,
     * which is what lets the page send the control that changed rather than the whole form.
     * An empty string clears a metadata value; the server writes no element for it.
     */
    interface Apply {
        readonly type: 'apply';
        readonly itemType?: string;
        readonly copyToOutputDirectory?: string;
        readonly generator?: string;
        readonly customToolNamespace?: string;
        readonly namespaceProvider?: boolean;
    }

    /** The page is up and wants the current answer — also what a manual refresh sends. */
    interface Ready {
        readonly type: 'ready';
    }

    /** Open the project file that declares the item, at the line that does. */
    interface Reveal {
        readonly type: 'reveal';
        readonly target: 'project' | 'declaredIn';
    }

    type ToHost = Apply | Ready | Reveal;
}
