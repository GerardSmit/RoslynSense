using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp;

/// <summary>
/// Maps the synthetic path Roslyn gives a source-generated document to the URI the editor can
/// open it with.
/// </summary>
/// <remarks>
/// <para>
/// A generated document's <c>FilePath</c> is <c>{generator assembly}\{generator type}\{hint
/// name}</c> — descriptive, but nothing on disk. Without this map every location inside
/// generated code converts to a file URI for a file that does not exist, so "go to definition"
/// on a generated member opened nothing and "find all references" silently dropped the
/// generated half of its results.
/// </para>
/// <para>
/// Filled in whenever generated documents are enumerated for some other reason — opening one,
/// listing them in the Solution Explorer, or a search that turned one up. A path that is not in
/// the map converts as it did before, so an unwarmed lookup degrades to the old behaviour
/// rather than to an error.
/// </para>
/// </remarks>
internal static class GeneratedDocumentRegistry
{
    private static readonly ConcurrentDictionary<string, string> s_uriByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records every generated document of a project.</summary>
    public static void Register(string projectPath, IEnumerable<SourceGeneratedDocument> documents)
    {
        foreach (var document in documents)
        {
            if (document.FilePath is not { Length: > 0 } path)
                continue;

            s_uriByPath[path] = Handlers.VirtualDocumentHandler.UriFor(
                Handlers.VirtualDocumentHandler.GeneratedScheme, projectPath, HintName(document));
        }
    }

    public static bool TryGetUri(string syntheticPath, out string uri) =>
        s_uriByPath.TryGetValue(syntheticPath, out uri!);

    /// <summary>
    /// Whether a path looks like generated output rather than a file. Used to decide whether a
    /// miss is worth an enumeration, so the cost is paid only when generated code is actually
    /// in the results.
    /// </summary>
    /// <remarks>
    /// Being rooted proves nothing. MSBuildWorkspace fills in
    /// <c>CompilationOutputInfo.GeneratedFilesOutputDirectory</c>, so Roslyn synthesizes the path
    /// under the project's <c>obj\</c> — rooted, and still nothing on disk unless
    /// <c>EmitCompilerGeneratedFiles</c> happened to write a copy there. Treating rooted as real
    /// is what sent F12 on a generated member to a path no build had ever produced.
    /// </remarks>
    public static bool LooksGenerated(string path) => path.Length > 0 && !File.Exists(path);

    internal static string HintName(SourceGeneratedDocument document) =>
        document.HintName is { Length: > 0 } hint ? hint : document.Name;
}
