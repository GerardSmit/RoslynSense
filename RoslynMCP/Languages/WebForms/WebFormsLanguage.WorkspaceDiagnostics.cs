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

        var reports = new List<object>(files.Count);

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            string uri = LspConverters.PathToUri(file);
            string? resultId = ResultId(file, semanticVersion);

            if (resultId is not null
                && previousResultIds.TryGetValue(uri, out string? previous)
                && previous == resultId)
            {
                reports.Add(new WorkspaceUnchangedDocumentDiagnosticReport("unchanged", uri, resultId));
                continue;
            }

            var items = await AspxLanguageHandler.DiagnosticsAsync(file, ct);
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
    private static string? ResultId(string path, VersionStamp semanticVersion)
    {
        try
        {
            byte[] content = OpenDocumentStore.TryGet(path, out var open)
                ? Encoding.UTF8.GetBytes(open.ToString())
                : File.ReadAllBytes(path);

            return $"{Convert.ToHexString(SHA256.HashData(content))}:{semanticVersion}";
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
