namespace RoslynMCP.Services.Symbols;

/// <summary>
/// Where a fact about a registration came from, and therefore whether it is knowable at all
/// without running the program.
/// </summary>
/// <remarks>
/// <para>
/// The distinction a list of registrations exists to draw. <c>AddOrUpdate("nightly", …, "0 3 * *
/// *")</c> and <c>AddOrUpdate(id, …, _config["Jobs:Cron"])</c> are the same call, and a list that
/// showed them the same way would be lying about the second: nobody reading it can say what that
/// job is called or when it runs, and the honest answer is to say so rather than to print the
/// expression that fetched the value. The same is true of <c>MapGet("/orders", …)</c> beside
/// <c>MapGet(Routes.Orders, …)</c> beside <c>MapGet(prefix + "/orders", …)</c>.
/// </para>
/// <para>
/// Shared rather than owned by one pack because the question is not about schedules or about URLs.
/// It is about what a reader of the source is entitled to conclude, and a second pack copying the
/// answer would be a second place for the two to drift apart.
/// </para>
/// </remarks>
internal enum RegistrationOrigin
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

    /// <summary>There is none. A removal carries no schedule; a bare <c>MapGet</c> group has no path.</summary>
    Absent,
}

/// <summary>
/// One fact about a registration: its text where that is knowable, and where the text came from.
/// </summary>
/// <param name="Text">
/// The value, when it is one a reader could have read off the source. Null otherwise — deliberately
/// not "the source text of the expression", which would render as a schedule, or a URL, that is not
/// one.
/// </param>
/// <param name="Detail">
/// What is knowable when the value is not: the configuration key, the parameter's name, the
/// expression as written. This is what the row shows instead.
/// </param>
internal readonly record struct RegistrationFacet(
    string? Text, RegistrationOrigin Origin, string? Detail)
{
    /// <summary>Nothing was passed at all.</summary>
    public static RegistrationFacet Absent { get; } = new(null, RegistrationOrigin.Absent, null);

    /// <summary>
    /// Whether this is a fact about a run of the program rather than about the program.
    /// </summary>
    /// <remarks>
    /// <see cref="RegistrationOrigin.Absent"/> is not dynamic. There being no value at all — a
    /// removal carries no schedule — is itself knowable from the source, and marking it as
    /// unknowable would put a question mark on the one row whose story is complete.
    /// </remarks>
    public bool IsDynamic =>
        Origin is not (RegistrationOrigin.Literal
            or RegistrationOrigin.Constant
            or RegistrationOrigin.Absent);
}
