using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage : ILanguageWorkspaceDiagnosticContributor
{
    /// <summary>
    /// Every markup file under the project, diagnosed without opening any of them. An
    /// <c>OnClick=</c> naming a handler that no longer exists is a build-clean runtime failure,
    /// so it has to reach Problems from a closed file the way a C# error does.
    /// </summary>
    /// <remarks>
    /// The file listing is the cached walk find-references already keeps warm, and the result id
    /// is checked before anything is parsed — a sweep over a site whose markup has not moved
    /// answers entirely out of <paramref name="previousResultIds"/> and never reads a parse tree.
    /// </remarks>
    public async Task<IReadOnlyList<object>> DiagnoseProjectAsync(
        Project project,
        IReadOnlyDictionary<string, string> previousResultIds,
        CancellationToken ct)
    {
        var files = AspxReferenceService.EnumerateFiles(project);
        if (files.Count == 0)
            return [];

        // Markup binds against the code-behind, so a handler appearing or disappearing changes a
        // page's diagnostics without touching its own text. The project's semantic version is
        // what makes that visible to a content hash.
        var semanticVersion = await project.GetDependentSemanticVersionAsync(ct);

        // Built once for the sweep: which files are include targets — answered from includer
        // scope, never their own — and which files' contents feed into which result ids.
        var graph = AspxIncludeService.GetGraph(project);

        var reports = new List<object>(files.Count);

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            string uri = LspConverters.PathToUri(file);
            string? resultId = ResultId(file, semanticVersion, graph);

            if (resultId is not null
                && previousResultIds.TryGetValue(uri, out string? previous)
                && previous == resultId)
            {
                reports.Add(new WorkspaceUnchangedDocumentDiagnosticReport("unchanged", uri, resultId));
                continue;
            }

            var items = await AspxLanguageHandler.DiagnosticsAsync(file, graph, ct);
            reports.Add(new WorkspaceFullDocumentDiagnosticReport("full", uri, items)
            {
                ResultId = resultId,
            });
        }

        return reports;
    }

    /// <summary>
    /// The version the client hands back on the next sweep, or null when the file cannot be read
    /// — in which case the report is sent in full rather than claimed unchanged.
    /// </summary>
    /// <remarks>
    /// Hashing the content rather than stamping the write time: an editor that saves without
    /// changing anything, and a branch switch that restores a file, both move the timestamp, and
    /// re-reporting a solution's worth of markup is exactly what the unchanged report exists to
    /// avoid. The open buffer wins over the disk for the same reason every other read here does.
    /// </remarks>
    private static string? ResultId(string path, VersionStamp semanticVersion, AspxIncludeGraph graph)
    {
        byte[]? content = ReadAllBytes(path);
        if (content is null)
            return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(content);

        // A file's diagnostics also move when a fragment it includes is edited, and — for an
        // include target — when the page whose scope it is judged in changes. Fold those files
        // in, or the sweep answers "unchanged" over a stale report. For the common file with no
        // include edges the closure is the file itself and this loop appends nothing.
        foreach (string member in graph.Closure(path))
        {
            if (string.Equals(member, path, StringComparison.OrdinalIgnoreCase))
                continue;

            hash.AppendData(Encoding.UTF8.GetBytes(member.ToUpperInvariant()));
            hash.AppendData(ReadAllBytes(member) ?? "missing"u8.ToArray());
        }

        return $"{Convert.ToHexString(hash.GetHashAndReset())}:{semanticVersion}";
    }

    private static byte[]? ReadAllBytes(string path)
    {
        try
        {
            return OpenDocumentStore.TryGet(path, out var open)
                ? Encoding.UTF8.GetBytes(open.ToString())
                : File.ReadAllBytes(path);
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
}
