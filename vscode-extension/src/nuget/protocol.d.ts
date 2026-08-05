/**
 * The wire format shared by the NuGet panel's two halves.
 *
 * Ambient declarations rather than a module: the extension host is CommonJS and the webview is a
 * single concatenated script, so an import would work in exactly one of them. Declared here, a
 * protocol change breaks compilation on both sides at once instead of failing silently at runtime.
 */
declare namespace NuGetMsg {
    // ---- Server shapes ---------------------------------------------------------------

    interface PackageSummary {
        id: string;
        version: string;
        authors: string | null;
        description: string | null;
        downloads: number | null;
        iconUrl: string | null;
        deprecated: boolean;
        vulnerable: boolean;
        installedVersion: string | null;
        installedVersions: string[];
        isCentrallyManaged: boolean;
        isGlobalPackageReference: boolean;
        versionSource: string | null;
        sourceName: string | null;
    }

    interface ProjectPackages {
        projectPath: string;
        projectName: string;
        targetFrameworks: string[];
        packages: PackageSummary[];
    }

    interface FeedOutcome {
        name: string;
        source: string;
        ok: boolean;
        unauthorized: boolean;
        error: string | null;
    }

    interface PackageSource {
        name: string;
        source: string;
        isEnabled: boolean;
        isMachineWide: boolean;
        isLocal: boolean;
        hasCredentials: boolean;
        configFilePath: string | null;
    }

    type Severity = 'none' | 'patch' | 'minor' | 'major' | 'unknown';

    interface PackageUpdate {
        id: string;
        currentVersion: string;
        latestVersion: string;
        severity: Severity;
        projectPath: string;
        projectName: string;
        isCentrallyManaged: boolean;
        isGlobalPackageReference: boolean;
        versionSource: string | null;
        /**
         * The newest usable version beyond the project's platform band, when band alignment held
         * latestVersion back. Shown as a disclosure, never offered as the update.
         */
        latestUncapped: string | null;
    }

    /** A reference the selected updates drag along with them (NU1605 prevention). */
    interface InducedUpdate {
        id: string;
        currentVersion: string;
        version: string;
        projectPath: string;
        projectName: string;
        requiredBy: string;
        requiredByVersion: string;
    }

    interface DependencyGroup {
        targetFramework: string;
        dependencies: { id: string; versionRange: string }[];
    }

    interface Deprecation {
        reasons: string[];
        message: string | null;
        alternatePackageId: string | null;
        alternateVersionRange: string | null;
    }

    interface Vulnerability {
        severity: number;
        advisoryUrl: string | null;
    }

    interface PackageMetadata {
        id: string;
        version: string;
        title: string | null;
        description: string | null;
        summary: string | null;
        authors: string | null;
        owners: string | null;
        tags: string | null;
        downloads: number | null;
        published: string | null;
        isListed: boolean;
        prefixReserved: boolean;
        requireLicenseAcceptance: boolean;
        licenseExpression: string | null;
        licenseFileText: string | null;
        licenseUrl: string | null;
        projectUrl: string | null;
        packageDetailsUrl: string | null;
        reportAbuseUrl: string | null;
        iconUrl: string | null;
        readmeMarkdown: string | null;
        dependencyGroups: DependencyGroup[];
        deprecation: Deprecation | null;
        vulnerabilities: Vulnerability[];
        allVersions: string[];
        sourceName: string | null;
    }

    interface Advisory {
        id: string;
        version: string;
        projectPath: string;
        targetFramework: string;
        isTransitive: boolean;
        severity: number;
        advisoryUrl: string | null;
    }

    interface DeprecationEntry {
        id: string;
        version: string;
        projectPath: string;
        targetFramework: string;
        isTransitive: boolean;
        reasons: string[];
        alternatePackageId: string | null;
        alternateVersionRange: string | null;
    }

    interface Audit {
        vulnerabilities: Advisory[];
        deprecations: DeprecationEntry[];
        error: string | null;
    }

    interface TransitivePackage {
        id: string;
        version: string;
        targetFramework: string;
        hasChildren: boolean;
    }

    interface UpdateOutcome {
        id: string;
        version: string;
        projectPath: string;
        success: boolean;
        message: string | null;
    }

    interface FrameworkCheck {
        compatible: boolean;
        unsupported: { projectPath: string; projectName: string; targetFrameworks: string[] }[];
        packageFrameworks: string[];
        warning: string | null;
    }

    // ---- Panel state -----------------------------------------------------------------

    type Tab = 'browse' | 'installed' | 'updates' | 'sources';

    type SourceAction = 'add' | 'update' | 'remove' | 'enable' | 'disable' | 'reorder';

    /**
     * How far a version may move. The platform band for Microsoft.Extensions.*-style families is
     * not a lock value: it is always applied by the server (see the alignPlatformPackages setting),
     * so "latest" already means "latest for the .NET major this project targets".
     */
    type Lock = 'none' | 'major' | 'minor';

    interface SavedState {
        v: 2;
        tab: Tab;
        query: string;
        prerelease: boolean;
        versionLock: Lock;
        source: string;
        selectedId: string | null;
        /** Width of the list pane, as a percentage of the split. */
        splitPercent: number;
    }

    interface ProjectRef {
        projectPath: string;
        projectName: string;
        targetFrameworks: string[];
    }

    interface Settings {
        pageSize: number;
        readme: 'rendered' | 'plain' | 'off';
        showTransitive: boolean;
    }

    // ---- Webview to host -------------------------------------------------------------

    type ToHost =
        | { type: 'ready'; state: SavedState | null }
        | { type: 'search'; gen: number; query: string; includePrerelease: boolean; source: string; skip: number }
        | { type: 'installed'; gen: number }
        | { type: 'updates'; gen: number; includePrerelease: boolean; versionLock: Lock; projectPaths: string[] }
        | { type: 'audit'; gen: number; refresh: boolean }
        | { type: 'versions'; id: string; includePrerelease: boolean }
        | { type: 'metadata'; gen: number; id: string; version: string | null }
        | { type: 'icon'; id: string; version: string | null; iconUrl: string | null; allowDownload: boolean }
        | { type: 'transitive'; gen: number; projectPath: string; packageId: string | null }
        | { type: 'pickScope' }
        | { type: 'sources' }
        | { type: 'sourceEdit'; action: SourceAction; name?: string; source?: string; order?: string[] }
        | { type: 'install'; id: string; version: string; projectPaths: string[]; requireLicenseAcceptance: boolean; license: string | null }
        | { type: 'uninstall'; id: string; projectPaths: string[] }
        | { type: 'consolidate'; id: string; version: string }
        | {
              type: 'updateAll';
              packages: { id: string; version: string; projectPaths: string[] }[];
              versionLock: Lock;
              includePrerelease: boolean;
          }
        | {
              type: 'updatePlan';
              gen: number;
              packages: { id: string; version: string; projectPaths: string[] }[];
              versionLock: Lock;
              includePrerelease: boolean;
          }
        | { type: 'openExternal'; url: string }
        | { type: 'openFile'; path: string }
        | { type: 'signIn'; feedName: string; feedUrl: string }
        | { type: 'persist'; state: SavedState };

    // ---- Host to webview -------------------------------------------------------------

    type ToView =
        | { type: 'boot'; scope: string[]; projects: ProjectRef[]; sources: PackageSource[]; settings: Settings; state: SavedState | null }
        | { type: 'results'; gen: number; tab: Tab; skip: number; results: PackageSummary[]; hasMore: boolean; feeds: FeedOutcome[] }
        | { type: 'projects'; gen: number; projects: ProjectPackages[] }
        | { type: 'updates'; gen: number; updates: PackageUpdate[]; feeds: FeedOutcome[] }
        | { type: 'updatePlan'; gen: number; induced: InducedUpdate[] }
        | { type: 'audit'; gen: number; audit: Audit }
        | { type: 'versions'; id: string; versions: string[] }
        | { type: 'metadata'; gen: number; id: string; version: string; metadata: PackageMetadata | null }
        | { type: 'icon'; id: string; key: string; dataUri: string | null }
        | { type: 'transitive'; gen: number; projectPath: string; packages: TransitivePackage[] }
        | { type: 'scope'; projectPaths: string[]; selectPackage?: string | null }
        | { type: 'goToTab'; tab: Tab }
        | { type: 'sources'; sources: PackageSource[] }
        | { type: 'sourceEditResult'; success: boolean; message: string; sources: PackageSource[] }
        | { type: 'busy'; busy: boolean }
        | { type: 'opResult'; results: UpdateOutcome[] }
        | { type: 'refresh' }
        | { type: 'error'; message: string; scope: 'list' | 'details' };
}
