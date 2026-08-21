using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Navigates to the definition of a symbol by fully-qualified name,
/// without requiring a code snippet or source file that references it.
/// </summary>
[McpServerToolType]
public static class GoToDefinitionTool
{
    [McpServerTool, Description(
        "Go to the definition of a type, method, property, field, or event by its fully-qualified name. " +
        "Examples: 'System.String', 'System.String.Contains', 'MyApp.Services.UserService.GetUser'. " +
        "For members, use 'TypeName.MemberName'. Returns source context or auto-decompiled source. " +
        "Use GoToDefinitionSnippet when you have a code snippet with [| |] markers instead.")]
    public static async Task<string> GoToDefinition(
        [Description("Path to any file in the project (used to determine which project/compilation to search).")]
        string filePath,
        [Description(
            "Fully-qualified symbol name. For types: 'System.String', 'MyApp.Models.User'. " +
            "For members: 'System.String.Contains', 'MyApp.Models.User.Name'. " +
            "For nested types: 'MyApp.Outer+Inner'. Generic types use backtick arity: 'System.Collections.Generic.List`1'.")]
        string symbolName,
        IOutputFormatter fmt,
        [Description("Number of lines of context to show around the definition. Default: 5.")]
        int contextLines = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(symbolName))
                return "Error: symbolName cannot be empty.";

            var errors = new StringBuilder();
            var fileCtx = await ToolHelper.ResolveFileAsync(filePath, errors, cancellationToken);
            if (fileCtx is null)
                return errors.ToString();

            var compilation = await fileCtx.Project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                return "Error: could not obtain compilation for project.";

            var symbol = ResolveSymbol(compilation, symbolName);
            if (symbol is null)
                return BuildNotFoundError(compilation, symbolName, cancellationToken);

            return await GoToDefinitionSnippetTool.FormatDefinitionAsync(
                symbol, fileCtx.Project, contextLines, fmt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GoToDefinition] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static ISymbol? ResolveSymbol(Compilation compilation, string symbolName)
    {
        // Try as a fully-qualified type first
        var type = compilation.GetTypeByMetadataName(symbolName);
        if (type is not null)
            return type;

        // Try splitting off the last segment as a member name
        int lastDot = symbolName.LastIndexOf('.');
        if (lastDot > 0)
        {
            string typePart = symbolName[..lastDot];
            string memberName = symbolName[(lastDot + 1)..];

            type = compilation.GetTypeByMetadataName(typePart);
            if (type is not null && PickMember(type, memberName) is { } member)
                return member;
        }

        // Lenient walk: tolerates a missing backtick arity ('List' for 'List`1'),
        // nested types written with '.' instead of '+', and casing differences.
        string[] segments = symbolName.Split('.', '+');
        if (segments.Any(string.IsNullOrWhiteSpace))
            return null;

        return Walk(compilation.GlobalNamespace, segments, 0);
    }

    private static ISymbol? Walk(INamespaceOrTypeSymbol scope, string[] segments, int index)
    {
        string segment = segments[index];
        bool last = index == segments.Length - 1;

        if (scope is INamespaceSymbol ns)
        {
            foreach (var child in ns.GetNamespaceMembers())
            {
                if (!string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!last && Walk(child, segments, index + 1) is { } viaNamespace)
                    return viaNamespace;
            }
        }

        foreach (var type in MatchTypes(scope, segment))
        {
            if (last)
                return type;
            if (Walk(type, segments, index + 1) is { } viaType)
                return viaType;
        }

        if (last && scope is INamedTypeSymbol containing)
            return PickMember(containing, segment);

        return null;
    }

    /// <summary>
    /// Types in <paramref name="scope"/> matching <paramref name="segment"/>. Without an
    /// explicit backtick arity every arity matches, lowest first, so 'List' finds 'List`1'.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> MatchTypes(INamespaceOrTypeSymbol scope, string segment)
    {
        int backtick = segment.IndexOf('`');
        if (backtick > 0 && int.TryParse(segment[(backtick + 1)..], out int arity))
        {
            return scope.GetTypeMembers(segment[..backtick], arity)
                .Concat(ByNameIgnoreCase(segment[..backtick]).Where(t => t.Arity == arity))
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<INamedTypeSymbol>();
        }

        return scope.GetTypeMembers(segment)
            .Concat(ByNameIgnoreCase(segment))
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<INamedTypeSymbol>()
            .OrderBy(t => t.Arity);

        IEnumerable<INamedTypeSymbol> ByNameIgnoreCase(string name) =>
            scope.GetTypeMembers().Where(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static ISymbol? PickMember(INamedTypeSymbol type, string memberName)
    {
        var members = type.GetMembers(memberName);
        if (members.Length == 0)
            members = [.. type.GetMembers().Where(m =>
                string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase))];

        if (members.Length == 1)
            return members[0];

        // Prefer non-accessor, non-implicit members
        return members.FirstOrDefault(m => m is not IMethodSymbol { AssociatedSymbol: not null }
                                            && !m.IsImplicitlyDeclared)
               ?? members.FirstOrDefault();
    }

    /// <summary>
    /// Not-found error with "did you mean" candidates, so a stale or misspelled name (a
    /// type renamed upstream, a wrong word in a long name) is a one-retry fix instead of
    /// a manual hunt through the referenced assemblies.
    /// </summary>
    private static string BuildNotFoundError(
        Compilation compilation, string symbolName, CancellationToken cancellationToken)
    {
        var suggestions = new List<string>();

        int lastDot = symbolName.LastIndexOf('.');
        string lastSegment = lastDot >= 0 ? symbolName[(lastDot + 1)..] : symbolName;
        string? namespaceHint = lastDot > 0 ? symbolName[..lastDot] : null;

        // The last segment may be a misspelled member of a type that does resolve.
        if (namespaceHint is not null &&
            ResolveSymbol(compilation, namespaceHint) is INamedTypeSymbol containingType)
        {
            string typeName = SymbolNameSuggester.GetMetadataQualifiedName(containingType);
            suggestions.AddRange(SymbolNameSuggester
                .SuggestMembers(containingType, lastSegment)
                .Select(m => $"{typeName}.{m}"));
        }

        suggestions.AddRange(SymbolNameSuggester.SuggestTypes(
            compilation, lastSegment, namespaceHint, cancellationToken));

        var error = new StringBuilder();
        error.Append($"Error: symbol '{symbolName}' not found.");

        if (suggestions.Count > 0)
        {
            error.AppendLine(" Did you mean:");
            foreach (string suggestion in suggestions.Distinct().Take(10))
                error.AppendLine($"- {suggestion}");
        }
        else
        {
            error.AppendLine();
        }

        error.Append("Use fully-qualified names (e.g. 'System.String', 'MyApp.Models.User.Name'). " +
                     "For generic types use backtick arity (e.g. 'System.Collections.Generic.List`1').");
        return error.ToString();
    }
}
