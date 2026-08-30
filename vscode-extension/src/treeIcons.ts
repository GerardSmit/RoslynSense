import * as Path from 'path';
import * as vscode from 'vscode';

/**
 * The tree rows both RoslynSense trees are built from, and what each one is drawn with.
 *
 * Shared rather than duplicated because the server says so: the node kinds are declared in one
 * table on the wire (`SolutionNodeKind`) precisely so that a kind means the same thing and gets
 * the same icon wherever it is produced. The Solution Explorer draws the solution's structure and
 * the Discovery view draws what the solution runs and exposes, but a project row is a project row
 * in both.
 */

export interface SolutionTreeNode {
    id: string;
    kind: string;
    label: string;
    description: string | null;
    resourceUri: string | null;
    hasChildren: boolean;
    contextValue: string;
    dimmed: boolean;
    highlights: [number, number][] | null;

    /** What the hover says, when the row itself cannot hold it. */
    tooltip?: string | null;

    /**
     * Where clicking this row should land, when that is somewhere other than the top of
     * `resourceUri`. A row standing for something written inside a file — one registration among
     * twenty in a startup method — has to name the line, or the click lands at the top and leaves
     * the reader to find what they clicked on.
     */
    goTo?: TreeNavigation | null;

    /** A second place worth going, offered on the context menu rather than on click. */
    goToSecondary?: TreeNavigation | null;
}

/** A place in a document a tree row can open. */
export interface TreeNavigation {
    uri: string;
    range: {
        start: { line: number; character: number };
        end: { line: number; character: number };
    };
}

/** What a row can be drawn with: a codicon, a shipped badge, or a light/dark icon pair. */
export type NodeIcon = vscode.ThemeIcon | vscode.Uri | { light: vscode.Uri; dark: vscode.Uri };

/**
 * One of the shipped structural icons, per theme.
 *
 * These are drawn rather than themed the way the codicons are: a codicon is a single glyph in a
 * single colour, and the structural rows want what ReSharper and Rider give them — a neutral
 * outline carrying one small accent, and composites like a folder with a package cube in the
 * corner. Two variants because "neutral" is a different grey on a light background.
 */
function treeIcon(name: string, extensionUri: vscode.Uri): NodeIcon {
    return {
        light: vscode.Uri.joinPath(extensionUri, 'media', 'tree', 'light', `${name}.svg`),
        dark: vscode.Uri.joinPath(extensionUri, 'media', 'tree', 'dark', `${name}.svg`),
    };
}

/**
 * Folders that mean something beyond their name, recognised the way Visual Studio recognises
 * them: by the name itself. `Properties` holds the app designer files; `wwwroot` is the web
 * root. A directory that merely shares the name gets the badge too, which is the trade Visual
 * Studio makes as well.
 */
function folderIconName(label: string): string {
    switch (label.toLowerCase()) {
        case 'properties':
        case 'my project':
            return 'folder-properties';
        case 'wwwroot':
            return 'folder-www';
        default:
            return 'folder';
    }
}

/**
 * The icon a project is drawn with, by the extension of its project file.
 *
 * Codicons have one project glyph and no notion of language, so a C# and a Visual Basic project
 * would be the same grey box — the one distinction Visual Studio, Rider and ReSharper all draw
 * first. Each has a `-dim` variant for when the project is unloaded.
 *
 * Projects only. A language mark on every `.cs` as well makes a project row indistinguishable
 * from the files inside it, and drowns out the one row in the branch that the mark is there for.
 */
const PROJECT_ICONS: Record<string, string> = {
    '.csproj': 'project-csharp',
    '.vbproj': 'project-vb',
    '.fsproj': 'project-fsharp',
};

/**
 * Everything else, as a tinted codicon.
 *
 * The point is coverage rather than fidelity: an extension that falls through here still gets a
 * glyph, so no row is ever drawn without one. See {@link iconFor} for why that matters.
 */
const FILE_CODICONS: Record<string, [string, string]> = {
    // The source languages, marked by colour rather than by a badge — the badge is the project's.
    '.cs': ['file-code', 'charts.purple'],
    '.csx': ['file-code', 'charts.purple'],
    '.vb': ['file-code', 'charts.blue'],
    '.fs': ['file-code', 'charts.green'],
    '.fsi': ['file-code', 'charts.green'],
    '.fsx': ['file-code', 'charts.green'],
    '.razor': ['code', 'charts.purple'],
    '.cshtml': ['code', 'charts.purple'],
    '.aspx': ['code', 'charts.purple'],
    '.ascx': ['code', 'charts.purple'],
    '.ashx': ['code', 'charts.purple'],
    '.master': ['code', 'charts.purple'],
    '.json': ['json', 'charts.blue'],
    '.xml': ['code', 'charts.green'],
    '.xaml': ['code', 'charts.green'],
    '.config': ['settings-gear', 'charts.blue'],
    '.props': ['settings-gear', 'charts.blue'],
    '.targets': ['settings-gear', 'charts.blue'],
    '.editorconfig': ['settings-gear', 'charts.blue'],
    '.yml': ['settings-gear', 'charts.blue'],
    '.yaml': ['settings-gear', 'charts.blue'],
    '.toml': ['settings-gear', 'charts.blue'],
    '.ini': ['settings-gear', 'charts.blue'],
    '.resx': ['symbol-string', 'charts.green'],
    '.md': ['markdown', 'charts.blue'],
    '.txt': ['note', 'descriptionForeground'],
    '.sql': ['database', 'charts.blue'],
    '.ts': ['file-code', 'charts.blue'],
    '.tsx': ['file-code', 'charts.blue'],
    '.js': ['file-code', 'charts.blue'],
    '.mjs': ['file-code', 'charts.blue'],
    '.css': ['symbol-color', 'charts.blue'],
    '.scss': ['symbol-color', 'charts.blue'],
    '.html': ['browser', 'charts.green'],
    '.htm': ['browser', 'charts.green'],
    '.sh': ['terminal', 'charts.green'],
    '.ps1': ['terminal', 'charts.blue'],
    '.cmd': ['terminal', 'descriptionForeground'],
    '.bat': ['terminal', 'descriptionForeground'],
    '.png': ['file-media', 'charts.purple'],
    '.jpg': ['file-media', 'charts.purple'],
    '.jpeg': ['file-media', 'charts.purple'],
    '.gif': ['file-media', 'charts.purple'],
    '.svg': ['file-media', 'charts.purple'],
    '.ico': ['file-media', 'charts.purple'],
    '.dll': ['library', 'charts.green'],
    '.exe': ['library', 'charts.green'],
    '.pdb': ['library', 'descriptionForeground'],
    '.snk': ['lock', 'charts.green'],
    '.pfx': ['lock', 'charts.green'],
    '.sln': ['versions', 'charts.purple'],
    '.slnx': ['versions', 'charts.purple'],
};

function extensionOf(resourceUri: string | null): string {
    return resourceUri
        ? Path.extname(vscode.Uri.parse(resourceUri).fsPath).toLowerCase()
        : '';
}

function badgeUri(name: string, extensionUri: vscode.Uri): vscode.Uri {
    return vscode.Uri.joinPath(extensionUri, 'media', `lang-${name}.svg`);
}

/** The icon for a project, by the language it is written in. */
function languageIcon(resourceUri: string | null, extensionUri: vscode.Uri): NodeIcon {
    return treeIcon(PROJECT_ICONS[extensionOf(resourceUri)] ?? 'project', extensionUri);
}

/**
 * The icon for a file.
 *
 * `ThemeIcon.File` hands the decision to the user's file icon theme, which is the friendlier
 * answer right up until the theme has nothing for the extension — or the user has no file icon
 * theme at all. Then the row is drawn without an icon, and one icon-less row is enough to shift a
 * whole branch out of line (see {@link iconFor}). Drawing files ourselves is what makes every row
 * the same width; `solutionExplorer.fileIcons` gives the icon theme back to anyone who prefers it.
 */
function fileIcon(
    resourceUri: string | null,
    extensionUri: vscode.Uri,
    fromIconTheme: boolean
): NodeIcon {
    if (fromIconTheme) {
        return vscode.ThemeIcon.File;
    }
    const extension = extensionOf(resourceUri);
    // The one file that keeps a shipped badge: no codicon says "protobuf", and `.proto` has no
    // project of its own for a badge to sit on instead.
    if (extension === '.proto') {
        return badgeUri('proto', extensionUri);
    }
    const [id, color] = FILE_CODICONS[extension] ?? ['file', 'descriptionForeground'];
    return tinted(id, color);
}

/**
 * A codicon with a tint, the way Rider and Visual Studio colour their tree.
 *
 * From the charts palette, blue, green, purple and red only. `charts.orange` and `charts.yellow`
 * resolve to #d18616 and #cca700, which at 16px on a dark background read as brown rather than
 * as a colour anyone chose. Red is the fourth because a fourth Discovery section needed one and
 * #f14c4c is the only remaining entry that stays a colour at that size.
 */
function tinted(id: string, color: string): vscode.ThemeIcon {
    return new vscode.ThemeIcon(id, new vscode.ThemeColor(color));
}

/**
 * What a row is drawn with.
 *
 * Every kind gets an icon of its own, because a tree drawn entirely in the foreground colour
 * reads as one undifferentiated list. A project carries the language badge; the files inside it
 * carry a glyph tinted in the same family, so the project is still the row that stands out.
 *
 * Every expandable row must end up with an icon, and that is why folders are shipped SVGs and
 * never `ThemeIcon('folder')`. VS Code special-cases the *id* `folder` (and `file`) on any row
 * that has a resourceUri: `ThemeIcon.isFolder` compares the id alone, colour and all, and hands
 * the row to the user's file icon theme. Most file icon themes — Seti, the default — ship file
 * icons and no folder icons, so the row renders with no icon at all. VS Code reacts to an
 * expandable row without an icon by collapsing the twistie column on its *leaf* siblings, to
 * line their icons up with the arrows; a row that does have one keeps the column. Mixing the two
 * indents the icon-bearing rows a whole extra level and lines up nothing with anything.
 *
 * Colour is deliberately sparse, the way ReSharper draws the same tree: a neutral outline with
 * at most one accent per icon. Blue is the dependency family end to end, purple is the solution's
 * mark and generated code, grey is what is dimmed or merely transitive. Language badges and file
 * glyphs carry their own colour; the structure around them stays quiet so they can.
 */
export function iconFor(
    node: SolutionTreeNode,
    extensionUri: vscode.Uri,
    fileIconsFromTheme: boolean
): NodeIcon {
    // A scheduled job's context value is composed — "cronJobDynamicTarget" and friends — so it
    // is read rather than matched, and only the one fact the icon is about is asked for.
    if (node.kind === 'cronJob') {
        // Marked rather than dimmed: dimming already means "the workspace cannot answer about
        // this" in this tree, and an unloaded project and a config-driven schedule must not look
        // the same.
        return node.contextValue.includes('Dynamic')
            ? tinted('question', 'charts.purple')
            : tinted('watch', 'charts.purple');
    }
    // A project's kind stays "project" whether it is runnable or unloaded; only its context
    // value says which, and an unloaded one is drawn greyed the way its label already is.
    switch (node.kind === 'project' ? node.contextValue : node.kind) {
        case 'solution':
            return treeIcon('solution', extensionUri);
        // A solution folder is not a directory — it exists only in the .sln — so it is drawn
        // apart from the real folders it sits beside: the folder shape, carrying the solution's
        // purple mark.
        case 'solutionFolder':
            return treeIcon('solution-folder', extensionUri);
        case 'folder':
            return treeIcon(folderIconName(node.label), extensionUri);
        case 'project':
        case 'projectRunnable':
        case 'projectRef':
            // Dimmed here does not mean unloaded-by-choice — that is the case below, with its own
            // context value — it means the workspace has not loaded this project yet. Same icon
            // for both because it says the same thing about the row: nothing in it can answer.
            return node.dimmed
                ? treeIcon(
                    `${PROJECT_ICONS[extensionOf(node.resourceUri)] ?? 'project'}-dim`,
                    extensionUri
                )
                : languageIcon(node.resourceUri, extensionUri);
        case 'projectUnloaded':
            // Nothing about an unloaded project is live, and a full-colour icon says otherwise.
            return treeIcon(
                `${PROJECT_ICONS[extensionOf(node.resourceUri)] ?? 'project'}-dim`,
                extensionUri
            );
        // A graph of nodes, the way ReSharper and Rider draw it — Dependencies is the
        // relationships between things, not a list of references.
        case 'dependencies':
        case 'dependenciesNetFx':
            return treeIcon('dependencies', extensionUri);
        case 'imports':
        case 'import':
            return tinted('file-symlink-file', 'charts.blue');
        case 'framework':
            return tinted('layers', 'charts.blue');
        case 'packages':
            return treeIcon('packages-folder', extensionUri);
        case 'package':
            return treeIcon('package', extensionUri);
        case 'transitive':
        case 'transitivePackage':
            // The same cube, faint and dashed: these are packages too, just not ones the project
            // file names — so not something to right-click and uninstall.
            return treeIcon('package-transitive', extensionUri);
        case 'projects':
            return treeIcon('projects-folder', extensionUri);
        case 'assemblies':
        case 'assembly':
            return tinted('library', 'charts.blue');
        case 'analyzers':
        case 'analyzer':
            return tinted('circuit-board', 'charts.blue');
        case 'generator':
            return tinted('wand', 'charts.purple');
        // Generated output is a file the user never wrote, and telling it apart from one they
        // did is the whole point of showing it separately.
        case 'generatedFile':
            return treeIcon('generated-code', extensionUri);
        case 'file':
        case 'solutionItem':
            return fileIcon(node.resourceUri, extensionUri, fileIconsFromTheme);
        // A schedule is a clock, and the section and its projects carry the same one so the
        // branch reads as one thing at every level.
        case 'cronJobs':
        case 'cronProject':
            return tinted('watch', 'charts.purple');
        // The schema's own badge, the same one a `.proto` file row carries — the section, the
        // package and the service are all the one contract seen at different depths, and no
        // codicon says "protobuf".
        case 'protoServices':
        case 'protoPackage':
            return badgeUri('proto', extensionUri);
        case 'protoService':
            return tinted('symbol-interface', 'charts.green');
        // A method on that contract, marked the way a method is marked everywhere else in VS Code.
        case 'protoRpc':
            return tinted('symbol-method', 'charts.green');
        // A route is a path, and the globe is what VS Code itself puts beside one.
        case 'routes':
        case 'routeProject':
            return tinted('globe', 'charts.blue');
        // A shared prefix is a segment of a path, not a path — the same distinction the tree is
        // drawing, so it gets its own glyph rather than a second globe.
        case 'routeGroup':
            return tinted('symbol-namespace', 'charts.blue');
        case 'route':
            return tinted('symbol-event', 'charts.blue');
        // A screen, and the section and the application above it carry the same glyph so the
        // branch reads as one thing at every level — the way the schedules section does. A window
        // divided into regions rather than an empty one: a glyph that is mostly outline disappears
        // at sixteen pixels against a dark theme, which is what the first attempt here did.
        case 'templates':
        case 'templateRoot':
        case 'templateEntry':
            return tinted('layout', 'charts.red');
        // What renders one, which is a part of a screen rather than a screen: a different glyph,
        // because the row underneath a screen is a different kind of thing from the row above it.
        case 'templateModule':
            return tinted('layout-panel', 'charts.red');
        default:
            // An unknown kind is still a row, and a row still needs its slot filled.
            return node.hasChildren
                ? treeIcon('folder', extensionUri)
                : node.resourceUri
                  ? fileIcon(node.resourceUri, extensionUri, fileIconsFromTheme)
                  : tinted('circle-outline', 'descriptionForeground');
    }
}
