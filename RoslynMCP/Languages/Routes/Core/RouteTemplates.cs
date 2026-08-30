using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Routes.Core;

/// <summary>
/// Putting a prefix and a template together, and expanding the tokens ASP.NET substitutes.
/// </summary>
/// <remarks>
/// The arithmetic of the section, and the part most worth pinning by a test: every rule in here is
/// a rule of the framework rather than a choice, so getting one wrong produces a row that is
/// plausible, well-formed and served by nobody.
/// </remarks>
internal static class RouteTemplates
{
    /// <summary>The suffix a controller's type name carries and its route does not.</summary>
    private const string ControllerSuffix = "Controller";

    /// <summary>
    /// A prefix and a template, as one path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two framework rules and no invention. In an attribute, a template beginning <c>/</c> or
    /// <c>~/</c> is absolute and discards the prefix — that is what those characters are for, and a
    /// codebase uses them precisely where an action must escape its controller's route. Anything
    /// else is relative and is joined with a single separator, whichever side wrote one.
    /// </para>
    /// <para>
    /// A registration escapes nothing, which is why the source has to be said out loud. A minimal
    /// API's pattern is conventionally written with a leading slash and still sits under whatever
    /// group registered it: <c>MapGroup("/api")</c> then <c>MapGet("/orders")</c> serves
    /// <c>/api/orders</c>, and applying the attribute rule here would list it as <c>/orders</c> —
    /// a path the application does not serve.
    /// </para>
    /// <para>
    /// A dynamic part poisons the whole path rather than being dropped from it. A prefix nobody
    /// could read makes every action under it unknowable, and rendering the readable half would
    /// print a path the application does not serve — the one outcome a list of endpoints must not
    /// produce, because a reader has no way to tell it is wrong.
    /// </para>
    /// </remarks>
    public static RegistrationFacet Combine(
        RegistrationFacet prefix, RegistrationFacet template, RouteSource source)
    {
        if (prefix.IsDynamic)
            return Unknown(prefix, "prefix");

        if (template.IsDynamic)
            return Unknown(template, "path");

        // Neither half was written, which is a controller routed by a convention this pack has not
        // read — the MapControllerRoute pattern, or an MVC default. The action is real and worth a
        // row; its path is not this pack's to state.
        if (prefix.Origin == RegistrationOrigin.Absent
            && template.Origin == RegistrationOrigin.Absent)
        {
            return new RegistrationFacet(null, RegistrationOrigin.Expression, "convention");
        }

        string head = Trim(prefix.Text);
        string tail = Trim(template.Text);

        // Absolute, so the prefix is not merely ignored — it is overridden, which is what the
        // leading slash means in an attribute and does not mean in a registration.
        if (source == RouteSource.Attribute
            && template.Text is { Length: > 0 } written
            && (written.StartsWith('/') || written.StartsWith("~/", StringComparison.Ordinal)))
        {
            head = string.Empty;
        }

        string joined = (head, tail) switch
        {
            ("", "") => "/",
            ("", _) => "/" + tail,
            (_, "") => "/" + head,
            _ => $"/{head}/{tail}",
        };

        // Literal only when both halves were. A path folded out of two constants is a constant.
        var origin = prefix.Origin == RegistrationOrigin.Literal
            && template.Origin is RegistrationOrigin.Literal or RegistrationOrigin.Absent
                ? RegistrationOrigin.Literal
                : RegistrationOrigin.Constant;

        return new RegistrationFacet(joined, origin, null);
    }

    /// <summary>
    /// The tokens ASP.NET replaces before it matches anything.
    /// </summary>
    /// <remarks>
    /// <c>[controller]</c> and <c>[action]</c> are not placeholders a request fills in — they are
    /// substituted at startup from the names of the type and the method, both of which are written
    /// in the source. Leaving them in place would make every controller's rows read
    /// <c>api/[controller]/{id}</c>, which is the template rather than the route, and identical
    /// across a solution.
    /// </remarks>
    public static RegistrationFacet Expand(
        RegistrationFacet path, string? controller, string? action)
    {
        if (path.Text is not { Length: > 0 } text || !text.Contains('[', StringComparison.Ordinal))
            return path;

        if (controller is { Length: > 0 })
            text = Replace(text, "[controller]", Trimmed(controller));

        if (action is { Length: > 0 })
            text = Replace(text, "[action]", action);

        // [area] is left alone on purpose. Its value comes from an [Area] attribute or from a
        // convention this pack has not read, so substituting a guess would be worse than showing
        // the token — which at least says out loud that something is missing.
        return path with { Text = text };
    }

    /// <summary>A controller's type name as its route spells it.</summary>
    public static string Trimmed(string typeName) =>
        typeName.EndsWith(ControllerSuffix, StringComparison.Ordinal)
            && typeName.Length > ControllerSuffix.Length
            ? typeName[..^ControllerSuffix.Length]
            : typeName;

    /// <summary>What is left of a path once it can no longer be printed.</summary>
    private static RegistrationFacet Unknown(RegistrationFacet part, string what) =>
        new(null, part.Origin, part.Detail is { Length: > 0 } detail ? $"{what}: {detail}" : what);

    private static string Trim(string? part) =>
        part?.TrimStart('~').Trim('/') ?? string.Empty;

    private static string Replace(string text, string token, string value) =>
        text.Replace(token, value, StringComparison.OrdinalIgnoreCase);
}
