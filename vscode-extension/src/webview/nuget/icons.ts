/// <reference path="./state.ts" />

/**
 * Package icons, loaded per visible row.
 *
 * The slot is always present and always 32x32, whether or not an icon exists, so a row's geometry
 * is fixed the moment it is created. Icons swap in as a pure paint. That is the whole fix for the
 * list shifting under the cursor as images landed.
 */
namespace NG {
    type IconState = 'pending' | { dataUri: string | null };

    const cache = new Map<string, IconState>();
    let observer: IntersectionObserver | undefined;

    /**
     * Drops requests that were posted but never answered — a client restart mid-scroll otherwise
     * leaves those rows marked pending, and nothing ever asks for them again.
     */
    export function clearPendingIcons(): void {
        for (const [key, value] of [...cache]) {
            if (value === 'pending') {
                cache.delete(key);
            }
        }
    }

    export function resetIcons(list: HTMLElement): void {
        observer?.disconnect();
        observer = new IntersectionObserver(
            (entries) => {
                for (const entry of entries) {
                    if (!entry.isIntersecting) {
                        continue;
                    }
                    observer?.unobserve(entry.target);
                    const row = rowByElement(entry.target as HTMLElement);
                    if (row) {
                        request(row);
                    }
                }
            },
            // A little ahead of the viewport, so a row is usually painted before it is read.
            { root: list, rootMargin: '200px 0px' }
        );
    }

    /** Applies a cached icon immediately, or queues the row for loading. */
    export function attachIcon(row: Row): void {
        const key = iconKey(row.pkg);
        const cached = cache.get(key);

        if (cached && cached !== 'pending') {
            apply(row, cached.dataUri);
            return;
        }

        if (cached === 'pending') {
            return;
        }

        observer?.observe(row.li);
    }

    /**
     * A reply that arrives after its list was replaced simply misses the lookup and is cached for
     * next time — which is exactly right, and why icons need no generation token.
     */
    export function onIcon(key: string, dataUri: string | null): void {
        cache.set(key, { dataUri });

        for (const row of rows) {
            if (iconKey(row.pkg) === key) {
                apply(row, dataUri);
            }
        }

        const detail = el<HTMLImageElement>('detail-icon');
        if (detail && detail.dataset.key === key && dataUri) {
            detail.src = dataUri;
            detail.hidden = false;
        }
    }

    export function requestIconFor(pkg: NuGetMsg.PackageSummary): void {
        const key = iconKey(pkg);
        const cached = cache.get(key);
        if (cached && cached !== 'pending') {
            return;
        }
        if (cached === 'pending') {
            return;
        }
        cache.set(key, 'pending');
        post({
            type: 'icon',
            id: pkg.id,
            version: pkg.installedVersion ?? pkg.version ?? null,
            iconUrl: pkg.iconUrl,
            // Installed packages already sit in the global packages folder, so the embedded icon
            // costs nothing. While browsing it would mean a download per row.
            allowDownload: state.tab !== 'browse',
        });
    }

    export function cachedIcon(pkg: NuGetMsg.PackageSummary): string | null {
        const cached = cache.get(iconKey(pkg));
        return cached && cached !== 'pending' ? cached.dataUri : null;
    }

    export function iconKey(pkg: NuGetMsg.PackageSummary): string {
        return pkg.iconUrl ?? `embedded:${pkg.id}`;
    }

    function request(row: Row): void {
        requestIconFor(row.pkg);
    }

    function apply(row: Row, dataUri: string | null): void {
        if (!dataUri) {
            return;
        }
        // Swapping on load avoids a blank frame if the data URI turns out to be unreadable.
        row.iconImg.onload = () => {
            row.iconImg.hidden = false;
            row.iconFallback.hidden = true;
        };
        row.iconImg.src = dataUri;
    }

    function rowByElement(element: HTMLElement): Row | undefined {
        return rows.find((row) => row.li === element);
    }
}
