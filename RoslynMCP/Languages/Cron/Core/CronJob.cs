using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Symbols;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>
/// One scheduled job, as the Discovery view shows it.
/// </summary>
/// <param name="Registration">Where the call is written — what clicking the row opens.</param>
/// <param name="Target">The job's own method, when one could be named.</param>
internal sealed record CronJob(
    RegistrationFacet JobId,
    RegistrationFacet Cron,
    RegistrationFacet Method,
    CronLibrary Library,
    CronRegistrationKind Kind,
    CronDialect Dialect,
    string ProjectPath,
    string FilePath,
    int Offset,
    Range Registration,
    Range? Target = null,
    string? TargetUri = null)
{
    /// <summary>Whether any part of this job is only knowable at run time.</summary>
    public bool IsDynamic => JobId.IsDynamic || Cron.IsDynamic || Method.IsDynamic;
}
