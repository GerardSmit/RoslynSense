using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageWorkspaceDiagnosticContributor
{
    /// <summary>
    /// Every <c>.dbml</c> the project generates from, diagnosed without opening any of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file list comes from the project's own documents rather than from its MSBuild items, which
    /// is the shortcut the protobuf pack does not get: a <c>.dbml</c> is a <c>None</c> item and is
    /// invisible to Roslyn, but the <c>.designer.cs</c> beside it is a <c>Compile</c> item and is
    /// therefore already in <see cref="Project.Documents"/>. Walking those and asking which ones have
    /// a model beside them costs a dictionary lookup per document and needs no project file parsed.
    /// </para>
    /// <para>
    /// The consequence is that a model whose designer has never been generated is not swept. That is
    /// the right side to fail on: the one diagnostic it would earn says the designer is missing, and
    /// reporting that about a closed file the user has not looked at is noise.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<object>> DiagnoseProjectAsync(
        Project project,
        IReadOnlyDictionary<string, string> previousResultIds,
        CancellationToken ct)
    {
        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();

            if (document.FilePath is not { Length: > 0 } path
                || DbmlSourceMappingService.ModelPathFor(path) is not { } model
                || !seen.Add(model)
                || !File.Exists(model))
            {
                continue;
            }

            models.Add(model);
        }

        if (models.Count == 0)
            return [];

        // The binding half of a model's diagnostics is decided by the compilation, so a build
        // appearing or vanishing changes them without a byte of the file moving.
        var semanticVersion = await project.GetDependentSemanticVersionAsync(ct);
        var reports = new List<object>(models.Count);

        foreach (string model in models)
        {
            ct.ThrowIfCancellationRequested();

            string uri = LspConverters.PathToUri(model);
            string? resultId = ResultId(model, semanticVersion);

            if (resultId is not null
                && previousResultIds.TryGetValue(uri, out string? previous)
                && previous == resultId)
            {
                reports.Add(new WorkspaceUnchangedDocumentDiagnosticReport("unchanged", uri, resultId));
                continue;
            }

            var items = await DiagnosticsAsync(model, ct);
            reports.Add(new WorkspaceFullDocumentDiagnosticReport("full", uri, items)
            {
                ResultId = resultId,
            });
        }

        return reports;
    }

    /// <summary>
    /// The version the client hands back on the next sweep, or null when the file could not be read —
    /// in which case the report is sent in full rather than claimed unchanged.
    /// </summary>
    /// <remarks>
    /// A model is diagnosed against itself and against the compilation and against nothing else — it
    /// imports nothing — so unlike the protobuf sweep there is no graph to hash, and the file's own
    /// bytes plus the semantic version are the whole of what the answer depends on. The open buffer
    /// wins over the disk for the reason every other read in the pack does: an unsaved edit is what
    /// the user is looking at.
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
