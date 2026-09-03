using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>
/// What implements the thing a Discovery row names, and — when the answer is nothing — why.
/// </summary>
/// <remarks>
/// <para>
/// The reason is the whole point of this type, and the reason it is not just a
/// <see cref="Location"/> array. An empty list means four different things when the row is an rpc:
/// the project has never been built, so there is no generated code to match against; the
/// declaration was added or renamed since the last build; the name is one of protoc's own types
/// whose C# lives in a runtime package; or the contract genuinely has no implementation in this
/// solution because the other side of the wire is somewhere else.
/// </para>
/// <para>
/// Those need four different next steps, and only the server can tell them apart — it is the side
/// that knows whether the generated index is empty. Returning a bare list would leave the client
/// guessing, and the guess it would have to make is "build the project", which is wrong three
/// times out of four and insulting the once the user already has.
/// </para>
/// </remarks>
/// <param name="Reason">
/// Null whenever <paramref name="Locations"/> has anything in it. A reason beside results would be
/// a message with nothing to do.
/// </param>
public sealed record DiscoveryImplementationsResult(
    [property: JsonPropertyName("locations")] Location[] Locations,
    [property: JsonPropertyName("reason")] string? Reason = null)
{
    /// <summary>Nothing found, and this is why.</summary>
    public static DiscoveryImplementationsResult None(string reason) => new([], reason);

    /// <summary>What came back, with no reason needed.</summary>
    public static DiscoveryImplementationsResult Found(Location[] locations) => new(locations);
}
