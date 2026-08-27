using RoslynMCP.Lsp.Protocol;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>
/// Where a fact about a registration came from, and therefore whether it is knowable at all
/// without running the program.
/// </summary>
/// <remarks>
/// The distinction the tree exists to draw. <c>AddOrUpdate("nightly", …, "0 3 * * *")</c> and
/// <c>AddOrUpdate(id, …, _config["Jobs:Cron"])</c> are the same call, and a list that showed them
/// the same way would be lying about the second: nobody reading it can say what that job is called
/// or when it runs, and the honest answer is to say so rather than to print the expression that
/// fetched the value.
/// </remarks>
internal enum CronOrigin
{
    /// <summary>Written on the spot, in the call.</summary>
    Literal,

    /// <summary>A constant the compiler folded — a <c>const</c>, a <c>nameof</c>, a joined pair.</summary>
    Constant,

    /// <summary>Read from configuration at run time. The key is knowable; the value is not.</summary>
    Configuration,

    /// <summary>A parameter of the enclosing method, so the caller decides.</summary>
    Parameter,

    /// <summary>A local or field whose value this cannot follow.</summary>
    Variable,

    /// <summary>Something computed — a ternary, a call, an interpolation over live values.</summary>
    Expression,

    /// <summary>There is none. A removal carries no schedule.</summary>
    Absent,
}

/// <summary>
/// One fact about a registration: its text where that is knowable, and where the text came from.
/// </summary>
/// <param name="Text">
/// The value, when it is one a reader could have read off the source. Null otherwise — deliberately
/// not "the source text of the expression", which would render as a schedule that is not one.
/// </param>
/// <param name="Detail">
/// What is knowable when the value is not: the configuration key, the parameter's name, the
/// expression as written. This is what the row shows instead.
/// </param>
internal readonly record struct CronFacet(string? Text, CronOrigin Origin, string? Detail)
{
    /// <summary>Nothing was passed at all.</summary>
    public static CronFacet Absent { get; } = new(null, CronOrigin.Absent, null);

    /// <summary>
    /// Whether this is a fact about a run of the program rather than about the program.
    /// </summary>
    /// <remarks>
    /// <see cref="CronOrigin.Absent"/> is not dynamic. There being no schedule at all — a removal
    /// carries none — is itself knowable from the source, and marking it as unknowable would put
    /// a question mark on the one row whose story is complete.
    /// </remarks>
    public bool IsDynamic =>
        Origin is not (CronOrigin.Literal or CronOrigin.Constant or CronOrigin.Absent);
}

/// <summary>
/// One scheduled job, as the Solution Explorer shows it.
/// </summary>
/// <param name="Registration">Where the call is written — what clicking the row opens.</param>
/// <param name="Target">The job's own method, when one could be named.</param>
internal sealed record CronJob(
    CronFacet JobId,
    CronFacet Cron,
    CronFacet Method,
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
