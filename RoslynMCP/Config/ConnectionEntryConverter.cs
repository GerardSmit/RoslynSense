using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Config;

public sealed class ConnectionEntryConverter : JsonConverter<ConnectionEntry>
{
    public override ConnectionEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString() ?? throw new JsonException("Connection string cannot be null.");
            return ParseShorthand(raw);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? provider = null;
            string? connectionString = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("Expected property name in connection entry object.");

                var name = reader.GetString();
                reader.Read();

                if (string.Equals(name, "provider", StringComparison.OrdinalIgnoreCase))
                    provider = reader.GetString();
                else if (string.Equals(name, "connectionString", StringComparison.OrdinalIgnoreCase))
                    connectionString = reader.GetString();
                else
                    reader.Skip();
            }

            if (string.IsNullOrWhiteSpace(provider))
                throw new JsonException("Connection entry object missing 'provider'.");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new JsonException("Connection entry object missing 'connectionString'.");

            if (!DbProviderFactory.TryCanonicalize(provider, out var canonical))
                throw new JsonException($"Unknown provider '{provider}'. Use psql, mssql, or sqlite.");

            var resolved = ConnectionStringResolver.Resolve(connectionString);
            return new ConnectionEntry(canonical, resolved);
        }

        throw new JsonException($"Connection entry must be a string or object, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, ConnectionEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("provider", value.Provider);
        writer.WriteString("connectionString", value.ConnectionString);
        writer.WriteEndObject();
    }

    private static ConnectionEntry ParseShorthand(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
            throw new JsonException($"Connection string '{value}' is missing a provider prefix (e.g. 'psql:', 'mssql:', 'sqlite:').");

        var providerToken = value[..colon];
        if (!DbProviderFactory.TryCanonicalize(providerToken, out var canonical))
            throw new JsonException($"Connection string '{value}' has unknown provider '{providerToken}'. Use psql, mssql, or sqlite.");

        var connRef = value[(colon + 1)..];
        if (string.IsNullOrWhiteSpace(connRef))
            throw new JsonException($"Connection string '{value}' has empty connection string.");

        var resolved = ConnectionStringResolver.Resolve(connRef);
        return new ConnectionEntry(canonical, resolved);
    }
}
