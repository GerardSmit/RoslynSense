using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record PublishDiagnosticsParams(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int? Version,
    [property: JsonPropertyName("diagnostics")] Diagnostic[] Diagnostics);

public sealed record Diagnostic(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("severity")] int Severity, // 1 error, 2 warning, 3 info, 4 hint
    [property: JsonPropertyName("code"), JsonConverter(typeof(StringOrNumberConverter))] string? Code,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("message")] string Message)
{
    /// <summary>
    /// Where the reader can go to understand the code — rendered by the client as a link on it.
    /// </summary>
    /// <remarks>
    /// Init-only rather than a sixth positional parameter, so the hundred or so existing
    /// constructions do not have to name it. The alternative to having it at all is putting the URL
    /// in the message text, which clients do linkify but which reads as noise on every diagnostic
    /// that has one — and a security advisory is exactly the case where the link is the point.
    /// </remarks>
    [JsonPropertyName("codeDescription")]
    public CodeDescription? CodeDescription { get; init; }

    /// <summary>
    /// LSP diagnostic tags: 1 unnecessary (rendered faded), 2 deprecated (rendered struck through).
    /// </summary>
    [JsonPropertyName("tags")]
    public int[]? Tags { get; init; }
}

/// <summary>The documentation a diagnostic code links to.</summary>
public sealed record CodeDescription(
    [property: JsonPropertyName("href")] string Href);

/// <summary>The <c>tags</c> values LSP defines.</summary>
public static class LspDiagnosticTag
{
    public const int Unnecessary = 1;
    public const int Deprecated = 2;
}

/// <summary>LSP allows <c>integer | string</c> for diagnostic codes. Clients echo third-party
/// diagnostics back in codeAction context — a numeric code must not blow up deserialization.</summary>
public sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out long l)
                ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for diagnostic code."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
