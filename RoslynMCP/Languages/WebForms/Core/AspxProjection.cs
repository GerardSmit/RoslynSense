using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WebFormsCore.Models;
using WebFormsCore.Nodes;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>One run of ASPX text copied verbatim into the projected C# document.</summary>
internal readonly record struct AspxProjectionSegment(int AspxStart, int ProjectedStart, int Length);

/// <summary>The projected C# for one markup file, before it becomes a document.</summary>
/// <param name="Text">The generated source.</param>
/// <param name="Segments">Where each verbatim run of markup ended up in it.</param>
internal sealed record AspxProjectedText(string Text, ImmutableArray<AspxProjectionSegment> Segments)
{
    /// <summary>The projected offset for a caret in the markup, or <c>null</c> when the caret
    /// is not inside code.</summary>
    public int? ToProjected(int aspxOffset)
    {
        foreach (var segment in Segments)
        {
            if (aspxOffset >= segment.AspxStart && aspxOffset <= segment.AspxStart + segment.Length)
                return segment.ProjectedStart + (aspxOffset - segment.AspxStart);
        }
        return null;
    }

    /// <summary>The markup span a projected span came from, or <c>null</c> when it landed in
    /// scaffolding rather than in copied code.</summary>
    /// <remarks>
    /// Binary search over <see cref="Segments"/>, which the builder emits in projected order.
    /// A find-references over a whole project maps every result through here, so this is the
    /// direction that sees N calls per gesture where <see cref="ToProjected"/> sees one.
    /// </remarks>
    public TextSpan? ToAspx(TextSpan projected)
    {
        int lo = 0, hi = Segments.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var segment = Segments[mid];

            if (projected.Start < segment.ProjectedStart)
            {
                hi = mid - 1;
            }
            else if (projected.Start > segment.ProjectedStart + segment.Length)
            {
                lo = mid + 1;
            }
            else
            {
                // Two script blocks copied back to back share a boundary offset; the linear scan
                // this replaces answered with the first of the two.
                while (mid > 0 && projected.Start <= Segments[mid - 1].ProjectedStart + Segments[mid - 1].Length)
                {
                    mid--;
                    segment = Segments[mid];
                }

                int start = segment.AspxStart + (projected.Start - segment.ProjectedStart);
                int length = Math.Min(projected.Length, segment.Length - (projected.Start - segment.ProjectedStart));
                return new TextSpan(start, Math.Max(0, length));
            }
        }
        return null;
    }
}

/// <summary>
/// The C# hiding inside an ASPX file — <c>&lt;script runat="server"&gt;</c> members,
/// <c>&lt;% %&gt;</c> statements, <c>&lt;%= %&gt;</c> expressions and the data-binding
/// expressions in attribute values — lifted into a synthetic partial of the code-behind class,
/// so Roslyn can bind it.
/// </summary>
/// <remarks>
/// This is the same trick Razor uses. The generated text is never shown to anyone: it exists so
/// that hover, go-to-definition, completion, signature help and find-references inside a code
/// block answer with real symbols instead of guesses. Every verbatim run is recorded in
/// <see cref="AspxProjectedText.Segments"/>, which is what maps a caret in the markup to a caret
/// in the projection and a resulting span back again.
/// </remarks>
internal sealed record AspxProjection(
    Document Document,
    SourceText Text,
    AspxProjectedText Projected)
{
    public ImmutableArray<AspxProjectionSegment> Segments => Projected.Segments;

    public int? ToProjected(int aspxOffset) => Projected.ToProjected(aspxOffset);

    public TextSpan? ToAspx(TextSpan projected) => Projected.ToAspx(projected);

    /// <summary>Whether a projected span is copied markup rather than generated scaffolding.
    /// Results landing in scaffolding are dropped — they point at text no one can open.</summary>
    public bool IsMapped(TextSpan projected) => ToAspx(projected) is not null;
}

/// <summary>
/// Every markup file in one project, projected into a single forked compilation.
/// </summary>
/// <remarks>
/// One compilation, not one per file. A find-references or a rename has to look at all of them
/// at once, and adding N syntax trees to an existing compilation costs N binds — where forking
/// the project N times would cost N compilations.
/// </remarks>
internal sealed record AspxProjectProjection(
    Compilation Compilation,
    ImmutableArray<Document> Documents,
    ImmutableDictionary<DocumentId, AspxProjectedFile> Files)
{
    public Solution Solution => Documents.IsEmpty
        ? throw new InvalidOperationException("An empty projection has no solution.")
        : Documents[0].Project.Solution;

    /// <summary>The markup file and span a projected location came from.</summary>
    public (string FilePath, SourceText Text, TextSpan Span)? ToMarkup(DocumentId id, TextSpan projected)
    {
        if (!Files.TryGetValue(id, out var file))
            return null;

        return file.Projected.ToAspx(projected) is { } span
            ? (file.MarkupPath, file.MarkupText, span)
            : null;
    }
}

/// <summary>One markup file inside an <see cref="AspxProjectProjection"/>.</summary>
internal sealed record AspxProjectedFile(
    string MarkupPath, SourceText MarkupText, AspxProjectedText Projected);

internal static class AspxProjectionService
{
    private const string InlineMethodPrefix = "__AspxInline";
    private const string WriteMethod = "__AspxWrite";

    /// <summary>The suffix that marks a document as a projection rather than a real file.</summary>
    internal const string ProjectionSuffix = ".aspx-inline.g.cs";

    /// <summary>
    /// Whether a path belongs to a projection. Callers that would hand a span or a path to the
    /// client have to check: a projection exists only in memory, and an edit or a location
    /// expressed against it means nothing in the file the user has open.
    /// </summary>
    public static bool IsProjectionPath(string? filePath) =>
        filePath is not null && filePath.EndsWith(ProjectionSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The markup file a projection was built from.</summary>
    public static string? MarkupPathFor(string? projectionPath) =>
        IsProjectionPath(projectionPath)
            ? projectionPath![..^ProjectionSuffix.Length]
            : null;

    // ---- Single document -------------------------------------------------------------------

    private sealed record CacheEntry(AspxDocument Document, AspxProjection? Projection);

    private static readonly ConcurrentDictionary<string, CacheEntry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds (or returns the memoized) projection for a document, or <c>null</c> when there is
    /// nothing to project.
    /// </summary>
    /// <remarks>
    /// Keyed on the document instance: <see cref="AspxDocumentService"/> serves the same
    /// <see cref="AspxDocument"/> for as long as its parse is valid, so the projection built from
    /// it — same text, same snapshots — is valid exactly as long.
    /// </remarks>
    public static AspxProjection? Get(AspxDocument document)
    {
        if (s_cache.TryGetValue(document.FilePath, out var cached)
            && ReferenceEquals(cached.Document, document))
        {
            return cached.Projection;
        }

        var projection = Build(document);
        s_cache[document.FilePath] = new CacheEntry(document, projection);
        return projection;
    }

    private static AspxProjection? Build(AspxDocument document)
    {
        if (ProjectText(document) is not { } projected)
            return null;

        var sourceText = SourceText.From(projected.Text);
        var added = document.Project.AddDocument(
            Path.GetFileName(document.FilePath) + ProjectionSuffix,
            sourceText,
            filePath: document.FilePath + ProjectionSuffix);

        return new AspxProjection(added, sourceText, projected);
    }

    // ---- Whole project ---------------------------------------------------------------------

    private sealed record ProjectCacheEntry(
        Compilation BaseCompilation,
        ImmutableDictionary<string, string> Stamps,
        AspxProjectProjection? Projection);

    private static readonly ConcurrentDictionary<string, ProjectCacheEntry> s_projectCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every markup file in the project, projected into one forked compilation, or <c>null</c>
    /// when none of them has any code to project.
    /// </summary>
    /// <remarks>
    /// Rebuilt outright when the project's compilation changes or a file enters or leaves the
    /// projection — which is what makes it safe to bind against. When only some files' contents
    /// changed, the cached fork is patched instead: replacing one document's text lets Roslyn
    /// reuse every other tree of the forked compilation, where rebuilding re-forks and re-binds
    /// all of them for a one-character markup edit. Built lazily: only find-references and rename
    /// need the whole project, and both are deliberate gestures rather than per-keystroke work.
    /// </remarks>
    public static async Task<AspxProjectProjection?> GetProjectAsync(
        Project project, CancellationToken ct)
    {
        if (project.FilePath is not { Length: > 0 } projectPath
            || project.Language != LanguageNames.CSharp)
            return null;

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return null;

        var documents = new List<AspxDocument>();
        foreach (string file in AspxReferenceService.EnumerateFiles(project))
        {
            ct.ThrowIfCancellationRequested();
            if (await AspxDocumentService.GetAsync(file, ct) is { } document)
                documents.Add(document);
        }

        var stamps = Stamps(documents);

        if (s_projectCache.TryGetValue(projectPath, out var cached)
            && ReferenceEquals(cached.BaseCompilation, compilation))
        {
            if (StampsEqual(cached.Stamps, stamps))
                return cached.Projection;

            if (await TryPatchAsync(project, cached, documents, stamps, ct) is { } patched)
            {
                s_projectCache[projectPath] = patched;
                return patched.Projection;
            }
        }

        var built = await BuildProjectAsync(project, documents, ct);
        s_projectCache[projectPath] = new ProjectCacheEntry(compilation, stamps, built);
        return built;
    }

    /// <summary>Checksums rather than <c>string.GetHashCode</c>: a 32-bit string hash colliding
    /// across two versions of a page would silently serve the old projection for the new text.</summary>
    private static ImmutableDictionary<string, string> Stamps(IReadOnlyList<AspxDocument> documents)
    {
        var stamps = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
            stamps[document.FilePath] = Convert.ToHexString(document.SourceText.GetChecksum().AsSpan());
        return stamps.ToImmutable();
    }

    private static bool StampsEqual(
        ImmutableDictionary<string, string> old, ImmutableDictionary<string, string> current)
    {
        if (old.Count != current.Count)
            return false;

        foreach (var (path, stamp) in current)
        {
            if (!old.TryGetValue(path, out var previous) || previous != stamp)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The cached fork with only the changed files' text replaced, or <c>null</c> when the change
    /// is one a patch cannot express: a file joined or left the projection, so the fork's document
    /// set is wrong and only a rebuild fixes it.
    /// </summary>
    private static async Task<ProjectCacheEntry?> TryPatchAsync(
        Project project, ProjectCacheEntry cached, IReadOnlyList<AspxDocument> documents,
        ImmutableDictionary<string, string> stamps, CancellationToken ct)
    {
        if (cached.Projection is not { } projection)
            return null;

        // A different file set is a membership change however the stamps read.
        if (cached.Stamps.Count != stamps.Count
            || documents.Any(d => !cached.Stamps.ContainsKey(d.FilePath)))
            return null;

        var byPath = projection.Files.ToDictionary(
            pair => pair.Value.MarkupPath, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

        var solution = projection.Solution;
        var files = projection.Files;

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();

            if (cached.Stamps[document.FilePath] == stamps[document.FilePath])
                continue;

            bool wasProjected = byPath.TryGetValue(document.FilePath, out var id);
            var projected = ProjectText(document);

            if (projected is null)
            {
                if (!wasProjected)
                    continue;
                return null;
            }

            if (!wasProjected)
                return null;

            solution = solution.WithDocumentText(id!, SourceText.From(projected.Text));
            files = files.SetItem(
                id!, new AspxProjectedFile(document.FilePath, document.SourceText, projected));
        }

        var compilation = await solution.GetProject(project.Id)!.GetCompilationAsync(ct);
        if (compilation is null)
            return null;

        var projectionDocuments = projection.Documents
            .Select(d => solution.GetDocument(d.Id)!)
            .ToImmutableArray();

        return cached with
        {
            Stamps = stamps,
            Projection = new AspxProjectProjection(compilation, projectionDocuments, files),
        };
    }

    private static async Task<AspxProjectProjection?> BuildProjectAsync(
        Project project, IReadOnlyList<AspxDocument> documents, CancellationToken ct)
    {
        var pending = new List<(AspxDocument Document, AspxProjectedText Projected, DocumentId Id)>();
        var solution = project.Solution;

        foreach (var document in documents)
        {
            if (ProjectText(document) is not { } projected)
                continue;

            var id = DocumentId.CreateNewId(project.Id);
            solution = solution.AddDocument(
                id,
                Path.GetFileName(document.FilePath) + ProjectionSuffix,
                SourceText.From(projected.Text),
                filePath: document.FilePath + ProjectionSuffix);

            pending.Add((document, projected, id));
        }

        if (pending.Count == 0)
            return null;

        var compilation = await solution.GetProject(project.Id)!.GetCompilationAsync(ct);
        if (compilation is null)
            return null;

        var files = pending.ToImmutableDictionary(
            entry => entry.Id,
            entry => new AspxProjectedFile(
                entry.Document.FilePath, entry.Document.SourceText, entry.Projected));

        var projectionDocuments = pending
            .Select(entry => solution.GetDocument(entry.Id)!)
            .ToImmutableArray();

        return new AspxProjectProjection(compilation, projectionDocuments, files);
    }

    // ---- Text generation -------------------------------------------------------------------

    /// <summary>
    /// The projected C# for one markup file, or <c>null</c> when there is nothing to project:
    /// the page is not C#, there is no class to hang the code off, or it has no code at all.
    /// </summary>
    private static AspxProjectedText? ProjectText(AspxDocument document)
    {
        if (document.Tree is not { } root)
            return null;

        // `<%@ Page Language="VB" %>`. Emitting VB into a C# document would produce a tree of
        // syntax errors and bind to nothing; the rest of the markup features do not care what
        // language the code blocks are in, so only this step opts out.
        if (root.Language != WebFormsCore.Nodes.Language.CSharp)
            return null;

        if (document.Project.Language != LanguageNames.CSharp)
            return null;

        var codeBehind = document.CodeBehind;

        // A generic code-behind cannot be reopened as `partial class X` without repeating its
        // type parameters and constraints; it is rare enough not to be worth the reconstruction.
        if (codeBehind is { IsGenericType: true })
            return null;

        // Two cases where the class must not be reopened as `partial class X`. A single-file
        // page has no Inherits at all, and the parser reports the page base itself — reopening
        // that would add the markup's members to `System.Web.UI.Page`. An Inherits naming a type
        // from a referenced assembly cannot be reopened either, partial or not. Both still have
        // code worth binding, so the projection derives a class of its own from that type.
        INamedTypeSymbol? pageBase = null;
        if (codeBehind is null || !HasInherits(root) || codeBehind.DeclaringSyntaxReferences.IsEmpty)
        {
            pageBase = codeBehind ?? PageBaseType(document.Compilation);
            codeBehind = null;
        }

        if (codeBehind is null && pageBase is null)
            return null;

        var sb = new StringBuilder();
        var segments = ImmutableArray.CreateBuilder<AspxProjectionSegment>();
        string text = document.Text;

        void Copy(TokenRange range)
        {
            int start = range.Start.Offset;
            int end = Math.Min(range.End.Offset, text.Length);
            if (end <= start)
                return;

            segments.Add(new AspxProjectionSegment(start, sb.Length, end - start));
            sb.Append(text, start, end - start);
        }

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");

        foreach (string import in root.Namespaces)
            sb.Append("using ").Append(import).AppendLine(";");

        string? ns = codeBehind?.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : null;

        if (ns is not null)
            sb.Append("namespace ").Append(ns).AppendLine(" {");

        if (codeBehind is not null)
        {
            sb.Append("partial class ").Append(codeBehind.Name).AppendLine(" {");
        }
        else
        {
            sb.Append("partial class ").Append(SyntheticClassName(document.FilePath))
              .Append(" : global::").Append(pageBase!.ToDisplayString()).AppendLine(" {");
        }

        sb.Append("private void ").Append(WriteMethod).AppendLine("(object __value) { }");

        // <script runat="server"> holds member declarations, so it goes at class level.
        foreach (var script in root.ScriptBlocks)
            Copy(script.Range);

        AppendInlineMethod(sb, Copy, root, index: 0);

        // Every template is its own method: `<% if (…) { %>` and its closing `<% } %>` have to
        // stay in one body to balance, and a template's blocks never pair with the page's.
        int templateIndex = 1;
        foreach (var template in root.Templates)
            AppendInlineMethod(sb, Copy, template, templateIndex++);

        sb.AppendLine("}");
        if (ns is not null)
            sb.AppendLine("}");

        return segments.Count == 0
            ? null
            : new AspxProjectedText(sb.ToString(), segments.ToImmutable());
    }

    /// <summary>
    /// Whether the markup names a code-behind class at all. <c>RootNode.Inherits</c> alone does
    /// not answer this: with no directive the parser reports the page base type, which is a
    /// class that must not be reopened.
    /// </summary>
    private static bool HasInherits(RootNode root) =>
        root.Directives.Any(directive => directive.Attributes.Keys.Any(
            key => key.Value.Equals("Inherits", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The base class a page with no code-behind implicitly derives from.</summary>
    private static INamedTypeSymbol? PageBaseType(Compilation compilation) =>
        compilation.GetTypeByMetadataName("System.Web.UI.Page")
        ?? compilation.GetTypeByMetadataName("WebFormsCore.UI.Page");

    /// <summary>
    /// A class name for a page with no code-behind. The path's hash is in it because two files
    /// with the same name in different folders would otherwise generate the same
    /// <c>partial class</c> and merge into one, colliding on every member.
    /// </summary>
    private static string SyntheticClassName(string filePath) =>
        "__AspxPage_"
        + Regex.Replace(Path.GetFileName(filePath), "[^A-Za-z0-9_]", "_")
        + "_"
        + (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(filePath);

    private static void AppendInlineMethod(
        StringBuilder sb, Action<TokenRange> copy, ContainerNode container, int index)
    {
        sb.Append("private void ").Append(InlineMethodPrefix).Append(index).AppendLine("() {");

        if (container is TemplateNode template)
        {
            // The variable ASP.NET puts in scope inside a container that declares an ItemType.
            if (template.ItemType is { } itemType)
            {
                sb.Append("var Item = default(")
                  .Append(itemType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                  .AppendLine(")!;");
            }

            // `Container`, typed by [TemplateContainer] on the template property.
            if (template.ContainerType is { } containerType)
            {
                sb.Append("var Container = default(")
                  .Append(containerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                  .AppendLine(")!;");
            }
        }

        // A template is itself an element, and its own attributes can carry code.
        if (container is ElementNode owner)
            AppendAttributeCode(sb, copy, owner);

        foreach (var node in container.AllChildren)
        {
            switch (node)
            {
                case StatementNode statement:
                    copy(statement.Text.Range);
                    sb.AppendLine();
                    break;

                case ExpressionNode expression:
                    sb.Append(WriteMethod).Append('(');
                    copy(expression.Text.Range);
                    sb.AppendLine(");");
                    break;

                // A builder is resolved when the page is parsed, not when it is compiled, so
                // nothing of it belongs in a compilation: copying `$ Resources: Strings, Title`
                // is what used to report a CS syntax error the user could not act on, and there
                // would be nothing for Roslyn to bind even if it were well-formed. Emitting
                // nothing also keeps it out of Segments, which is what tells the rest of the
                // server that an offset is C#.
                case ExpressionBuilderNode:
                    break;

                case ElementNode element:
                    AppendAttributeCode(sb, copy, element);
                    break;
            }
        }

        sb.AppendLine("}");
    }

    /// <summary>
    /// The data-binding expressions in an element's attributes —
    /// <c>Text='&lt;%# Eval("Name") %&gt;'</c>.
    /// </summary>
    /// <remarks>
    /// The parser hands these back on the attribute rather than as a child node, so the walk over
    /// the container's children never sees them. Without this they are the one kind of code in a
    /// page that reaches no compilation at all: nothing binds, nothing renames, and a method
    /// called only from an attribute looks unused.
    /// </remarks>
    private static void AppendAttributeCode(
        StringBuilder sb, Action<TokenRange> copy, ElementNode element)
    {
        foreach (var (_, value) in element.RawAttributes)
        {
            // By kind rather than by emptiness: an expression builder's value is its resource key,
            // which is neither empty nor C#.
            if (value.Kind is not AttributeValueKind.Code || string.IsNullOrWhiteSpace(value.Value))
                continue;

            sb.Append(WriteMethod).Append('(');
            copy(value.Range);
            sb.AppendLine(");");
        }
    }
}
