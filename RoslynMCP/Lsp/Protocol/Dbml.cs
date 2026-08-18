using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>
/// The wire shapes of the <c>.dbml</c> refresh commands.
/// </summary>
/// <remarks>
/// Explicitly named, like the rest of the protocol here: the session's serializer applies no naming
/// policy, so a property without an attribute goes out in PascalCase and the client reads
/// <c>undefined</c>.
/// </remarks>
public sealed record DbmlConnection(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("provider")] string Provider);

/// <summary>The registered connections a refresh can be run against.</summary>
/// <param name="Unsupported">Aliases that exist but cannot describe a schema, so the client can say
/// why a connection it can see is not in the list rather than appearing to have lost it.</param>
public sealed record DbmlConnectionList(
    [property: JsonPropertyName("connections")] DbmlConnection[] Connections,
    [property: JsonPropertyName("unsupported")] string[] Unsupported);

/// <summary>One column in a planned refresh, as a line the confirmation can show.</summary>
public sealed record DbmlPlannedColumn(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>What refreshing a table would do, for the client to show before it does it.</summary>
public sealed record DbmlRefreshPlanResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("table")] string? Table = null,
    [property: JsonPropertyName("added")] DbmlPlannedColumn[]? Added = null,
    [property: JsonPropertyName("updated")] DbmlPlannedColumn[]? Updated = null,
    [property: JsonPropertyName("removed")] DbmlPlannedColumn[]? Removed = null,
    [property: JsonPropertyName("associations")] DbmlPlannedColumn[]? Associations = null,
    [property: JsonPropertyName("notes")] string[]? Notes = null);

/// <summary>The outcome of a write.</summary>
public sealed record DbmlRefreshResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message);
