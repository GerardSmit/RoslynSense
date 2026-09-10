using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Debugging;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.DiaSymReader;

namespace RoslynMCP.Services.HotReload;

/// <summary>
/// Keeps EnC document paths in the exact spelling recorded by the baseline compiler.
/// </summary>
/// <remarks>
/// Windows editors and MSBuild can disagree on path casing, especially the drive letter.
/// Roslyn looks up PDB documents with an ordinal comparison and silently treats a miss as a
/// design-time-only document, so its text edits otherwise produce no delta. Only the EnC
/// snapshots change here; document identities, text, and the shared workspace stay intact.
/// </remarks>
internal sealed class EncPdbDocumentPaths
{
    private readonly Dictionary<ProjectId, Dictionary<string, string?>> _projects = [];

    public static EncPdbDocumentPaths Read(Solution solution, CancellationToken cancellationToken)
    {
        var result = new EncPdbDocumentPaths();
        if (!OperatingSystem.IsWindows())
            return result;

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Use EnC's output resolver, including custom CodeView paths and embedded PDBs.
                var outputs = new CompilationOutputFilesWithImplicitPdbPath(
                    project.CompilationOutputInfo.AssemblyPath);
                using var provider = outputs.OpenPdb();
                if (provider is null)
                    continue;

                var paths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in ReadPaths(provider))
                {
                    // A case-sensitive Windows directory can contain distinct files differing
                    // only in case. An ambiguous PDB entry must never redirect one to the other.
                    if (paths.TryGetValue(path, out var existing)
                        && !StringComparer.Ordinal.Equals(existing, path))
                        paths[path] = null;
                    else
                        paths[path] = path;
                }
                result._projects.Add(project.Id, paths);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException
                or UnauthorizedAccessException or COMException or DllNotFoundException
                or NotSupportedException or InvalidOperationException)
            {
                // Path correction is optional. Leave unavailable or unreadable outputs to
                // EnC itself so its normal build/PDB diagnostics are preserved.
            }
        }
        return result;
    }

    public Solution Apply(Solution solution, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_projects.TryGetValue(project.Id, out var paths))
                continue;

            foreach (var document in project.Documents)
                if (document.FilePath is { } current
                    && paths.TryGetValue(current, out var recorded)
                    && recorded is not null
                    && !StringComparer.Ordinal.Equals(current, recorded))
                    solution = solution.WithDocumentFilePath(document.Id, recorded);
        }
        return solution;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadPaths(DebugInformationReaderProvider provider)
    {
        // Features is already publicized: typed access keeps Roslyn's portable/native loading
        // and ownership in one place and makes an API change a compile-time failure.
        if (provider is DebugInformationReaderProvider.Portable portable)
        {
            var reader = portable._pdbReaderProvider.GetMetadataReader();
            foreach (var handle in reader.Documents)
                yield return reader.GetString(reader.GetDocument(handle).Name);
        }
        else if (provider is DebugInformationReaderProvider.Native native)
        {
            foreach (var document in native._symReader!.GetDocuments())
            {
                string path;
                try { path = document.GetName(); }
                finally
                {
                    if (Marshal.IsComObject(document))
                        Marshal.ReleaseComObject(document);
                }
                yield return path;
            }
        }
    }
}
