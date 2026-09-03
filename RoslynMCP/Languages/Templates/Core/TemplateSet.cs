using System.Collections.Immutable;

namespace RoslynMCP.Languages.Templates.Core;

/// <summary>
/// Every template file of one application, merged the way the application merges them.
/// </summary>
/// <remarks>
/// <para>
/// A folder of two hundred files is one document. An entry is declared in the file that
/// introduces it and added to by every later file that names it again — which is what makes a
/// folder like this maintainable, and what makes a single file a misleading thing to read on its
/// own. The tree has to show the merged answer or it shows something that exists nowhere.
/// </para>
/// <para>
/// First declaration wins, later ones add. That is the merge the application performs: a name, a
/// parent and a position come from the file that introduced the entry, and the modules it hosts
/// accumulate. So <see cref="TemplateEntry.Site"/> is where somebody should be sent when they ask
/// where an entry comes from, even though four other files mention it.
/// </para>
/// </remarks>
internal sealed class TemplateSet
{
    /// <summary>How far above the content root a control path is looked for. See <see cref="Resolve"/>.</summary>
    private const int Ancestors = 3;

    private readonly Dictionary<string, TemplateEntry> _byKey;
    private readonly Dictionary<string, List<TemplateEntry>> _children;
    private readonly Dictionary<string, TemplateModule> _modules;
    private readonly TemplateControls _controls;

    private TemplateSet(
        string contentRoot,
        Dictionary<string, TemplateEntry> byKey,
        Dictionary<string, List<TemplateEntry>> children,
        ImmutableArray<TemplateEntry> roots,
        Dictionary<string, TemplateModule> modules,
        ImmutableArray<string> errors,
        ImmutableArray<string> controlFolders)
    {
        ContentRoot = contentRoot;
        _byKey = byKey;
        _children = children;
        _modules = modules;
        _controls = new TemplateControls(contentRoot, controlFolders);
        Roots = roots;
        Errors = errors;
    }

    public static TemplateSet Empty { get; } = new(
        string.Empty,
        new Dictionary<string, TemplateEntry>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, List<TemplateEntry>>(StringComparer.OrdinalIgnoreCase),
        [],
        new Dictionary<string, TemplateModule>(StringComparer.OrdinalIgnoreCase),
        [],
        []);

    /// <summary>The directory a control path is written relative to.</summary>
    public string ContentRoot { get; }

    /// <summary>The entries with no parent, or whose parent is not declared anywhere.</summary>
    /// <remarks>
    /// An entry naming a parent that no file declares is shown at the top rather than dropped. It
    /// is a broken reference and the tree is where somebody would notice it; hiding the row would
    /// hide the only evidence.
    /// </remarks>
    public ImmutableArray<TemplateEntry> Roots { get; }

    /// <summary>One line per file that could not be read, with the reason.</summary>
    public ImmutableArray<string> Errors { get; }

    public int Count => _byKey.Count;

    public TemplateEntry? Entry(string key) =>
        _byKey.TryGetValue(key, out var entry) ? entry : null;

    public IReadOnlyList<TemplateEntry> Children(string key) =>
        _children.TryGetValue(key, out var children) ? children : [];

    /// <summary>
    /// The module an entry hosts, by the name it was registered under.
    /// </summary>
    /// <remarks>
    /// A hosted module is named either by its own key or by the package it ships in and then its
    /// key — <c>Shop.OrderList</c> for the <c>OrderList</c> a <c>Shop</c> package installed. The
    /// qualified form names a module the application already had rather than one these files
    /// declare, so it usually resolves to nothing; the last segment is tried anyway, because when
    /// it does match it matches the right thing and the alternative is a dead button.
    /// </remarks>
    public TemplateModule? Module(string type)
    {
        if (_modules.TryGetValue(type, out var module))
            return module;

        int dot = type.LastIndexOf('.');

        return dot >= 0 && dot < type.Length - 1 && _modules.TryGetValue(type[(dot + 1)..], out module)
            ? module
            : null;
    }

    /// <summary>
    /// A control's path as a file on disk, or null when nothing is there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path is written relative to the root the application serves from, which is where the
    /// template folder lives — so the content root is tried first. The ancestors above it are
    /// tried next, for the layout where the templates are deployed from one project into a web
    /// root beside it; three levels rather than every level, because the point where a wrong file
    /// with the right relative path becomes likely is not far up.
    /// </para>
    /// <para>
    /// A path that climbs out of the directory it is resolved against is refused rather than
    /// followed. These files are read from a workspace and are not always the reader's own.
    /// </para>
    /// </remarks>
    public string? Resolve(string? relativePath)
    {
        if (relativePath is not { Length: > 0 } written || ContentRoot.Length == 0)
            return null;

        string relative = written
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (relative.Length == 0 || Path.IsPathRooted(relative))
            return null;

        string? directory = ContentRoot;

        for (int level = 0; level <= Ancestors && directory is { Length: > 0 }; level++)
        {
            string candidate = Path.GetFullPath(Path.Combine(directory, relative));

            if (candidate.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>
    /// The control file of a module these files never declared, found by the folder it lives in.
    /// </summary>
    public string? Control(string moduleType) => _controls.Find(moduleType);

    /// <summary>Merges what the files declared, in the order the application would read them.</summary>
    public static TemplateSet Build(
        string contentRoot,
        IEnumerable<TemplateDocument> documents,
        ImmutableArray<string> controlFolders = default)
    {
        var byKey = new Dictionary<string, TemplateEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var modules = new Dictionary<string, TemplateModule>(StringComparer.OrdinalIgnoreCase);
        var errors = ImmutableArray.CreateBuilder<string>();

        foreach (var document in documents)
        {
            if (document.Error is { Length: > 0 } error)
            {
                errors.Add($"{Path.GetFileName(document.FilePath)}: {error}");
                continue;
            }

            foreach (var entry in document.Entries)
            {
                if (byKey.TryGetValue(entry.Key, out var known))
                    byKey[entry.Key] = Merge(known, entry);
                else
                {
                    byKey[entry.Key] = entry;
                    order.Add(entry.Key);
                }
            }

            foreach (var module in document.Modules)
            {
                if (modules.TryGetValue(module.Key, out var known))
                    modules[module.Key] = Merge(known, module);
                else
                    modules[module.Key] = module;
            }
        }

        var children = new Dictionary<string, List<TemplateEntry>>(StringComparer.OrdinalIgnoreCase);
        var roots = ImmutableArray.CreateBuilder<TemplateEntry>();

        foreach (string key in order)
        {
            var entry = byKey[key];

            if (entry.Parent is { Length: > 0 } parent
                && byKey.ContainsKey(parent)
                && !Cycles(byKey, entry))
            {
                if (!children.TryGetValue(parent, out var siblings))
                    children[parent] = siblings = [];

                siblings.Add(entry);
            }
            else
            {
                roots.Add(entry);
            }
        }

        return new TemplateSet(
            contentRoot,
            byKey,
            children,
            roots.ToImmutable(),
            modules,
            errors.ToImmutable(),
            controlFolders.IsDefault ? [] : controlFolders);
    }

    /// <summary>
    /// Whether following an entry's parents comes back to where it started.
    /// </summary>
    /// <remarks>
    /// Two entries naming each other is a mistake somebody will make, and it is the kind that
    /// costs the whole view: a tree built from it either never terminates while expanding or
    /// silently loses every entry in the loop. Broken here rather than while drawing, so the
    /// entries in the cycle come out at the top where they can be seen.
    /// </remarks>
    private static bool Cycles(Dictionary<string, TemplateEntry> byKey, TemplateEntry entry)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Key };
        var walk = entry;

        while (walk.Parent is { Length: > 0 } parent && byKey.TryGetValue(parent, out var next))
        {
            if (!seen.Add(parent))
                return true;

            walk = next;
        }

        return false;
    }

    private static TemplateEntry Merge(TemplateEntry first, TemplateEntry later) =>
        first with
        {
            Parent = first.Parent ?? later.Parent,
            Names =
            [
                .. first.Names,
                .. later.Names.Where(name =>
                    !first.Names.Any(known =>
                        known.Tag.Equals(name.Tag, StringComparison.OrdinalIgnoreCase))),
            ],
            Modules = [.. first.Modules, .. later.Modules],
        };

    private static TemplateModule Merge(TemplateModule first, TemplateModule later) =>
        first with
        {
            Name = first.Name ?? later.Name,
            Controls =
            [
                .. first.Controls,
                .. later.Controls.Where(control =>
                    !first.Controls.Any(known =>
                        known.Name.Equals(control.Name, StringComparison.OrdinalIgnoreCase))),
            ],
        };
}
