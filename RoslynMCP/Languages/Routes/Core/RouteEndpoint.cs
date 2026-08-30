using RoslynMCP.Services.Symbols;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Routes.Core;

/// <summary>
/// One HTTP endpoint, as the Discovery view shows it.
/// </summary>
/// <param name="Path">
/// The template, already combined with whatever prefix its type or its group contributes. Dynamic
/// when any part of it is — a prefix read from configuration makes the whole path unknowable, and
/// showing the half that was readable would be a path nobody serves.
/// </param>
/// <param name="Verb">
/// The HTTP method, or null when the declaration constrains none. Null is not "GET": an action
/// reachable by every verb is a deliberate thing, and inventing one would be a row a reader could
/// act on wrongly.
/// </param>
/// <param name="Handler">What runs. Absent for a lambda, which is not a method there is a name for.</param>
/// <param name="Declaration">Where the route is written — what clicking the row opens.</param>
/// <param name="Target">The handler method's own name, when there is one to open.</param>
internal sealed record RouteEndpoint(
    RegistrationFacet Path,
    string? Verb,
    RegistrationFacet Handler,
    RouteSource Source,
    string ProjectPath,
    string FilePath,
    int Offset,
    Range Declaration,
    Range? Target = null,
    string? TargetUri = null)
{
    /// <summary>
    /// Whether the path is only knowable at run time.
    /// </summary>
    /// <remarks>
    /// The path alone, deliberately, though a handler can be unreadable too. What the mark governs
    /// is whether the row's path may be copied and used, and an endpoint whose path is written
    /// plainly is exactly as usable when the delegate behind it came out of a field.
    /// </remarks>
    public bool IsDynamic => Path.IsDynamic;
}
