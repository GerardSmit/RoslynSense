using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// textDocument/codeLens + codeLens/resolve. Two lens kinds per member:
/// - reference count — returned unresolved (Data only); codeLens/resolve runs the
///   workspace-wide FindReferencesAsync, so only lenses actually visible in the editor pay it
/// - "Run test" on xUnit/NUnit/MSTest methods — inline
/// (Inheritance relations are gutter markers via roslynSense/inheritanceMarkers, not lenses.)
/// Commands are client-side: roslynSense.runTest, roslynSense.showReferences
/// (see the VSCode extension).
/// </summary>
internal static class CodeLensHandler
{
    private const int MaxReferenceLocations = 100;

    private static readonly HashSet<string> s_testAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory",                       // xUnit
        "Test", "TestCase", "TestCaseSource",   // NUnit
        "TestMethod", "DataTestMethod",         // MSTest
    };

    public static async Task<LspCodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<LspCodeLens>();

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root is null)
            return Array.Empty<LspCodeLens>();

        string? projectPath = document.Project.FilePath;
        var lenses = new List<LspCodeLens>();

        foreach (var (declaration, identifier) in EnumerateMembers(root))
        {
            var range = LspConverters.ToRange(text.Lines, identifier);
            var identifierPosition = text.Lines.GetLinePosition(identifier.Start);

            // Reference count: deferred to codeLens/resolve.
            lenses.Add(new LspCodeLens(range, Command: null)
            {
                Data = new CodeLensData(p.TextDocument.Uri,
                    identifierPosition.Line, identifierPosition.Character, "references"),
            });

            if (declaration is MethodDeclarationSyntax method && IsTestMethod(method)
                && FullyQualifiedName(method) is { } fqn)
                lenses.Add(new LspCodeLens(range, new Command(
                    "▶ Run test", "roslynSense.runTest", [fqn, projectPath ?? ""])));
        }
        return lenses.ToArray();
    }

    /// <summary>codeLens/resolve: computes the reference count for one visible lens.</summary>
    public static async Task<LspCodeLens> ResolveAsync(LspCodeLens lens, CancellationToken ct)
    {
        if (lens.Data is not { Kind: "references" } data)
            return lens;

        // Zero-reference lenses still carry the showReferences command (with an empty
        // location list) — LSP requires a non-empty command id, and an empty peek is a
        // sane click result.
        var noReferences = new Command("0 references", "roslynSense.showReferences",
            [data.Uri, data.Line, data.Character, Array.Empty<LspLocation>()]);

        var resolved = await HandlerHelpers.ResolveAsync(
            new TextDocumentIdentifier(data.Uri), new Position(data.Line, data.Character), ct);
        if (resolved is not var (document, _, offset))
            return lens with { Command = noReferences };

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return lens with { Command = noReferences };

        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, ct);
        var locations = references
            .SelectMany(r => r.Locations)
            .Where(l => l.Location.IsInSource)
            .Select(l => LspConverters.ToLocation(l.Location))
            .Where(l => l is not null)
            .Select(l => l!)
            .Distinct()
            .ToArray();

        string title = locations.Length == 1 ? "1 reference" : $"{locations.Length} references";
        return lens with
        {
            Command = new Command(title, "roslynSense.showReferences",
                [data.Uri, data.Line, data.Character, locations.Take(MaxReferenceLocations).ToArray()]),
        };
    }

    private static IEnumerable<(SyntaxNode Declaration, TextSpan Identifier)> EnumerateMembers(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    yield return (type, type.Identifier.Span);
                    break;
                case MethodDeclarationSyntax method:
                    yield return (method, method.Identifier.Span);
                    break;
                case ConstructorDeclarationSyntax ctor:
                    yield return (ctor, ctor.Identifier.Span);
                    break;
                case PropertyDeclarationSyntax property:
                    yield return (property, property.Identifier.Span);
                    break;
                case EventDeclarationSyntax ev:
                    yield return (ev, ev.Identifier.Span);
                    break;
            }
        }
    }

    private static bool IsTestMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes)
            .Any(a => s_testAttributes.Contains(AttributeName(a)));

    private static string AttributeName(AttributeSyntax attribute)
    {
        string name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length] : name;
    }

    private static string? FullyQualifiedName(MethodDeclarationSyntax method)
    {
        var parts = new List<string> { method.Identifier.Text };
        for (var node = method.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case TypeDeclarationSyntax type:
                    parts.Add(type.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString());
                    break;
            }
        }
        if (parts.Count < 2)
            return null; // method outside a type — not a runnable test
        parts.Reverse();
        return string.Join(".", parts);
    }
}
