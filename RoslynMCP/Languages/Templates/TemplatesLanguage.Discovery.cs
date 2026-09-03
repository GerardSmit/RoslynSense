using System.Collections.Immutable;
using System.Globalization;
using RoslynMCP.Languages.Templates.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;

using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Templates;

/// <summary>
/// The <b>Templates</b> section of the Discovery view: the screens an application declares, as the
/// tree they are declared as.
/// </summary>
/// <remarks>
/// <para>
/// Every row answers the two questions a declared screen raises, and they are the same two at
/// every depth: where is this written, and what renders it. The click and the Definition button
/// open the declaration; the Implementation button opens the control the declaration names. The
/// two are in different languages in different projects and nothing but a string joins them, which
/// is why the section is worth having at all.
/// </para>
/// <para>
/// The section is drawn on the root listing, which happens every time the view becomes visible and
/// must therefore evaluate no project and read no file. So the decision to show it comes from
/// <see cref="TemplateRoots"/>, which asks only whether the folder exists.
/// </para>
/// </remarks>
internal sealed partial class TemplatesLanguage : ILanguageDiscoveryContributor
{
    /// <summary>The section, and the prefix of everything under it.</summary>
    private const string Prefix = "templates:";

    /// <summary>One application's templates, when the solution holds more than one set.</summary>
    private const string RootPrefix = Prefix + "r|";

    /// <summary>One entry of the tree.</summary>
    private const string EntryPrefix = Prefix + "e|";

    /// <summary>One module an entry hosts, listed only when it hosts more than one.</summary>
    private const string ModulePrefix = Prefix + "m|";

    public string NodeIdPrefix => Prefix;

    public Task<SolutionTreeNode?> SectionAsync(string solutionPath, CancellationToken ct)
    {
        if (!Settings.Enabled || Roots().Length == 0)
            return Task.FromResult<SolutionTreeNode?>(null);

        return Task.FromResult<SolutionTreeNode?>(new SolutionTreeNode(
            Id: Prefix + solutionPath,
            Kind: SolutionNodeKind.Templates,
            Label: "Templates",
            Description: null,
            ResourceUri: null,
            HasChildren: true,
            ContextValue: SolutionNodeKind.Templates));
    }

    public Task<SolutionTreeNode[]> ChildrenAsync(
        string nodeId, SolutionTreeParams p, CancellationToken ct)
    {
        if (!Settings.Enabled)
            return Task.FromResult<SolutionTreeNode[]>([]);

        // A module is a leaf, so a request for its children is a client that has lost its place
        // rather than a question. Answering with the top of the tree would fill the row underneath
        // it with the whole section.
        if (nodeId.StartsWith(ModulePrefix, StringComparison.Ordinal))
            return Task.FromResult<SolutionTreeNode[]>([]);

        if (nodeId.StartsWith(EntryPrefix, StringComparison.Ordinal))
        {
            string rest = nodeId[EntryPrefix.Length..];
            int split = rest.IndexOf('|', StringComparison.Ordinal);

            return Task.FromResult(
                split < 0 ? [] : Under(rest[..split], rest[(split + 1)..], ct));
        }

        if (nodeId.StartsWith(RootPrefix, StringComparison.Ordinal))
            return Task.FromResult(TopOf(nodeId[RootPrefix.Length..], ct));

        return Task.FromResult(SectionChildren(ct));
    }

    /// <summary>
    /// What the section itself holds: the tree, or one row per application when there is more than
    /// one.
    /// </summary>
    /// <remarks>
    /// The intermediate row is skipped for a solution with a single set of templates, for the same
    /// reason a single-module entry does not grow a row naming the module: a level of the tree that
    /// never offers a choice is a click that tells the reader nothing. Most solutions have one.
    /// </remarks>
    private SolutionTreeNode[] SectionChildren(CancellationToken ct)
    {
        var roots = Roots();

        if (roots.Length == 1)
            return TopOf(roots[0].ContentRoot, ct);

        var rows = new List<SolutionTreeNode>(roots.Length);

        foreach (var root in roots.OrderBy(root => root.ProjectName, StringComparer.OrdinalIgnoreCase))
        {
            var set = Read(root, ct);

            rows.Add(new SolutionTreeNode(
                Id: RootPrefix + root.ContentRoot,
                Kind: SolutionNodeKind.TemplateRoot,
                Label: root.ProjectName,
                Description: Pages(set.Count),
                ResourceUri: LspConverters.PathToUri(root.ProjectPath),
                HasChildren: set.Roots.Length > 0,
                ContextValue: SolutionNodeKind.TemplateRoot));
        }

        return [.. rows];
    }

    /// <summary>The entries of one application that sit under nothing.</summary>
    private SolutionTreeNode[] TopOf(string contentRoot, CancellationToken ct)
    {
        if (Roots().FirstOrDefault(root =>
                root.ContentRoot.Equals(contentRoot, StringComparison.OrdinalIgnoreCase))
            is not { ContentRoot.Length: > 0 } found)
        {
            return [];
        }

        var set = Read(found, ct);

        return Rows(set, set.Roots, Settings.Locale);
    }

    /// <summary>What sits under one entry: the entries below it, then the modules it hosts.</summary>
    /// <remarks>
    /// Entries first and then modules, rather than one list. They are two different kinds of
    /// answer — where to go next, and what this screen is made of — and interleaving them by name
    /// would put the second in the middle of the first.
    /// </remarks>
    private SolutionTreeNode[] Under(string contentRoot, string key, CancellationToken ct)
    {
        if (Roots().FirstOrDefault(root =>
                root.ContentRoot.Equals(contentRoot, StringComparison.OrdinalIgnoreCase))
            is not { ContentRoot.Length: > 0 } found)
        {
            return [];
        }

        var set = Read(found, ct);

        if (set.Entry(key) is not { } entry)
            return [];

        return [.. Rows(set, set.Children(key), Settings.Locale), .. ModuleRows(set, entry)];
    }

    /// <summary>The entries of one level, in the order a reader would look for them.</summary>
    /// <remarks>
    /// By name rather than by the order the files declare them. The declaration order is the order
    /// changes were made in over some years, which is meaningful to the merge and to nobody
    /// reading the tree; a reader scanning a level of forty screens is looking for a word.
    /// </remarks>
    internal static SolutionTreeNode[] Rows(
        TemplateSet set, IReadOnlyList<TemplateEntry> entries, string? locale)
    {
        return
        [
            .. entries
                .Select(entry => Node(set, entry, locale))
                .OrderBy(row => row.Label, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// One entry, as a row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function from the merged set to the row, which is what makes the decisions in it —
    /// which name is shown, when a module gets a row of its own, where the two buttons point —
    /// checkable without a workspace or a solution behind them.
    /// </para>
    /// <para>
    /// An entry hosting exactly one module carries that module's implementation itself rather than
    /// growing a child row to hold it. The row would say the module's name and nothing else, and
    /// the reader would have to expand a screen to reach the only thing under it. Two or more is a
    /// choice, and a choice needs rows.
    /// </para>
    /// </remarks>
    internal static SolutionTreeNode Node(TemplateSet set, TemplateEntry entry, string? locale)
    {
        var children = set.Children(entry.Key);
        var implementation = entry.Modules.Length == 1
            ? Implementation(set, entry.Modules[0])
            : null;

        return new SolutionTreeNode(
            Id: $"{EntryPrefix}{set.ContentRoot}|{entry.Key}",
            Kind: SolutionNodeKind.TemplateEntry,
            Label: entry.Label(locale),
            Description: Detail(entry, children.Count),
            ResourceUri: LspConverters.PathToUri(entry.Site.FilePath),
            HasChildren: children.Count > 0 || entry.Modules.Length > 1,
            ContextValue: SolutionNodeKind.TemplateEntry
                + (implementation is not null ? SolutionNodeKind.SecondaryTargetSuffix : string.Empty),
            Tooltip: Tooltip(entry),
            GoTo: Navigation(entry.Site),
            GoToSecondary: implementation);
    }

    /// <summary>The modules an entry hosts, listed only when there is a choice between them.</summary>
    private static SolutionTreeNode[] ModuleRows(TemplateSet set, TemplateEntry entry)
    {
        if (entry.Modules.Length < 2)
            return [];

        var rows = new List<SolutionTreeNode>(entry.Modules.Length);

        for (int i = 0; i < entry.Modules.Length; i++)
            rows.Add(ModuleNode(set, entry, i));

        return [.. rows];
    }

    /// <summary>
    /// One hosted module, as a row.
    /// </summary>
    /// <remarks>
    /// In the order the entry hosts them rather than by name: the order is what decides which one
    /// renders first on the screen, so it is the one fact about a list of modules that means
    /// something.
    /// </remarks>
    internal static SolutionTreeNode ModuleNode(TemplateSet set, TemplateEntry entry, int index)
    {
        var use = entry.Modules[index];
        var module = set.Module(use.Type);
        var implementation = Implementation(set, use);

        // The module's own declaration when these files hold one, and where it is hosted when they
        // do not. A module the application already had is named here and declared somewhere this
        // pack cannot see; the line that names it is then the only place to go, and it is a better
        // answer than a row that does nothing.
        var declaration = module?.Site ?? use.Site;

        return new SolutionTreeNode(
            Id: string.Create(
                CultureInfo.InvariantCulture,
                $"{ModulePrefix}{set.ContentRoot}|{entry.Key}|{index}"),
            Kind: SolutionNodeKind.TemplateModule,
            Label: module?.Name ?? use.Type,
            Description: null,
            ResourceUri: LspConverters.PathToUri(declaration.FilePath),
            HasChildren: false,
            ContextValue: SolutionNodeKind.TemplateModule
                + (implementation is not null ? SolutionNodeKind.SecondaryTargetSuffix : string.Empty),
            Tooltip: use.Type,
            GoTo: Navigation(declaration),
            GoToSecondary: implementation);
    }

    /// <summary>
    /// Where the thing that renders a module lives.
    /// </summary>
    /// <remarks>
    /// The view control's file; the file the module's own folder holds when the templates declare
    /// no control; and the module's declaration when neither is there. Falling back rather than
    /// giving up is what keeps the button useful on a module whose control ships inside a package:
    /// the path names something this checkout does not contain, and the next best answer to "what
    /// renders this" is the line that says which control does.
    /// </remarks>
    private static SolutionTreeNavigation? Implementation(TemplateSet set, TemplateModuleUse use)
    {
        var module = set.Module(use.Type);

        if (module?.View is { } control && set.Resolve(control.Path) is { Length: > 0 } declared)
            return new SolutionTreeNavigation(LspConverters.PathToUri(declared), Whole);

        // Nothing declared, or declared and not there: the folder named after the module is the
        // next place to look, and it is where the quarter of them these files never declared live.
        if (set.Control(use.Type) is { Length: > 0 } conventional)
            return new SolutionTreeNavigation(LspConverters.PathToUri(conventional), Whole);

        return module is { Controls.IsEmpty: false } ? Navigation(module.Site) : null;
    }

    /// <summary>
    /// What the row says beside its name.
    /// </summary>
    /// <remarks>
    /// The module it hosts when it hosts one, which is the fact that distinguishes two screens
    /// with similar names; how many when it hosts several; and how many screens are under it when
    /// it is a heading rather than a screen. In the dimmed column on the right, where the tree
    /// puts every other secondary fact.
    /// </remarks>
    private static string? Detail(TemplateEntry entry, int children) =>
        entry.Modules switch
        {
            [var only] => only.Type,
            { Length: > 1 } many => $"{many.Length} modules",
            _ => Pages(children),
        };

    private static string? Pages(int count) => count switch
    {
        0 => null,
        1 => "1 page",
        _ => $"{count} pages",
    };

    /// <summary>The key and where it comes from, for the hover.</summary>
    /// <remarks>
    /// The key is on the hover rather than on the row because the row shows a name in the
    /// customer's language and the key is what the files and the code use — so it is what somebody
    /// searches for the moment they need to change anything, and what a row shows on screen should
    /// still be the words they were sent.
    /// </remarks>
    private static string Tooltip(TemplateEntry entry) =>
        $"{entry.Key} — {Path.GetFileName(entry.Site.FilePath)}:{entry.Site.Range.Start.Line + 1}";

    private static SolutionTreeNavigation Navigation(TemplateSite site) =>
        new(LspConverters.PathToUri(site.FilePath), site.Range);

    /// <summary>The top of a file, for a target that is a whole file rather than a line in one.</summary>
    private static Range Whole { get; } = new(new Position(0, 0), new Position(0, 0));

    private ImmutableArray<TemplateRoot> Roots() =>
        TemplateRoots.Of(SolutionProjectIndex.Projects(), Settings.Folders);

    /// <summary>
    /// The merged templates of one root, saying so once when a file in it could not be read.
    /// </summary>
    /// <remarks>
    /// A file with a tab where YAML wants spaces costs its own declarations and nothing else, so
    /// the tree still draws — which is right, and which also means nobody would ever find out. The
    /// log is where that goes: keyed on the root, so a folder with three bad files says so once
    /// per parse rather than once per expand.
    /// </remarks>
    private TemplateSet Read(TemplateRoot root, CancellationToken ct)
    {
        var set = Templates.Of(root, ct);

        if (!set.Errors.IsEmpty)
        {
            ServiceLog.Warn(
                $"Some template files under {root.ContentRoot} could not be read: "
                + string.Join("; ", set.Errors),
                key: $"templates:{root.ContentRoot}");
        }

        return set;
    }
}
