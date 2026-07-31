using System.Text.RegularExpressions;

namespace RoslynMCP.Services.ProjectModel;

/// <summary>A file and the files nested under it.</summary>
public sealed record NestedFile(string FullPath, IReadOnlyList<NestedFile> Children);

/// <summary>
/// Decides which files nest under which, the way Visual Studio and Rider display them.
///
/// Two sources, explicit first: <c>DependentUpon</c> metadata from the project's item model —
/// which is how WinForms (<c>Form1.cs</c> ← <c>Form1.Designer.cs</c>, <c>Form1.resx</c>) and
/// WebForms express it — then a rule set for SDK-style projects that rely on convention alone.
/// </summary>
public static partial class FileNestingService
{
    /// <summary>Suffix rules: a file matching the pattern nests under the sibling named by the
    /// captured stem plus the parent extension.</summary>
    private static readonly (Regex Pattern, string ParentExtension)[] s_rules =
    [
        (SuffixRule(@"\.designer\.cs"), ".cs"),
        (SuffixRule(@"\.g\.cs"), ".cs"),
        (SuffixRule(@"\.generated\.cs"), ".cs"),
        (SuffixRule(@"\.razor\.cs"), ".razor"),
        (SuffixRule(@"\.razor\.css"), ".razor"),
        (SuffixRule(@"\.cshtml\.cs"), ".cshtml"),
        (SuffixRule(@"\.aspx\.cs"), ".aspx"),
        (SuffixRule(@"\.aspx\.designer\.cs"), ".aspx"),
        (SuffixRule(@"\.ascx\.cs"), ".ascx"),
        (SuffixRule(@"\.xaml\.cs"), ".xaml"),
        (SuffixRule(@"\.js"), ".ts"),
        (SuffixRule(@"\.js\.map"), ".ts"),
        (SuffixRule(@"\.d\.ts"), ".ts"),
    ];

    /// <summary>Dotted-segment rules: <c>appsettings.Development.json</c> under
    /// <c>appsettings.json</c>.</summary>
    private static readonly string[] s_dottedVariantExtensions = [".json", ".resx", ".config", ".xml"];

    /// <summary>Exact pairs that share no naming pattern.</summary>
    private static readonly Dictionary<string, string> s_exactPairs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["package-lock.json"] = "package.json",
        ["Directory.Build.targets"] = "Directory.Build.props",
        ["Directory.Packages.props"] = "Directory.Build.props",
    };

    /// <summary>
    /// Groups <paramref name="files"/> into a nested tree. Files whose parent is not present
    /// stay at the top level rather than disappearing — an orphaned child is far more
    /// confusing than an un-nested one.
    /// </summary>
    public static IReadOnlyList<NestedFile> Nest(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string>? dependentUpon = null,
        bool enabled = true)
    {
        if (!enabled || files.Count == 0)
            return files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(f => new NestedFile(f, []))
                .ToList();

        var byName = files
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);

            // Explicit metadata wins: the project said so.
            if (dependentUpon is not null &&
                dependentUpon.TryGetValue(file, out string? declared) &&
                Resolve(declared, file, byName) is { } declaredParent &&
                !PathsEqual(declaredParent, file))
            {
                parentOf[file] = declaredParent;
                continue;
            }

            if (InferParentName(name) is { } inferred &&
                byName.TryGetValue(inferred, out string? parent) &&
                !PathsEqual(parent, file))
            {
                parentOf[file] = parent;
            }
        }

        // A chain (a → b → c) collapses to one level: nesting depth beyond one is noise.
        foreach (string child in parentOf.Keys.ToList())
        {
            string parent = parentOf[child];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { child };
            while (parentOf.TryGetValue(parent, out string? grandparent) && seen.Add(parent))
                parent = grandparent;
            parentOf[child] = parent;
        }

        var children = parentOf
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(pair => pair.Key).ToList(), StringComparer.OrdinalIgnoreCase);

        return files
            .Where(f => !parentOf.ContainsKey(f))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(f => new NestedFile(
                f,
                (children.TryGetValue(f, out var kids) ? kids : [])
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Select(k => new NestedFile(k, []))
                    .ToList()))
            .ToList();
    }

    /// <summary>The file name this one should nest under, or null.</summary>
    internal static string? InferParentName(string fileName)
    {
        if (s_exactPairs.TryGetValue(fileName, out string? exact))
            return exact;

        foreach (var (pattern, parentExtension) in s_rules)
        {
            var match = pattern.Match(fileName);
            if (match.Success)
                return match.Groups["stem"].Value + parentExtension;
        }

        // appsettings.Development.json → appsettings.json
        string extension = Path.GetExtension(fileName);
        if (s_dottedVariantExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            int dot = stem.IndexOf('.');
            if (dot > 0)
                return stem[..dot] + extension;
        }

        return null;
    }

    private static string? Resolve(
        string declared, string child, IReadOnlyDictionary<string, string> byName)
    {
        // DependentUpon is usually a bare file name, occasionally a relative path.
        string name = Path.GetFileName(declared);
        if (byName.TryGetValue(name, out string? match))
            return match;

        string? directory = Path.GetDirectoryName(child);
        if (directory is null)
            return null;

        string candidate = Path.GetFullPath(Path.Combine(directory, declared));
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static Regex SuffixRule(string suffix) =>
        new($"^(?<stem>.+?){suffix}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
