using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Tools;
using WebFormsCore.Nodes;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// One ASPX file as the editor sees it: the buffer text, the project it belongs to, and the
/// markup parse tree built against that project's compilation.
/// </summary>
internal sealed record AspxDocument(
    string FilePath,
    string Text,
    SourceText SourceText,
    Project Project,
    Compilation Compilation,
    AspxParseResult Parse)
{
    public RootNode? Tree => Parse.ParseTree;

    /// <summary>The code-behind class the <c>Inherits</c> directive names, when it resolved.</summary>
    public INamedTypeSymbol? CodeBehind => Parse.ParseTree?.Inherits;
}

/// <summary>
/// Resolves an ASPX-family path to a parsed <see cref="AspxDocument"/>, the way
/// <see cref="LspDocumentResolver"/> resolves a <c>.cs</c> path to a Roslyn document.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is memoized per file the way an incremental generator stage is: against the buffer
/// text and the project's dependent semantic version, which moves when a declaration anywhere
/// in the dependency closure changes and stays put for a method-body edit. Markup binds only
/// to declarations — control types, members, event handlers — so a body edit cannot change
/// what the same markup means, and the memo survives the keystrokes that used to invalidate
/// every markup parse in the project.
/// </para>
/// <para>
/// The code-behind's own files are checked by text version on top of that. A body edit there
/// still shifts the lines that markup navigation answers with, and the file is being edited
/// alongside its markup anyway.
/// </para>
/// <para>
/// A served hit keeps the project and compilation snapshots it was parsed against, so the
/// tree, its symbols and those snapshots stay consistent with each other. Symbols read from
/// the tree may therefore be from an older snapshot than the caller's: anything that feeds
/// them into <c>SymbolFinder</c> against a newer solution has to re-anchor them first (see
/// <c>AspxReferenceService</c>), because Roslyn resolves a symbol's originating project by
/// compilation identity and silently returns nothing for a foreign snapshot's symbol.
/// </para>
/// </remarks>
internal static class AspxDocumentService
{
    private sealed record CacheEntry(
        ImmutableArray<byte> Checksum,
        VersionStamp SemanticVersion,
        ImmutableArray<(DocumentId Id, VersionStamp Version)> CodeBehindVersions,
        AspxDocument Document);

    private static readonly ConcurrentDictionary<string, CacheEntry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record WebConfigEntry(DateTime WriteTimeUtc, ImmutableArray<KeyValuePair<string, string>> Namespaces);

    private static readonly ConcurrentDictionary<string, WebConfigEntry> s_webConfig =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAspxFile(string filePath) => AspxSourceMappingService.IsAspxFile(filePath);

    /// <summary>
    /// Returns the parsed document, or <c>null</c> when the file is not ASPX-family, does not
    /// belong to a project, or that project has no compilation.
    /// </summary>
    public static async Task<AspxDocument?> GetAsync(string filePath, CancellationToken ct)
    {
        if (!IsAspxFile(filePath))
            return null;

        string path = PathHelper.NormalizePath(filePath);

        var sourceText = ReadSourceText(path);
        if (sourceText is null)
            return null;

        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(path, ct);
        if (string.IsNullOrEmpty(projectPath))
            return null;

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: path, cancellationToken: ct);

        // The staleness test runs before the compilation is asked for: on a hit, a hover in
        // markup after a C# body edit no longer waits for that edit to compile. The text is
        // compared by the buffer's own checksum — materializing an open buffer into a string
        // costs a full copy, and this runs on every request.
        var semanticVersion = await project.GetDependentSemanticVersionAsync(ct);
        var checksum = sourceText.GetChecksum();

        if (s_cache.TryGetValue(path, out var cached)
            && cached.SemanticVersion.Equals(semanticVersion)
            && cached.Checksum.SequenceEqual(checksum)
            && await UnchangedAsync(project, cached.CodeBehindVersions, ct))
        {
            return cached.Document;
        }

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return null;

        string? projectDir = Path.GetDirectoryName(projectPath);
        var namespaces = projectDir is null ? default : WebConfigNamespaces(projectDir);

        string text = sourceText.ToString();
        var parse = AspxSourceMappingService.Parse(
            path, text, compilation,
            namespaces: namespaces.IsDefaultOrEmpty ? null : namespaces,
            rootDirectory: projectDir);

        var document = new AspxDocument(
            path, text, sourceText, project, compilation, parse);

        s_cache[path] = new CacheEntry(
            checksum, semanticVersion, await CodeBehindVersionsAsync(project, document, ct), document);
        return document;
    }

    /// <summary>The text versions of the files declaring the code-behind class, so an edit in
    /// them — even a body edit the semantic version ignores — reparses and navigation into the
    /// class answers with the lines the user is looking at.</summary>
    private static async Task<ImmutableArray<(DocumentId Id, VersionStamp Version)>> CodeBehindVersionsAsync(
        Project project, AspxDocument document, CancellationToken ct)
    {
        if (document.CodeBehind is not { } codeBehind)
            return [];

        var versions = ImmutableArray.CreateBuilder<(DocumentId, VersionStamp)>();
        foreach (var reference in codeBehind.DeclaringSyntaxReferences)
        {
            foreach (var id in project.Solution.GetDocumentIdsWithFilePath(reference.SyntaxTree.FilePath))
            {
                if (project.Solution.GetDocument(id) is { } declaring)
                    versions.Add((id, await declaring.GetTextVersionAsync(ct)));
            }
        }

        return versions.ToImmutable();
    }

    private static async Task<bool> UnchangedAsync(
        Project project,
        ImmutableArray<(DocumentId Id, VersionStamp Version)> versions,
        CancellationToken ct)
    {
        foreach (var (id, version) in versions)
        {
            if (project.Solution.GetDocument(id) is not { } document
                || !(await document.GetTextVersionAsync(ct)).Equals(version))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Open buffer first — an unsaved edit is what the user is looking at.</summary>
    private static SourceText? ReadSourceText(string path)
    {
        if (OpenDocumentStore.TryGet(path, out var open))
            return open;

        try
        {
            return File.Exists(path) ? SourceText.From(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// web.config tag-prefix registrations, re-read only when the file's timestamp moves. Every
    /// keystroke in a markup file goes through here, and the parse needs them to bind a prefix.
    /// </summary>
    private static ImmutableArray<KeyValuePair<string, string>> WebConfigNamespaces(string projectDir)
    {
        DateTime writeTime = default;
        foreach (string name in new[] { "web.config", "Web.config" })
        {
            var info = new FileInfo(Path.Combine(projectDir, name));
            if (info.Exists)
            {
                writeTime = info.LastWriteTimeUtc;
                break;
            }
        }

        if (s_webConfig.TryGetValue(projectDir, out var entry) && entry.WriteTimeUtc == writeTime)
            return entry.Namespaces;

        var namespaces = AspxSourceMappingService.LoadWebConfigNamespaces(projectDir);
        s_webConfig[projectDir] = new WebConfigEntry(writeTime, namespaces);
        return namespaces;
    }

    /// <summary>
    /// The current project for a cached document, with <paramref name="symbol"/> re-resolved
    /// into it — for the gestures that search or edit. <c>SymbolFinder</c> and <c>Renamer</c>
    /// resolve a symbol's originating project by compilation identity, so a cached snapshot's
    /// symbol fed to the current solution silently finds nothing; and an edit computed against
    /// a stale solution lands on lines the user no longer has. Falls back to the document's own
    /// snapshot — consistent, merely older — when the symbol no longer resolves.
    /// </summary>
    public static async Task<(Project Project, ISymbol Symbol)> AnchorAsync(
        AspxDocument document, ISymbol symbol, CancellationToken ct)
    {
        var project = await CurrentProjectAsync(document, ct);

        if (ReferenceEquals(project.Solution, document.Project.Solution))
            return (project, symbol);

        if (await project.GetCompilationAsync(ct) is { } compilation
            && Microsoft.CodeAnalysis.FindSymbols.SymbolFinder
                .FindSimilarSymbols(symbol.OriginalDefinition, compilation, ct)
                .FirstOrDefault() is { } fresh)
        {
            return (project, fresh);
        }

        return (document.Project, symbol);
    }

    /// <summary>The current project for a cached document — for gestures whose answers are
    /// positions in text the user has now rather than in the snapshot the parse kept.</summary>
    public static async Task<Project> CurrentProjectAsync(AspxDocument document, CancellationToken ct)
    {
        if (document.Project.FilePath is not { Length: > 0 } projectPath)
            return document.Project;

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: document.FilePath, cancellationToken: ct);
        return project;
    }

    /// <summary>Drops a file's memoized parse — used when the file changes on disk under us.</summary>
    public static void Invalidate(string filePath) =>
        s_cache.TryRemove(PathHelper.NormalizePath(filePath), out _);

    /// <summary>
    /// Drops every memoized parse. For a <c>web.config</c> change, which is the one edit that
    /// changes how markup binds without changing any markup: the entries are keyed on the file's
    /// own text and the project's semantic version, neither of which moves when a tag prefix is
    /// registered, so
    /// every already-parsed page would keep reporting the control it now knows about as unknown.
    /// </summary>
    public static void InvalidateAll()
    {
        s_cache.Clear();
        s_webConfig.Clear();
    }
}
