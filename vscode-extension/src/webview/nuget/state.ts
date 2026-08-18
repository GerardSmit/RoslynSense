/// <reference path="./dom.ts" />

namespace NG {
    export interface Row {
        pkg: NuGetMsg.PackageSummary;
        li: HTMLLIElement;
        iconImg: HTMLImageElement;
        iconFallback: HTMLElement;
        badges: HTMLElement;
        /** Per-row actions (the Installed tab's Update button); rebuilt by decorateRow. */
        actions?: HTMLElement;
        check?: HTMLInputElement;
        severity?: NuGetMsg.Severity;
        update?: NuGetMsg.PackageUpdate;
        projectPaths: string[];
    }

    export const state = {
        tab: 'browse' as NuGetMsg.Tab,
        projects: [] as NuGetMsg.ProjectRef[],
        installed: [] as NuGetMsg.ProjectPackages[],
        sources: [] as NuGetMsg.PackageSource[],
        scope: [] as string[],
        settings: { pageSize: 30, readme: 'rendered', showTransitive: true } as NuGetMsg.Settings,
        versions: {} as Record<string, string[]>,
        metadata: {} as Record<string, NuGetMsg.PackageMetadata | null>,
        audit: null as NuGetMsg.Audit | null,
        /** Available updates, kept for the Installed tab's inline hints and Update buttons. */
        updates: [] as NuGetMsg.PackageUpdate[],
        pendingSelect: null as string | null,
        hasMore: false,
        query: '',
        selectedVersion: null as string | null,
        splitPercent: 42,
        /**
         * Which feeds carry each installed package id, keyed lowercase. Null while "All sources" is
         * selected or before the answer lands — the filter treats null as "do not narrow", so the
         * list is never wrong, only briefly wider than it will be.
         */
        sourceMap: null as Record<string, string[]> | null,
    };

    /** The feed the top-right selector is on, or '' for all of them. */
    export function selectedSource(): string {
        return el<HTMLSelectElement>('source').value;
    }

    /**
     * Replies are dropped unless they belong to the current request. Switching tab, editing the
     * query or changing scope all bump this, so a slow search that lands after the user moved on
     * cannot repaint the list underneath them.
     */
    export let listGen = 0;
    export let detailsGen = 0;
    export let planGen = 0;

    export function nextListGen(): number {
        return ++listGen;
    }

    export function nextDetailsGen(): number {
        return ++detailsGen;
    }

    export function nextPlanGen(): number {
        return ++planGen;
    }

    const api = acquireVsCodeApi();

    export function post(message: NuGetMsg.ToHost): void {
        api.postMessage(message);
    }

    export function savedState(): NuGetMsg.SavedState | null {
        const stored = api.getState() as NuGetMsg.SavedState | undefined;
        return stored && stored.v === 2 ? stored : null;
    }

    let persistTimer: number | undefined;

    export function persist(): void {
        window.clearTimeout(persistTimer);
        persistTimer = window.setTimeout(() => {
            api.setState({
                v: 2,
                tab: state.tab,
                query: state.query,
                prerelease: el<HTMLInputElement>('prerelease').checked,
                versionLock: el<HTMLSelectElement>('version-lock').value as NuGetMsg.Lock,
                source: el<HTMLSelectElement>('source').value,
                selectedId: focusedRow?.pkg.id ?? null,
                splitPercent: state.splitPercent,
            } satisfies NuGetMsg.SavedState);
        }, 200);
    }
}

declare function acquireVsCodeApi(): {
    postMessage(message: unknown): void;
    getState(): unknown;
    setState(state: unknown): void;
};
