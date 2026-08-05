using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using WebFormsCore;
using WebFormsCore.Models;
using WebFormsCore.Nodes;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>One place a symbol is mentioned in markup.</summary>
internal readonly record struct AspxReference(string FilePath, TextSpan Span, SourceText Text)
{
    /// <summary>The 1-based line the mention starts on.</summary>
    public int Line => LineIndex + 1;

    /// <summary>The whole source line, for a report that shows the mention in context.</summary>
    public string LineText => Text.Lines[LineIndex].ToString().Trim();

    private int LineIndex => Text.Lines.GetLinePosition(Math.Min(Span.Start, Text.Length)).Line;
}

/// <summary>Which part of a markup file a reference landed in.</summary>
internal enum AspxRegion
{
    /// <summary>A tag name, attribute name or attribute value.</summary>
    Markup,

    /// <summary>A <c>&lt;%= %&gt;</c>, <c>&lt;%: %&gt;</c> or <c>&lt;%# %&gt;</c> expression.</summary>
    Expression,

    /// <summary>A <c>&lt;% %&gt;</c> statement block.</summary>
    CodeBlock,

    /// <summary>A <c>&lt;script runat="server"&gt;</c> block.</summary>
    Script,
}

/// <summary>
/// Finds the markup half of a symbol's references: the tags, attributes and handler names in
/// <c>.aspx</c> files that Roslyn's own reference search cannot see.
/// </summary>
internal static class AspxReferenceService
{
    private sealed record FileListEntry(DateTime StampUtc, IReadOnlyList<string> Files);

    private static readonly ConcurrentDictionary<string, FileListEntry> s_files =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan FileListLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every markup mention of <paramref name="symbol"/> in <paramref name="project"/>.
    /// Tags, attribute names, handler names and IDs are matched against the parse tree; mentions
    /// inside code blocks are bound through the project's C# projection. Both are exact, which
    /// is what lets rename apply the result.
    /// </summary>
    public static async Task<List<AspxReference>> FindAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        var results = new List<AspxReference>();

        // Nothing in a project that cannot host WebForms markup is worth a directory walk, and
        // every find-references in the solution would otherwise pay for one.
        if (!await HostsWebFormsAsync(project, ct))
            return results;

        var definition = symbol.OriginalDefinition;
        var mentions = AspxMentionFilter.For(symbol);

        foreach (string file in EnumerateFiles(project))
        {
            ct.ThrowIfCancellationRequested();

            if (!mentions.MayMention(file))
                continue;

            var document = await AspxDocumentService.GetAsync(file, ct);
            if (document?.Tree is not { } root)
                continue;

            CollectMarkup(document, root, definition, results);
        }

        results.AddRange(await CollectCodeAsync(symbol, project, ct));
        return results;
    }

    /// <summary>Every markup mention of <paramref name="symbol"/> in one file's tags and
    /// attributes.</summary>
    public static List<AspxReference> FindInDocument(AspxDocument document, ISymbol symbol)
    {
        var results = new List<AspxReference>();
        if (document.Tree is not { } root)
            return results;

        CollectMarkup(document, root, symbol.OriginalDefinition, results);
        return results;
    }

    /// <summary>
    /// One file's mentions, markup and code both — for callers that only ever care about the
    /// file in front of them.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="FindAsync"/>: document highlight runs on every
    /// cursor move, and it must not build (or invalidate) the whole project's projection to
    /// answer a question about one file. The single-file projection it uses is the same one
    /// hover and completion already keep warm.
    /// </remarks>
    public static async Task<List<AspxReference>> FindInDocumentAsync(
        AspxDocument document, ISymbol symbol, CancellationToken ct)
    {
        var results = FindInDocument(document, symbol);

        if (AspxProjectionService.Get(document) is not { } projection)
            return results;

        var compilation = await projection.Document.Project.GetCompilationAsync(ct);
        if (compilation is null)
            return results;

        var target = SymbolFinder
            .FindSimilarSymbols(symbol.OriginalDefinition, compilation, ct)
            .FirstOrDefault();
        if (target is null)
            return results;

        var found = await SymbolFinder.FindReferencesAsync(
            target,
            projection.Document.Project.Solution,
            ImmutableHashSet.Create(projection.Document),
            ct);

        foreach (var location in found.SelectMany(r => r.Locations))
        {
            if (projection.ToAspx(location.Location.SourceSpan) is { } span)
                results.Add(new AspxReference(document.FilePath, span, document.SourceText));
        }

        return results;
    }

    private static void CollectMarkup(
        AspxDocument document, RootNode root, ISymbol symbol, List<AspxReference> results)
    {
        void Add(TokenRange range) =>
            results.Add(new AspxReference(document.FilePath, AspxSymbolResolver.Span(range), document.SourceText));

        if (symbol is INamedTypeSymbol && Same(root.Inherits, symbol))
        {
            foreach (var directive in root.Directives)
            {
                foreach (var (key, value) in directive.Attributes)
                {
                    if (key.Value.Equals("Inherits", StringComparison.OrdinalIgnoreCase))
                        Add(value.Range);
                }
            }
        }

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            var type = AspxSymbolResolver.ElementType(element);

            if (type is not null && Same(type, symbol))
            {
                Add(element.StartTag.ElementRange);
                if (element.EndTag is not null)
                    Add(element.EndTag.ElementRange);
            }

            if (element is ITypedNode typed)
            {
                foreach (var property in typed.Properties)
                {
                    // The name, not the value: a reference to the property Title is the `Title=`,
                    // and renaming it must not rewrite the "Welcome" the page actually displays.
                    // A synthesised property has no name in source and so has nothing to report.
                    if (Same(property.Member.Symbol, symbol) && property.NameRange is { } name)
                        Add(name);
                }

                foreach (var @event in typed.Events)
                {
                    if (Same(@event.Method, symbol) || Same(@event.Event, symbol))
                        Add(@event.Range);
                }
            }

            foreach (var (key, value) in element.RawAttributes)
            {
                if (type is not null && AspxSymbolResolver.TryGetEvent(type, key.Value) is { } declared
                    && Same(declared, symbol))
                {
                    Add(key.Range);
                    continue;
                }

                // An ID is the declaration site of the code-behind field, so it belongs in the
                // results for that field the way a declaration does.
                if (key.Value.Equals("ID", StringComparison.OrdinalIgnoreCase)
                    && symbol.Name.Equals(value.Value, StringComparison.Ordinal)
                    && Same(document.CodeBehind?.GetMemberDeep(value.Value)?.Symbol, symbol))
                {
                    Add(value.Range);
                }
            }
        }
    }

    /// <summary>
    /// Mentions inside <c>&lt;% %&gt;</c> blocks, <c>&lt;%= %&gt;</c> expressions and
    /// <c>&lt;script runat="server"&gt;</c> — bound, not matched by name.
    /// </summary>
    /// <remarks>
    /// The project's markup is projected into one forked compilation and Roslyn's own reference
    /// search runs over it. That is what keeps the word <c>Compute</c> in a comment out of the
    /// results, and — since rename applies whatever this returns — out of a rename.
    /// The symbol has to be re-resolved into that compilation first: it is a different
    /// <see cref="Compilation"/> instance, so the caller's symbol is not the same object.
    /// </remarks>
    private static async Task<List<AspxReference>> CollectCodeAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        var results = new List<AspxReference>();

        if (await AspxProjectionService.GetProjectAsync(project, ct) is not { } projection)
            return results;

        var target = SymbolFinder.FindSimilarSymbols(symbol.OriginalDefinition, projection.Compilation, ct)
            .FirstOrDefault();
        if (target is null)
            return results;

        var scope = projection.Documents.ToImmutableHashSet();
        var found = await SymbolFinder.FindReferencesAsync(target, projection.Solution, scope, ct);

        foreach (var referenced in found)
        {
            foreach (var location in referenced.Locations)
            {
                if (projection.ToMarkup(location.Document.Id, location.Location.SourceSpan) is { } mapped)
                    results.Add(new AspxReference(mapped.FilePath, mapped.Span, mapped.Text));
            }

            // A symbol declared in a code block — a `<script runat="server">` method — has its
            // declaration there too, and that is a result the same way a C# declaration is.
            foreach (var location in referenced.Definition.Locations)
            {
                if (!location.IsInSource
                    || !AspxProjectionService.IsProjectionPath(location.SourceTree?.FilePath))
                    continue;

                var id = projection.Documents
                    .FirstOrDefault(d => d.FilePath == location.SourceTree!.FilePath)?.Id;
                if (id is not null && projection.ToMarkup(id, location.SourceSpan) is { } mapped)
                    results.Add(new AspxReference(mapped.FilePath, mapped.Span, mapped.Text));
            }
        }

        return results;
    }

    /// <summary>
    /// Whether the project could contain markup at all — a metadata lookup, not I/O. Markup
    /// needs a control base class, and there are only two of them.
    /// </summary>
    public static async Task<bool> HostsWebFormsAsync(Project project, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct);
        return compilation is not null
            && (compilation.GetTypeByMetadataName("System.Web.UI.Control") is not null
                || compilation.GetTypeByMetadataName("WebFormsCore.UI.Control") is not null);
    }

    /// <summary>
    /// Which region of <paramref name="root"/> the span at <paramref name="offset"/> sits in, so
    /// a report can say whether a mention is markup or inline code without re-deriving it.
    /// </summary>
    public static AspxRegion RegionOf(RootNode? root, int offset)
    {
        if (root is null)
            return AspxRegion.Markup;

        foreach (var node in root.AllChildren)
        {
            switch (node)
            {
                case ExpressionNode expr when AspxSymbolResolver.Contains(expr.Text.Range, offset):
                    return AspxRegion.Expression;
                case StatementNode stmt when AspxSymbolResolver.Contains(stmt.Text.Range, offset):
                    return AspxRegion.CodeBlock;
            }
        }

        foreach (var script in root.ScriptBlocks)
        {
            if (AspxSymbolResolver.Contains(script.Range, offset))
                return AspxRegion.Script;
        }

        return AspxRegion.Markup;
    }

    /// <summary>
    /// The text a rename should put in a reference's span.
    /// </summary>
    /// <remarks>
    /// A markup mention may be qualified where the symbol is not — <c>asp:Button</c> names the
    /// type <c>Button</c>, and <c>Inherits="Site.DefaultPage"</c> names the type
    /// <c>DefaultPage</c> — so only the trailing segment is replaced. Replacing the whole span
    /// would drop the prefix and leave markup that no longer resolves.
    /// </remarks>
    public static string RenamedText(AspxReference reference, string newName)
    {
        string existing = reference.Text.ToString(reference.Span);
        int separator = existing.LastIndexOfAny([':', '.']);
        return separator < 0 ? newName : existing[..(separator + 1)] + newName;
    }

    private static bool Same(ISymbol? candidate, ISymbol symbol) =>
        candidate is not null
        && SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, symbol);

    /// <summary>
    /// Every ASPX-family file under the project directory. The listing is re-taken periodically
    /// rather than per call: a find-references over a large site would otherwise walk the whole
    /// tree before it parsed anything.
    /// </summary>
    public static IReadOnlyList<string> EnumerateFiles(Project project)
    {
        string? projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir is null || !Directory.Exists(projectDir))
            return [];

        if (s_files.TryGetValue(projectDir, out var cached)
            && DateTime.UtcNow - cached.StampUtc < FileListLifetime)
        {
            return cached.Files;
        }

        var files = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(projectDir, "*.*", SearchOption.AllDirectories))
            {
                if (!AspxDocumentService.IsAspxFile(file))
                    continue;

                string relative = Path.GetRelativePath(projectDir, file);
                string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (first.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || first.Equals("bin", StringComparison.OrdinalIgnoreCase))
                    continue;

                files.Add(file);
            }
        }
        catch (IOException)
        {
            // A directory vanished mid-walk; report what was found.
        }
        catch (UnauthorizedAccessException)
        {
        }

        s_files[projectDir] = new FileListEntry(DateTime.UtcNow, files);
        return files;
    }
}
