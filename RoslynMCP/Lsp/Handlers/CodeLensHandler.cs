using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// textDocument/codeLens + codeLens/resolve. Lens kinds per member:
/// - reference count — returned unresolved (Data only); codeLens/resolve runs the
///   workspace-wide FindReferencesAsync, so only lenses actually visible in the editor pay it
/// - "N tests" — how many tests the per-test coverage map says execute this member; inline,
///   because it is a lookup in an already-loaded file rather than a search
/// - "Run test" on xUnit/NUnit/MSTest methods — inline
/// (Inheritance relations are gutter markers via roslynSense/inheritanceMarkers, not lenses.)
/// Commands are client-side: roslynSense.runTest, roslynSense.showReferences,
/// roslynSense.showTestsAt (see the VSCode extension).
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

    public static async Task<LspCodeLens[]> CodeLensAsync(
        CodeLensParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<LspCodeLens>();

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        if (root is null || model is null)
            return Array.Empty<LspCodeLens>();

        string? projectPath = document.Project.FilePath;
        var lenses = new List<LspCodeLens>();

        // Read once for the whole document: the coverage map is a solution-wide file, and the
        // rows for this file are all any member in it can match against.
        var coverageRows = TestCoverageLenses.ForFile(document.FilePath);

        foreach (var (declaration, identifier) in EnumerateMembers(root))
        {
            var range = LspConverters.ToRange(text.Lines, identifier);
            var identifierPosition = text.Lines.GetLinePosition(identifier.Start);

            // "N tests" — which tests are known to execute this member. Absent entirely when
            // nothing covers it, rather than a lens per method reading "0 tests".
            if (coverageRows.Count > 0)
            {
                int testCount = TestCoverageLenses.CountTests(
                    coverageRows, TestCoverageLenses.LineRangeOf(declaration));

                if (testCount > 0)
                {
                    lenses.Add(new LspCodeLens(range, new Command(
                        testCount == 1 ? "1 test" : $"{testCount} tests",
                        "roslynSense.showTestsAt",
                        [p.TextDocument.Uri, identifierPosition.Line, identifierPosition.Character])));
                }
            }

            // Reference count: deferred to codeLens/resolve.
            lenses.Add(new LspCodeLens(range, Command: null)
            {
                Data = new CodeLensData(p.TextDocument.Uri,
                    identifierPosition.Line, identifierPosition.Character, "references"),
            });

            // Inheritance lenses — the clickable counterpart to the gutter arrows (which
            // VSCode gives no click/hover events for). Up relations are cheap and inline;
            // down counts (derived/implementations) need workspace queries -> lazy resolve,
            // and only where results are likely (interfaces, abstract members) to avoid a
            // wall of "0 overrides".
            if (model.GetDeclaredSymbol(declaration, ct) is { } symbol)
            {
                object[] inheritanceArgs =
                    [p.TextDocument.Uri, identifierPosition.Line, identifierPosition.Character];

                // Compact titles: type names only ("↑ impl. IHostedService"), never full
                // member paths — the QuickPick behind the click carries the detail.
                var shortNames = InheritanceMarkersHandler.ApplicableUpKinds(symbol)
                    .SelectMany(kind => InheritanceMarkersHandler.ComputeUpTargets(symbol, kind)
                        .Select(t => kind switch
                        {
                            "overrides" => $"overrides {t.Symbol.ContainingType?.Name ?? t.Symbol.Name}",
                            "implements" => $"impl. {t.Symbol.ContainingType?.Name ?? t.Symbol.Name}",
                            _ => t.Symbol.Name, // type's bases/interfaces — the name is the info
                        }))
                    .ToList();
                if (shortNames.Count > 0)
                {
                    string title = "↑ " + string.Join(", ", shortNames.Take(2))
                        + (shortNames.Count > 2 ? $" +{shortNames.Count - 2}" : "");
                    lenses.Add(new LspCodeLens(range, new Command(
                        title, "roslynSense.showInheritanceAt", inheritanceArgs)));
                }

                bool likelyHasDown = symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface }
                    || symbol.ContainingType?.TypeKind == TypeKind.Interface
                    || symbol.IsAbstract;
                if (likelyHasDown && InheritanceMarkersHandler.ApplicableDownKind(symbol) is { } downKind)
                {
                    lenses.Add(new LspCodeLens(range, Command: null)
                    {
                        Data = new CodeLensData(p.TextDocument.Uri,
                            identifierPosition.Line, identifierPosition.Character, downKind),
                    });
                }
            }

            if (declaration is MethodDeclarationSyntax method && IsTestMethod(method)
                && FullyQualifiedName(method) is { } fqn)
            {
                lenses.Add(new LspCodeLens(range, new Command(
                    "▶ Run test", "roslynSense.runTest", [fqn, projectPath ?? ""])));
                lenses.Add(new LspCodeLens(range, new Command(
                    "Debug test", "roslynSense.debugTest", [fqn, projectPath ?? ""])));
            }
        }

        // Lenses only a pack can count: a mediator handler is dispatched to from everywhere and
        // referenced from nowhere, so nothing above this line has anything to say about it.
        foreach (var contributor in
                 LanguageScope.Of(languages).Contributors<ILanguageCodeLensContributor>())
        {
            lenses.AddRange(await contributor.CodeLensAsync(document, ct));
        }

        return lenses.ToArray();
    }

    /// <summary>codeLens/resolve: computes the reference count (or inheritance-down count)
    /// for one visible lens.</summary>
    public static async Task<LspCodeLens> ResolveAsync(
        LspCodeLens lens, CancellationToken ct, LanguageSession? languages = null)
    {
        // A contributed lens carries the id of the pack that emitted it, because the document is
        // C# and so the URI cannot say whose it is. Checked first: the Kind below it is C#'s own
        // vocabulary and a pack's means something else.
        if (lens.Data is { PackId: { Length: > 0 } packId } packData)
        {
            // Memoized on the same key C#'s own counted lenses use. A contributed lens sits in a
            // C# document, and a pack's resolve can be just as expensive — the mediator's runs a
            // solution-wide SymbolFinder sweep per dispatch site, and a file with six handlers
            // emits about a dozen lenses, every one of them re-resolved on each scroll and each
            // edit. This branch returned before the memo was ever consulted.
            var packGeneration = await DocumentSemanticGeneration.ForAsync(packData.Uri, ct);

            foreach (var contributor in
                     LanguageScope.Of(languages).Contributors<ILanguageCodeLensContributor>())
            {
                if (contributor is not ILanguagePack pack ||
                    !pack.Id.Equals(packId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (packGeneration is null)
                {
                    if (await contributor.ResolveCodeLensAsync(lens, ct) is { } uncached)
                        return uncached;
                    continue;
                }

                var resolved = await CodeLensResolveMemo.ResolveAsync(
                    packData, packGeneration, lens,
                    async l => (await contributor.ResolveCodeLensAsync(l, CancellationToken.None))?.Command,
                    ct);

                if (resolved.Command is not null)
                    return resolved;
            }

            // The pack that emitted it is switched off for this window, or declined. An
            // unresolved lens with no command is what the protocol wants for "nothing here".
            return lens;
        }

        if (lens.Data is not { Kind: "references" or "derived" or "implemented" or "overridden" } data)
            return lens;

        // Both kinds run a workspace-wide search, and the client re-resolves every visible lens on
        // every edit and every scroll. The answer is a function of this file's text and the
        // semantics it can see, so it is memoized against exactly that — the same key
        // AnalyzerDiagnosticCache versions by. An edit in a project that depends on this one can
        // leave a count stale until this key next moves, which is the trade every IDE's lens makes.
        if (await DocumentSemanticGeneration.ForAsync(data.Uri, ct) is { } generation)
        {
            return await CodeLensResolveMemo.ResolveAsync(data, generation, lens,
                async l => (await ResolveCountedAsync(l, data, CancellationToken.None, languages)).Command,
                ct);
        }

        return await ResolveCountedAsync(lens, data, ct, languages);
    }

    private static Task<LspCodeLens> ResolveCountedAsync(
        LspCodeLens lens, CodeLensData data, CancellationToken ct, LanguageSession? languages) =>
        data.Kind == "references"
            ? ResolveReferencesAsync(lens, data, ct, languages)
            : ResolveInheritanceDownAsync(lens, data, ct);

    private static async Task<LspCodeLens> ResolveReferencesAsync(
        LspCodeLens lens, CodeLensData data, CancellationToken ct, LanguageSession? languages)
    {
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

        // Contributors included, rather than Roslyn's answer alone. A method a dozen mediator
        // sends dispatch to has no C# references at all, and a gutter reading "0 references"
        // above the peek that lists twelve is worse than no gutter. Counted rather than
        // enumerated: the peek below shows MaxReferenceLocations at most, so a symbol referenced
        // ten thousand times must not be searched to the end on every scroll.
        var (locations, capped) = await NavigationHandlers.CountedReferencesAsync(
            symbol, document.Project, MaxReferenceLocations, ct, languages);

        string title = capped
            ? $"{MaxReferenceLocations}+ references"
            : locations.Length == 1 ? "1 reference" : $"{locations.Length} references";
        return lens with
        {
            Command = new Command(title, "roslynSense.showReferences",
                [data.Uri, data.Line, data.Character, locations.Take(MaxReferenceLocations).ToArray()]),
        };
    }

    private static async Task<LspCodeLens> ResolveInheritanceDownAsync(
        LspCodeLens lens, CodeLensData data, CancellationToken ct)
    {
        object[] args = [data.Uri, data.Line, data.Character];
        var inert = new Command("", "roslynSense.showInheritanceAt", args);

        var resolved = await HandlerHelpers.ResolveAsync(
            new TextDocumentIdentifier(data.Uri), new Position(data.Line, data.Character), ct);
        if (resolved is not var (document, _, offset))
            return lens with { Command = inert };

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return lens with { Command = inert };

        var targets = await InheritanceMarkersHandler.ComputeDownTargetsAsync(
            symbol, data.Kind, document.Project.Solution, ct);
        string noun = data.Kind switch
        {
            "implemented" => targets.Count == 1 ? "implementation" : "implementations",
            "overridden" => targets.Count == 1 ? "override" : "overrides",
            _ => "derived",
        };
        return lens with
        {
            Command = new Command($"↓ {targets.Count} {noun}", "roslynSense.showInheritanceAt", args),
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
