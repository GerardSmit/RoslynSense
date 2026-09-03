namespace RoslynMCP.Languages.Proto.Core;

/// <summary>
/// A declaration written back out as protobuf, for the front-ends that show one to a reader —
/// hover, the outline detail, the MCP reports.
/// </summary>
/// <remarks>
/// Here rather than in each of them because none of it is presentation. A <c>map</c> is parsed into
/// two type references and has to be put back together to be readable at all, a <c>stream</c> is a
/// flag rather than part of the type it qualifies, and a label is an enum whose spellings are
/// protobuf's own — three pieces of grammar knowledge, and every copy of them is a copy that can
/// drift from what the file actually says.
/// </remarks>
internal static class ProtoDeclarationText
{
    /// <summary>The declaration as it would be written, statement and all.</summary>
    public static string Signature(ProtoDeclaration declaration) => declaration switch
    {
        ProtoMessage message => $"message {message.Name.Value}",
        ProtoEnum @enum => $"enum {@enum.Name.Value}",
        ProtoEnumValue value => $"{value.Name.Value} = {value.Number};",
        ProtoField field => $"{Label(field)}{TypeText(field)} {field.Name.Value} = {field.Number};",
        ProtoOneof oneof => $"oneof {oneof.Name.Value}",
        ProtoService service => $"service {service.Name.Value}",
        ProtoRpc rpc => $"rpc {rpc.Name.Value}{Parameters(rpc)};",
        ProtoExtend extend => $"extend {extend.Target.Text}",
        _ => declaration.Name.Value,
    };

    /// <summary>An rpc's <c>(Request) returns (Reply)</c>, which is the whole of what distinguishes
    /// one rpc from another and the only place <c>stream</c> appears.</summary>
    public static string Parameters(ProtoRpc rpc) =>
        $"({TypeText(rpc.ClientStreaming, rpc.RequestType)})"
        + $" returns ({TypeText(rpc.ServerStreaming, rpc.ResponseType)})";

    /// <summary>The field's label with its trailing space, or nothing for a proto3 field that
    /// carries none.</summary>
    public static string Label(ProtoField field) => field.Label switch
    {
        ProtoFieldLabel.Optional => "optional ",
        ProtoFieldLabel.Required => "required ",
        ProtoFieldLabel.Repeated => "repeated ",
        _ => string.Empty,
    };

    /// <summary>The field's type as written. A map is one type in the source and two in the parse,
    /// so its key comes back out of the field it was parsed off.</summary>
    public static string TypeText(ProtoField field) =>
        field.MapKeyType is { } key ? $"map<{key.Text}, {field.Type.Text}>" : field.Type.Text;

    /// <summary>One side of an rpc, <c>stream</c> included.</summary>
    public static string TypeText(bool streaming, ProtoTypeRef type) =>
        streaming ? "stream " + type.Text : type.Text;
}
