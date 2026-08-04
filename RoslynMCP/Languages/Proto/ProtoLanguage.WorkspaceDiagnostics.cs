using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageWorkspaceDiagnosticContributor
{
    /// <summary>
    /// Every <c>.proto</c> the project compiles, diagnosed without opening any of them. A field
    /// renumbered in a contract nobody has open is a wire-breaking change that no C# compiler will
    /// ever complain about, so it has to reach Problems from a closed file the way a C# error does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file list is the one <see cref="ProtoWorkspace.ProtoFilesAsync"/> already keeps warm, and
    /// it costs nothing in a project with no protos: the walk behind it is memoized per project file
    /// and answers empty before a compilation is ever asked for.
    /// </para>
    /// <para>
    /// No <c>HostsProtobufAsync</c> gate, unlike the reference contributors. That gate asks whether
    /// <c>Google.Protobuf</c> resolves, which it does not in a solution that has been cloned but not
    /// restored — exactly the state in which a syntax error in a <c>.proto</c> most needs reporting,
    /// and one where the pack has a perfectly good answer that needs no compilation at all.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<object>> DiagnoseProjectAsync(
        Project project,
        IReadOnlyDictionary<string, string> previousResultIds,
        CancellationToken ct)
    {
        var files = await ProtoWorkspace.ProtoFilesAsync(project, ct);
        if (files.IsDefaultOrEmpty)
            return [];

        // A proto binds against the C# protoc generated from it, so a build appearing or vanishing
        // changes a file's diagnostics without touching its own text.
        var semanticVersion = await project.GetDependentSemanticVersionAsync(ct);

        var contents = new List<(string Path, byte[]? Content)>(files.Length);

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            contents.Add((file, ReadBytes(file)));
        }

        string digest = Digest(contents);
        var reports = new List<object>(contents.Count);

        foreach (var (file, content) in contents)
        {
            ct.ThrowIfCancellationRequested();

            string uri = LspConverters.PathToUri(file);
            string? resultId = ResultId(content, digest, semanticVersion);

            if (resultId is not null
                && previousResultIds.TryGetValue(uri, out string? previous)
                && previous == resultId)
            {
                reports.Add(new WorkspaceUnchangedDocumentDiagnosticReport("unchanged", uri, resultId));
                continue;
            }

            var items = await ProtoDiagnosticsHandler.DiagnosticsAsync(file, ct);
            reports.Add(new WorkspaceFullDocumentDiagnosticReport("full", uri, items)
            {
                ResultId = resultId,
            });
        }

        return reports;
    }

    /// <summary>
    /// The version the client hands back on the next sweep, or null when the file could not be read
    /// — in which case the report is sent in full rather than claimed unchanged.
    /// </summary>
    /// <remarks>
    /// The file's own bytes are not enough, which is the one place this departs from the markup
    /// sweep. Half of what a <c>.proto</c> is diagnosed for is decided by the files it imports: a
    /// message deleted from <c>common.proto</c> makes every reference to it in <c>widgets.proto</c>
    /// unresolvable while both the text of <c>widgets.proto</c> and the compilation stand still, and
    /// a result id built from either alone would report the file unchanged and leave the editor
    /// showing a squiggle that is no longer true — or hiding one that is. <paramref name="digest"/>
    /// is the whole project's proto text, so any edit anywhere in the import graph re-diagnoses the
    /// files that can see it. It re-diagnoses the ones that cannot as well, which is the same
    /// over-reporting the dependent semantic version already causes on the C# side and is paid in
    /// parses that are all memoized.
    /// </remarks>
    private static string? ResultId(byte[]? content, string digest, VersionStamp semanticVersion) =>
        content is null
            ? null
            : $"{Convert.ToHexString(SHA256.HashData(content))}:{digest}:{semanticVersion}";

    /// <summary>One value standing for the text of every <c>.proto</c> in the project.</summary>
    private static string Digest(List<(string Path, byte[]? Content)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var (path, content) in files)
        {
            // The path as well as the text, so that adding a file — or renaming one, which is how a
            // proto stops being imported — moves the digest even when no byte of any file changed.
            hash.AppendData(Encoding.UTF8.GetBytes(path));

            if (content is not null)
                hash.AppendData(content);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// The file's bytes, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Hashing the content rather than stamping the write time: an editor that saves without
    /// changing anything, and a branch switch that restores a file, both move the timestamp, and
    /// re-reporting a solution's worth of schemas is exactly what the unchanged report exists to
    /// avoid. The open buffer wins over the disk for the same reason every other read in the pack
    /// does — an unsaved edit is what the user is looking at.
    /// </remarks>
    private static byte[]? ReadBytes(string path)
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
