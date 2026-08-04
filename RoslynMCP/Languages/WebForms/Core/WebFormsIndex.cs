using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using WebFormsCore.Models;
using WebFormsCore.Nodes;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>A control the markup declares, and where its <c>ID</c> attribute value is written.</summary>
internal readonly record struct WebFormsControlId(
    string Id, string? Prefix, string TagName, LinePositionSpan Span);

/// <summary>An <c>On…</c> attribute and the method name it names.</summary>
/// <remarks>
/// Candidates, not bound handlers: <c>OnClientClick</c> is a plain string property and only the
/// compilation can tell the two apart. Binding is the consumer's job.
/// </remarks>
internal readonly record struct WebFormsHandler(
    string AttributeName, string MethodName, LinePositionSpan Span);

/// <summary>A control a <c>&lt;%@ Register %&gt;</c> directive brings into scope.</summary>
internal readonly record struct WebFormsRegistration(
    string Prefix, string TagName, string? SourcePath, LinePositionSpan Span);

/// <summary>
/// What one markup file declares, without its parse tree: the control IDs, the tag prefixes in
/// scope, the handler names its attributes mention, and the class its <c>Inherits</c> names.
/// </summary>
/// <remarks>
/// Names, never symbols. That is what keeps an entry keyed on the file itself rather than on the
/// file and the compilation together — Roslyn's <c>SyntaxTreeIndex</c> holds names for the same
/// reason — and it is why a consumer that has to know whether a name actually binds holds an
/// <see cref="AspxDocument"/> as well, which is memoized against the compilation.
/// </remarks>
internal sealed class WebFormsFileIndex
{
    public required string FilePath { get; init; }

    /// <summary>The <c>Inherits</c> value as written — a fully-qualified name, usually.</summary>
    public required string? Inherits { get; init; }

    public required LinePositionSpan InheritsSpan { get; init; }

    public required ImmutableArray<WebFormsControlId> Controls { get; init; }

    /// <summary>Every prefix the file can write a tag under, from its own
    /// <c>&lt;%@ Register %&gt;</c> directives and from <c>web.config</c>.</summary>
    public required ImmutableArray<string> TagPrefixes { get; init; }

    public required ImmutableArray<WebFormsHandler> Handlers { get; init; }

    public required ImmutableArray<WebFormsRegistration> Registrations { get; init; }

    /// <summary>The page class's own name, with the namespace stripped.</summary>
    public string? InheritsName =>
        Inherits is { Length: > 0 } name
            ? name[(name.LastIndexOf('.') + 1)..]
            : null;

    /// <summary>The namespace the page class sits in, or null when <c>Inherits</c> named no
    /// namespace.</summary>
    public string? InheritsNamespace =>
        Inherits is { Length: > 0 } name && name.LastIndexOf('.') is > 0 and var dot
            ? name[..dot]
            : null;
}

/// <summary>
/// Per-file summaries of a project's markup, so that a question about the whole solution does not
/// reparse every page to answer it.
/// </summary>
/// <remarks>
/// The shape is Roslyn's: <c>SyntaxTreeIndex</c> is a per-document, checksum-keyed digest that
/// Find-References and Navigate-To consult to prune candidates before doing real work, and the
/// alternative here — <see cref="AspxReferenceService.EnumerateFiles"/> plus a parse per consumer
/// — is what makes those features scale badly on a site with hundreds of pages.
/// <para>
/// Two entry points because there are two shapes of question, and each rides a cache that already
/// exists rather than adding a third. One file at a time goes through
/// <see cref="AspxDocumentService"/>, which is open-buffer aware and memoizes the parse. A whole
/// project goes through <see cref="ProjectIndexCacheService"/>, which already parses every markup
/// file once and drops the result when a <see cref="FileSystemWatcher"/> says it moved; the
/// summaries hang off that result's identity, so they live and die with it.
/// </para>
/// </remarks>
internal static class WebFormsIndex
{
    /// <summary>
    /// The file's checksum, and the tree the summary was read out of.
    /// </summary>
    /// <remarks>
    /// Roslyn keys a <c>SyntaxTreeIndex</c> on the checksum alone, and for a C# file that is
    /// enough: the file is the whole input. Markup is not self-contained — registering a tag
    /// prefix in <c>web.config</c> changes which controls a page has in scope without touching a
    /// byte of the page — so the checksum answers "did the text move" and the tree answers "did
    /// anything else". <see cref="AspxDocumentService"/> hands out a new tree in both cases,
    /// which makes the second question a reference comparison.
    /// </remarks>
    private sealed record CacheEntry(
        ImmutableArray<byte> Checksum, RootNode Tree, WebFormsFileIndex Index);

    private static readonly ConcurrentDictionary<string, CacheEntry> s_files =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConditionalWeakTable<AspxProjectIndex, WebFormsFileIndex[]> s_projects =
        new();

    /// <summary>
    /// The summary of one markup file, or <c>null</c> when it does not parse or belongs to no
    /// project.
    /// </summary>
    public static async Task<WebFormsFileIndex?> GetAsync(string filePath, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(filePath, ct);
        if (document?.Tree is not { } root)
            return null;

        var checksum = document.SourceText.GetChecksum();

        if (s_files.TryGetValue(document.FilePath, out var cached)
            && ReferenceEquals(cached.Tree, root)
            && cached.Checksum.AsSpan().SequenceEqual(checksum.AsSpan()))
        {
            return cached.Index;
        }

        var index = Build(document.FilePath, root);
        s_files[document.FilePath] = new CacheEntry(checksum, root, index);
        return index;
    }

    /// <summary>
    /// Every markup file in the project, summarized. Empty for a project that cannot host
    /// WebForms at all — the metadata check that keeps a C#-only solution off the file system.
    /// </summary>
    public static async Task<IReadOnlyList<WebFormsFileIndex>> ForProjectAsync(
        Project project, CancellationToken ct)
    {
        if (project.FilePath is null || !await AspxReferenceService.HostsWebFormsAsync(project, ct))
            return [];

        var parsed = await ProjectIndexCacheService.GetAspxIndexAsync(project, ct);

        if (s_projects.TryGetValue(parsed, out var cached))
            return cached;

        var summaries = new List<WebFormsFileIndex>(parsed.Files.Count);
        foreach (var file in parsed.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.ParseTree is { } root)
                summaries.Add(Build(file.FilePath, root));
        }

        var result = summaries.ToArray();
        return s_projects.GetValue(parsed, _ => result);
    }

    /// <summary>Summarizes an already-parsed tree.</summary>
    public static WebFormsFileIndex Build(string filePath, RootNode root)
    {
        string? inherits = null;
        LinePositionSpan inheritsSpan = default;
        var registrations = ImmutableArray.CreateBuilder<WebFormsRegistration>();

        foreach (var directive in root.Directives)
        {
            if (directive.DirectiveType == DirectiveType.Register)
            {
                if (Attribute(directive, "TagPrefix") is { Value.Length: > 0 } prefix
                    && Attribute(directive, "TagName") is { } tagName)
                {
                    registrations.Add(new WebFormsRegistration(
                        prefix.Value, tagName.Value, Attribute(directive, "Src")?.Value,
                        tagName.Range));
                }

                continue;
            }

            // The page directive comes first and is the only one that may name a code-behind
            // class; a later Inherits belongs to something else and must not overwrite it.
            if (inherits is null && Attribute(directive, "Inherits") is { Value.Length: > 0 } value)
            {
                inherits = value.Value;
                inheritsSpan = value.Range;
            }
        }

        var controls = ImmutableArray.CreateBuilder<WebFormsControlId>();
        var handlers = ImmutableArray.CreateBuilder<WebFormsHandler>();

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            if (!IsServerControl(element))
                continue;

            foreach (var (key, value) in element.RawAttributes)
            {
                if (key.Value.Equals("ID", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Value.Length > 0)
                    {
                        controls.Add(new WebFormsControlId(
                            value.Value, element.Namespace?.Value, element.Name.Value,
                            value.Range));
                    }

                    continue;
                }

                if (key.Value.Length > 2 && value.Value.Length > 0
                    && key.Value.StartsWith("On", StringComparison.OrdinalIgnoreCase))
                {
                    handlers.Add(new WebFormsHandler(key.Value, value.Value, value.Range));
                }
            }
        }

        return new WebFormsFileIndex
        {
            FilePath = filePath,
            Inherits = inherits,
            InheritsSpan = inheritsSpan,
            Controls = controls.ToImmutable(),
            TagPrefixes = [.. root.TagPrefixes.Keys],
            Handlers = handlers.ToImmutable(),
            Registrations = registrations.ToImmutable(),
        };
    }

    /// <summary>
    /// Whether the tag is a control rather than markup the browser gets verbatim. The
    /// <c>id</c> of a plain <c>&lt;div&gt;</c> is an HTML id and belongs to nobody; the same
    /// attribute on a control names a field.
    /// </summary>
    private static bool IsServerControl(ElementNode element) =>
        element.Namespace is not null
        || (element.RawAttributes.TryGetValue("runat", out var runAt)
            && runAt.Value.Equals("server", StringComparison.OrdinalIgnoreCase));

    private static AttributeValue? Attribute(DirectiveNode directive, string name) =>
        directive.Attributes.TryGetValue(name, out var value) ? value : null;
}
