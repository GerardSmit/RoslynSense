using Microsoft.Language.Xml;

namespace RoslynMCP.Services;

/// <summary>
/// Determines which profiled frames belong to the user's own solution, so profiling output can
/// hide framework and third-party methods (System.*, SQL client internals, DNN, …) by default.
/// </summary>
/// <remarks>
/// Prefixes come from every project in the nearest solution: AssemblyName, RootNamespace, and
/// their first namespace segment. The first segment is what makes this practical — a project
/// named <c>Company.Product.Web</c> marks all <c>Company.*</c> frames as own code, which matches
/// how real solutions spread namespaces across projects.
/// </remarks>
public static class CodeScope
{
    /// <summary>
    /// Own-code prefixes for a project: the project itself plus every sibling in the nearest
    /// solution. Empty when nothing could be determined — treat that as "no filtering".
    /// </summary>
    public static IReadOnlyList<string> OwnPrefixesForProject(string csprojPath)
    {
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);

        var solution = PathHelper.FindNearestSolution(csprojPath);
        List<string> projects = solution is not null
            ? PathHelper.GetProjectsFromSolution(solution)
            : [];

        if (!projects.Contains(csprojPath, StringComparer.OrdinalIgnoreCase))
            projects.Add(csprojPath);

        foreach (var project in projects)
            AddProjectPrefixes(project, prefixes);

        return [.. prefixes];
    }

    /// <summary>
    /// Own-code prefixes for the solution nearest to a directory. Used when only a PID is known
    /// (ProfileProcess) and the server's working directory identifies the codebase.
    /// </summary>
    public static IReadOnlyList<string> OwnPrefixesForDirectory(string directory)
    {
        var solution = PathHelper.FindNearestSolution(directory);
        if (solution is null)
            return [];

        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in PathHelper.GetProjectsFromSolution(solution))
            AddProjectPrefixes(project, prefixes);

        return [.. prefixes];
    }

    /// <summary>
    /// Whether a profiled frame belongs to own code. Frames may carry a module prefix
    /// ("Assembly!Namespace.Type.Method"); both sides are checked.
    /// </summary>
    public static bool IsOwn(string frameName, IReadOnlyList<string> prefixes)
    {
        var bang = frameName.IndexOf('!');
        var method = bang >= 0 ? frameName[(bang + 1)..] : frameName;
        var module = bang >= 0 ? frameName[..bang] : null;

        foreach (var prefix in prefixes)
        {
            if (MatchesPrefix(method, prefix))
                return true;
            if (module is not null && MatchesPrefix(module, prefix))
                return true;
        }

        return false;
    }

    /// <summary>Splits methods into (own, hiddenCount) preserving order.</summary>
    public static (List<SpeedscopeParser.MethodProfile> Own, int Hidden) FilterOwn(
        IReadOnlyList<SpeedscopeParser.MethodProfile> methods, IReadOnlyList<string> prefixes)
    {
        var own = new List<SpeedscopeParser.MethodProfile>();
        foreach (var method in methods)
        {
            if (IsOwn(method.FullName, prefixes))
                own.Add(method);
        }

        return (own, methods.Count - own.Count);
    }

    private static bool MatchesPrefix(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        if (name.Length == prefix.Length)
            return true;

        // Must end on a namespace/type boundary, so "App" does not match "Apple.Pie".
        var next = name[prefix.Length];
        return next is '.' or '+' or '(' or '`';
    }

    private static void AddProjectPrefixes(string csprojPath, SortedSet<string> prefixes)
    {
        var names = new List<string>();

        try
        {
            var document = Parser.ParseText(File.ReadAllText(csprojPath));

            foreach (var element in document.DescendantNodes().OfType<XmlElementBaseSyntax>())
            {
                if (element.NameNode?.LocalName is "AssemblyName" or "RootNamespace" &&
                    !string.IsNullOrWhiteSpace(element.Value))
                {
                    names.Add(element.Value.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable project file; the filename below still yields a usable prefix. A
            // malformed one needs no catch — the parse is error-tolerant and simply finds less.
        }

        // The file name is a namespace hint in its own right — a project can compile into an
        // assembly named differently (Storefront.Website.csproj → Legacy.Modules.dll) while
        // code still lives in namespaces matching either name.
        names.Add(Path.GetFileNameWithoutExtension(csprojPath));

        foreach (var name in names)
        {
            prefixes.Add(name);

            var firstDot = name.IndexOf('.');
            if (firstDot > 0)
                prefixes.Add(name[..firstDot]);
        }
    }
}
