using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
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

    /// <param name="clientRefreshes">Whether the client honours
    /// <c>workspace/codeLens/refresh</c>. It is what makes it safe to answer before the semantic
    /// model exists: a client that cannot be asked to re-pull would keep the short list forever,
    /// so for one the model is awaited as it always was.</param>
    public static async Task<LspCodeLens[]> CodeLensAsync(
        CodeLensParams p, CancellationToken ct, LanguageSession? languages = null,
        bool clientRefreshes = false)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<LspCodeLens>();

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root is null)
            return Array.Empty<LspCodeLens>();

        // The syntactic lenses cost a parse the editor has already paid for; the inheritance ones
        // cost a semantic model, which on a large file in a cold project is the whole wait. So the
        // model is used when it is already there and never waited for: the first pull answers with
        // what the tree alone can say, and the arrows arrive on the re-pull the warm-up asks for.
        var model = await SemanticModelForListAsync(document, clientRefreshes, ct);

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
            if (model?.GetDeclaredSymbol(declaration, ct) is { } symbol)
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

    /// <summary>
    /// Which files have already had a lens list answered without their semantic model, and at
    /// which version of their text.
    /// </summary>
    /// <remarks>
    /// The bound on the loop. Without it a document whose model keeps being dropped — a file in a
    /// project half the solution depends on, edited elsewhere — would answer short, ask for a
    /// refresh, be asked again, answer short again, forever. Remembering the version turns that
    /// into exactly one extra round trip: the second pull for the same text waits for the model.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, VersionStamp> s_answeredWithout =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files whose model is being warmed, so a scroll does not start a second warm-up.</summary>
    private static readonly ConcurrentDictionary<string, byte> s_warming =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The semantic model for the list pass — the one already built, or none.
    /// </summary>
    /// <remarks>
    /// Roslyn hands one over immediately when it has it, and building one otherwise means binding
    /// the project: seconds on a large file the first time it is opened, during which the editor
    /// shows no lens at all rather than the run buttons and reference counts it could already
    /// have. So a miss returns null and warms the model on a thread of its own, and the client is
    /// asked to re-pull when it lands.
    /// </remarks>
    private static async Task<SemanticModel?> SemanticModelForListAsync(
        Document document, bool clientRefreshes, CancellationToken ct)
    {
        if (document.TryGetSemanticModel(out var ready))
        {
            if (document.FilePath is { Length: > 0 } done)
                s_answeredWithout.TryRemove(done, out _);

            return ready;
        }

        if (!clientRefreshes || document.FilePath is not { Length: > 0 } path)
            return await document.GetSemanticModelAsync(ct);

        var version = await document.GetTextVersionAsync(ct);

        // Already answered short for this exact text and the model still is not here: waiting is
        // the honest answer now, rather than a third short list.
        if (s_answeredWithout.TryGetValue(path, out var answered) && answered == version)
            return await document.GetSemanticModelAsync(ct);

        s_answeredWithout[path] = version;
        WarmSemanticModel(document, path);
        return null;
    }

    private static void WarmSemanticModel(Document document, string path)
    {
        if (!s_warming.TryAdd(path, 0))
            return;

        // Not awaited, and not cancelled by the request that started it: the point is to have the
        // model by the time the client comes back, and the request that noticed it was missing is
        // over long before then.
        _ = Task.Run(async () =>
        {
            try
            {
                await document.GetSemanticModelAsync(CancellationToken.None);
                LspSessionRegistry.ScheduleRefresh(RefreshKind.CodeLens, "codelens-semantics");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceLog.Warn(
                    $"Could not warm the semantic model for '{Path.GetFileName(path)}': {ex.Message}",
                    key: $"codelens-warm:{path}");
            }
            finally
            {
                s_warming.TryRemove(path, out _);
            }
        });
    }

    /// <summary>Forgets what has been answered short. Tests only.</summary>
    internal static void ClearWarmupState()
    {
        s_answeredWithout.Clear();
        s_warming.Clear();
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
        // every edit and every scroll. The answer is a function of this file's text and of every
        // project that could mention its symbols — dependents included, because a new call site is
        // typed into the caller's project — so it is memoized against exactly that. Scroll after
        // scroll is served from the memo; the edit that could change the count is the one that
        // drops it.
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
        // Decompiled source counts against the solution, like the peek the click opens — a lens
        // reading "0 references" above every member of a type the solution uses everywhere is
        // worse than no lens. Warm projects only: this resolves as the view scrolls, and compiling
        // the solution to put a number in the gutter is not a trade anybody asked for.
        var counted = await ExternalSymbolBridge.TryMapAsync(
            symbol, document, Services.WorkspaceService.TryGetSessionSolution(), ct,
            warmProjectsOnly: true);

        var (locations, capped) = await NavigationHandlers.CountedReferencesAsync(
            counted?.Symbol ?? symbol, counted?.Project ?? document.Project,
            MaxReferenceLocations, ct, languages);

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

        // Mapped the way the gutter markers are, and warm-only for the same reason, so the two
        // renderings of one relationship cannot disagree about how many there are.
        var mapped = await ExternalSymbolBridge.TryMapAsync(
            symbol, document, Services.WorkspaceService.TryGetSessionSolution(), ct,
            warmProjectsOnly: true);

        var targets = await InheritanceMarkersHandler.ComputeDownTargetsAsync(
            mapped?.Symbol ?? symbol,
            data.Kind,
            mapped?.Project.Solution ?? document.Project.Solution,
            ct);
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
