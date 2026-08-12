using RoslynMCP.Languages.MsBuild.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.MsBuild;

/// <summary>Package diagnostics on a project file.</summary>
internal sealed partial class MsBuildLanguage : ILanguageDiagnosticProvider
{
    /// <summary>
    /// Synchronous work behind an async signature, and deliberately so.
    /// </summary>
    /// <remarks>
    /// The interface is async because most packs parse or bind to answer. This one only reads what
    /// is already cached — anything it does not know it reports nothing about, and fetches behind
    /// the scenes. A <c>Task.FromResult</c> here is the visible form of the invariant: there is
    /// nothing to await, because awaiting is what this path may never do.
    /// </remarks>
    public Task<Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct) =>
        Task.FromResult(MsBuildDiagnosticsHandler.Compute(filePath));
}
