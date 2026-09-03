using System.Text;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>
/// The names protoc's C# generator produces, computed from the parser's tree alone.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the pack <b>binds</b> through these. <see cref="ProtoGeneratedIndex"/> discovers each
/// C# name by reading an anchor protoc left in its own output — the descriptor index expression, the
/// <c>…FieldNumber</c> constant, the <c>OriginalName</c> attribute, <c>__ServiceName</c> — precisely
/// because the rules below are conventions that move between protoc releases, differ per plugin and
/// are overridable per file. A prediction that has drifted does not fail visibly, which is what
/// makes it the worse tool: the wrong name usually still resolves to <i>something</i>, so
/// go-to-definition lands on a plausible neighbour instead of on nothing.
/// </para>
/// <para>
/// What is left are the two questions no anchor answers. A <c>oneof</c> generates none — no
/// descriptor index, no constant, no attribute — so its members are looked up by predicted name
/// against the type its message did bind to. And a declaration in a project that has never been
/// built has no generated code to read a name out of, so a hover that wants to say what C# it maps
/// to has only these rules to say it with. Each is ported from <c>compiler/csharp/names.cc</c> and
/// <c>csharp_helpers.cc</c> in protobuf, or from <c>csharp_generator.cc</c> in grpc, and was checked
/// against real protoc output.
/// </para>
/// <para>
/// Type names are <b>not</b> converted and member names are. protoc builds a message or enum class
/// name straight from the proto name — <c>message UUID</c> is <c>class UUID</c> — while the field
/// <c>common.UUID uuid = 2;</c> becomes the property <c>Uuid</c>, because only members go through
/// <see cref="UnderscoresToPascalCase"/>. Converting both would fail to bind every message whose
/// name is not already PascalCase, and converting neither would miss every multi-word field.
/// </para>
/// <para>
/// The conversions are ASCII-only, as protoc's are: it works on UTF-8 bytes through
/// <c>absl::ascii_*</c>, which leave anything above 0x7F alone. Proto identifiers are ASCII by
/// grammar, so the two agree wherever it matters.
/// </para>
/// </remarks>
internal static class ProtoNaming
{
    /// <summary>
    /// The member names protoc refuses to let a property take, because the generated message class
    /// already declares them.
    /// </summary>
    /// <remarks>
    /// A field whose PascalCased name lands on one of these gets a trailing underscore instead:
    /// <c>parser</c> becomes <c>Parser_</c> and <c>to_string</c> becomes <c>ToString_</c>. This is
    /// not a general C# keyword guard and protoc does not claim it is one — <c>class</c> becomes
    /// plain <c>Class</c>, because the name the message already declares is what matters and a
    /// keyword is not one. Reproducing the list exactly is the only way to predict which of the
    /// two happens.
    /// </remarks>
    private static readonly HashSet<string> s_reservedMemberNames = new(StringComparer.Ordinal)
    {
        "Types",
        "Descriptor",
        "Equals",
        "ToString",
        "GetHashCode",
        "WriteTo",
        "Clone",
        "CalculateSize",
        "MergeFrom",
        "OnConstruction",
        "Parser",
    };

    private static readonly char[] s_pathSeparators = ['/', '\\'];

    /// <summary>The zero member every generated <c>…OneofCase</c> enum starts with.</summary>
    public const string OneofNoneCaseName = "None";

    /// <summary>The static class protoc nests a message's nested messages and enums inside.</summary>
    public const string NestedTypesContainerName = "Types";

    // ---- Primitives ----------------------------------------------------------------------------

    /// <summary>
    /// protoc's <c>UnderscoresToCamelCase</c>: the conversion behind every generated member name.
    /// </summary>
    /// <param name="capitalizeNext">Whether the first character is capitalised.</param>
    /// <param name="preservePeriods">Keeps <c>.</c> as a separator instead of dropping it, which is
    /// what turns a dotted package into a dotted namespace.</param>
    /// <remarks>
    /// <para>
    /// Two details in here are easy to get wrong and both are load-bearing. A capital letter after
    /// the first position is <b>left alone</b> rather than lowercased, so <c>UUID</c> stays
    /// <c>UUID</c> and <c>image_URL</c> becomes <c>ImageURL</c>. And a digit sets the capitalise
    /// flag, so <c>v2_widget</c> becomes <c>V2Widget</c> — the letter after a digit is capitalised
    /// even without a separator between them.
    /// </para>
    /// <para>
    /// The two trailing adjustments look like dead code and are not: <c>#</c> is legal in the
    /// synthetic names protoc builds internally, and the leading-underscore rule keeps
    /// <c>_2fa</c> from becoming the invalid identifier <c>2Fa</c>.
    /// </para>
    /// </remarks>
    public static string UnderscoresToCamelCase(string input, bool capitalizeNext, bool preservePeriods = false)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var result = new StringBuilder(input.Length);

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (char.IsAsciiLetterLower(c))
            {
                result.Append(capitalizeNext ? AsciiToUpper(c) : c);
                capitalizeNext = false;
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                result.Append(i == 0 && !capitalizeNext ? AsciiToLower(c) : c);
                capitalizeNext = false;
            }
            else if (char.IsAsciiDigit(c))
            {
                result.Append(c);
                capitalizeNext = true;
            }
            else
            {
                capitalizeNext = true;

                if (c == '.' && preservePeriods)
                    result.Append('.');
            }
        }

        if (input[^1] == '#')
            result.Append('_');

        if (result.Length > 0 && char.IsAsciiDigit(result[0]) && input[0] == '_')
            result.Insert(0, '_');

        return result.ToString();
    }

    /// <summary>protoc's <c>UnderscoresToPascalCase</c> — the capitalising form of
    /// <see cref="UnderscoresToCamelCase"/>, and the primitive under every member name.</summary>
    public static string UnderscoresToPascalCase(string input) =>
        UnderscoresToCamelCase(input, capitalizeNext: true);

    /// <summary>
    /// protoc's <c>ShoutyToPascalCase</c>, which turns <c>ALPHA_BETA</c> into <c>AlphaBeta</c>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="UnderscoresToPascalCase"/> because it <b>lowercases</b> a capital
    /// that follows another capital, which is what a SCREAMING_CASE enum value needs and what a
    /// field name must never get. Feeding an enum value to the wrong one yields <c>ALPHA</c> where
    /// protoc wrote <c>Alpha</c>.
    /// </remarks>
    public static string ShoutyToPascalCase(string input)
    {
        var result = new StringBuilder(input.Length);
        char previous = '_';

        foreach (char current in input)
        {
            if (!char.IsAsciiLetterOrDigit(current))
            {
                previous = current;
                continue;
            }

            result.Append(
                !char.IsAsciiLetterOrDigit(previous) || char.IsAsciiDigit(previous) ? AsciiToUpper(current)
                : char.IsAsciiLetterLower(previous) ? current
                : AsciiToLower(current));

            previous = current;
        }

        return result.ToString();
    }

    /// <summary>
    /// protoc's <c>TryRemovePrefix</c>: strips the enum's own name off the front of a value's name,
    /// or returns the value unchanged when it is not a prefix.
    /// </summary>
    /// <remarks>
    /// The comparison ignores case and ignores underscores <b>on both sides</b>, which is why
    /// <c>CHANNEL_ALPHA</c> in <c>enum Channel</c> loses its prefix at all: neither the case nor
    /// the separator matches literally. Two cases deliberately keep the original — a value that is
    /// only the prefix (<c>CHANNEL</c> in <c>Channel</c>) would otherwise become an empty member
    /// name, and a value shorter than the prefix runs out before matching it.
    /// </remarks>
    public static string TryRemovePrefix(string prefix, string value)
    {
        string prefixToMatch = LowercaseWithoutUnderscores(prefix);

        int prefixIndex = 0;
        int valueIndex = 0;

        for (; prefixIndex < prefixToMatch.Length && valueIndex < value.Length; valueIndex++)
        {
            if (value[valueIndex] == '_')
                continue;

            if (AsciiToLower(value[valueIndex]) != prefixToMatch[prefixIndex++])
                return value;
        }

        if (prefixIndex < prefixToMatch.Length)
            return value;

        while (valueIndex < value.Length && value[valueIndex] == '_')
            valueIndex++;

        return valueIndex == value.Length ? value : value[valueIndex..];
    }

    // ---- File ----------------------------------------------------------------------------------

    /// <summary>
    /// The C# namespace every type generated from this file lands in.
    /// </summary>
    /// <remarks>
    /// <c>option csharp_namespace</c> is taken verbatim — protoc does no conversion on it, so a
    /// lower-case option value produces a lower-case namespace. Only the fallback converts, and it
    /// is the one place <see cref="UnderscoresToCamelCase"/> runs with periods preserved:
    /// <c>package my_app.v2;</c> is namespace <c>MyApp.V2</c>. A file with neither option nor
    /// package generates its types into the global namespace, which is why the empty string is a
    /// real answer here rather than a failure.
    /// </remarks>
    public static string Namespace(ProtoFile file) =>
        file.CSharpNamespace ?? UnderscoresToCamelCase(file.Package, capitalizeNext: true, preservePeriods: true);

    /// <summary>
    /// The stem every name derived from the file itself is built on: the file name with its
    /// directory and its extension removed, PascalCased.
    /// </summary>
    /// <remarks>
    /// The extension is stripped at the <b>last</b> dot rather than by matching <c>.proto</c>, so
    /// <c>types.v1.proto</c> gives <c>TypesV1</c> — periods are not preserved here, unlike in
    /// <see cref="Namespace"/>. protoc reads the path the import graph gave it, always with forward
    /// slashes; taking it from the file's own path means also splitting on a backslash, and only
    /// the last segment survives either way.
    /// </remarks>
    public static string FileNameBase(ProtoFile file)
    {
        string path = file.FilePath;
        int separator = path.LastIndexOfAny(s_pathSeparators);
        var name = path.AsSpan(separator + 1);

        int dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[..dot];

        return UnderscoresToPascalCase(name.ToString());
    }

    /// <summary>
    /// The static class holding the file's <c>FileDescriptor</c> — <c>TypesReflection</c> for
    /// <c>common/types.proto</c>.
    /// </summary>
    /// <remarks>
    /// Every generated message points back at this class (<c>TypesReflection.Descriptor
    /// .MessageTypes[0]</c>), so it is the hinge the descriptor-expression binder turns on.
    /// <para>
    /// protoc does not guard this name against colliding with a message: a
    /// <c>message TypesReflection</c> in <c>types.proto</c> makes protoc emit two types with the
    /// same name and the generated code does not compile. The comment in protoc's source that once
    /// promised to append an underscore here has never been implemented, so neither is one here —
    /// inventing the underscore would mispredict every ordinary file to buy nothing on a file that
    /// cannot build anyway. <see cref="CollidesWithReflectionClass"/> reports the case instead.
    /// </para>
    /// </remarks>
    public static string ReflectionClassName(ProtoFile file) => FileNameBase(file) + "Reflection";

    /// <summary>The static class holding accessors for the file's top-level <c>extend</c>
    /// blocks — <c>TypesExtensions</c> for <c>types.proto</c>. Generated only when the file
    /// declares extensions outside a message.</summary>
    public static string ExtensionClassName(ProtoFile file) => FileNameBase(file) + "Extensions";

    /// <summary>Whether a declaration in this file takes the same name as
    /// <see cref="ReflectionClassName"/>, which is the one case where protoc's output for a
    /// well-formed proto does not compile.</summary>
    public static bool CollidesWithReflectionClass(ProtoFile file, ProtoDeclaration declaration) =>
        declaration.Kind is ProtoDeclarationKind.Message or ProtoDeclarationKind.Enum
        && declaration.Parent is null
        && declaration.Name.Value == ReflectionClassName(file);

    /// <summary>
    /// The messages-and-enums file protoc writes for this proto — <c>Types.cs</c> for
    /// <c>common/types.proto</c>.
    /// </summary>
    /// <remarks>
    /// The leaf name is protoc's; the directory is not. Grpc.Tools invokes protoc once per proto
    /// with an output directory that mirrors the proto's own directory under <c>obj/</c>, which is
    /// how two files both called <c>types.proto</c> end up as <c>common/Types.cs</c> and
    /// <c>widgets/Types.cs</c> without colliding.
    /// </remarks>
    public static string GeneratedFileName(ProtoFile file) => FileNameBase(file) + ".cs";

    /// <summary>The gRPC stub file — <c>WidgetsGrpc.cs</c> for <c>widgets/widgets.proto</c>.
    /// The plugin writes it whether or not the file declares a service, so an empty one means no
    /// services rather than a failed generation.</summary>
    public static string GrpcFileName(ProtoFile file) => FileNameBase(file) + "Grpc.cs";

    // ---- Messages and enums --------------------------------------------------------------------

    /// <summary>The class name alone, which is the proto name unchanged.</summary>
    public static string ClassName(ProtoMessage message) => message.Name.Value;

    /// <summary>The enum name alone, which is the proto name unchanged.</summary>
    public static string ClassName(ProtoEnum @enum) => @enum.Name.Value;

    /// <summary>
    /// The namespace-relative C# name, with protoc's <c>Types</c> container between each level of
    /// nesting: <c>message Outer { message Inner {} }</c> is <c>Outer.Types.Inner</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The container exists because a nested type and a field very often share a name — a message
    /// <c>Widget</c> holding a <c>Widget widget = 1;</c> — and C# will not let a class and a
    /// property of the same name sit in one type. Every nesting level gets its own container, so
    /// three levels deep is <c>A.Types.B.Types.C</c>. A nested enum goes in the same container as a
    /// nested message.
    /// </para>
    /// <para>
    /// This is the name as C# <i>source</i> spells it, <b>not</b> a CLR metadata name: metadata
    /// separates a nested type from its container with <c>+</c>, so <c>Widget.Types.Placement</c>
    /// here is <c>Widget+Types+Placement</c> there. Handing this to
    /// <c>Compilation.GetTypeByMetadataName</c> returns <c>null</c> without complaining, and nothing
    /// in this class produces the metadata form. A caller that wants the symbol should ask
    /// <see cref="ProtoGeneratedIndex"/> for it instead — it holds the type protoc's own anchors
    /// bound the declaration to, which is right even where these rules would be wrong.
    /// </para>
    /// </remarks>
    public static string NestedName(ProtoMessage message) => NestedName((ProtoDeclaration)message);

    /// <inheritdoc cref="NestedName(ProtoMessage)"/>
    public static string NestedName(ProtoEnum @enum) => NestedName((ProtoDeclaration)@enum);

    /// <summary>The fully-qualified C# name as a human reads it —
    /// <c>Sandbox.Proto.Widgets.Outer.Types.Inner</c>.</summary>
    public static string DisplayName(ProtoFile file, ProtoMessage message) =>
        Qualify(Namespace(file), NestedName(message));

    /// <inheritdoc cref="DisplayName(ProtoFile, ProtoMessage)"/>
    public static string DisplayName(ProtoFile file, ProtoEnum @enum) =>
        Qualify(Namespace(file), NestedName(@enum));

    // ---- Fields --------------------------------------------------------------------------------

    /// <summary>
    /// The property protoc generates for a field: <c>created_at</c> becomes <c>CreatedAt</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trailing underscore is protoc's only collision rule for members, and it fires on exactly
    /// two things: the name of the message the field is declared in, and the fixed set of members
    /// every generated message already has (<c>Parser</c>, <c>Descriptor</c>, <c>Clone</c>, …). So
    /// <c>message Widget { string widget = 1; }</c> generates <c>Widget_</c>. Fields never collide
    /// with each other, which is why nothing else is checked.
    /// </para>
    /// <para>
    /// This is also the name protoc lists in the generated reflection descriptor — <c>new[]{ "Id",
    /// "Uuid", "Label", "Channel", "CreatedAt", "ImageUrl", "ImageHash" }</c> — in the order of
    /// <see cref="ProtoMessage.AllFields"/>. Nothing binds through that array: a field binds through
    /// its <c>…FieldNumber</c> constant, which survives a rename where a name match would not. The
    /// array is what the tests check this prediction against, so a disagreement means these rules
    /// have drifted from the protoc that wrote the file.
    /// </para>
    /// </remarks>
    public static string PropertyName(ProtoField field)
    {
        string name = UnderscoresToPascalCase(field.Name.Value);

        return name == ContainingTypeName(field) || s_reservedMemberNames.Contains(name)
            ? name + "_"
            : name;
    }

    /// <summary>The <c>public const int IdFieldNumber = 1;</c> constant.</summary>
    /// <remarks>
    /// The constant's <i>value</i> is <see cref="ProtoField.Number"/>, which makes this pair the
    /// sharpest binding anchor a field has: the name can be rewritten by a rename and the number
    /// cannot, so a name that matches and a number that does not means the generated code is stale.
    /// </remarks>
    public static string FieldNumberConstName(ProtoField field) => PropertyName(field) + "FieldNumber";

    /// <summary>The <c>bool HasImageUrl</c> presence property, when the field has one.</summary>
    /// <remarks>
    /// protoc builds this and <see cref="ClearMethodName(ProtoField)"/> from the same string it
    /// built the property from, so a caller holding the bound property should prefix
    /// <c>ISymbol.Name</c> instead of coming through here: that agrees with protoc by construction,
    /// where this agrees only as long as <see cref="PropertyName"/> does.
    /// </remarks>
    public static string HasPropertyName(ProtoField field) => "Has" + PropertyName(field);

    /// <summary>The <c>void ClearImageUrl()</c> method, when the field has one.</summary>
    /// <inheritdoc cref="HasPropertyName" path="/remarks"/>
    public static string ClearMethodName(ProtoField field) => "Clear" + PropertyName(field);

    /// <summary>
    /// Whether the field has explicit presence, which is the syntactic half of protoc's rule for
    /// generating <see cref="HasPropertyName"/> and <see cref="ClearMethodName"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Presence follows the dialect: a <c>oneof</c> member always has it, proto2 gives it to
    /// <c>optional</c> and <c>required</c>, proto3 gives it only to a field written
    /// <c>optional</c>, and a <c>repeated</c> field or a <c>map</c> never has it. Editions default
    /// to explicit presence, which is why a bare field counts as having it there; a file that moves
    /// <c>features.field_presence</c> is not modelled, matching the parser.
    /// </para>
    /// <para>
    /// The other half of protoc's rule cannot be answered from one file and is deliberately left to
    /// the caller: protoc omits both members when the field's type is a message or a group, because
    /// a null reference already carries presence in C#. The sandbox's
    /// <c>google.protobuf.Empty reset = 2;</c> sits in a oneof and gets a <c>Reset</c> property with
    /// neither <c>HasReset</c> nor <c>ClearReset</c>, while its sibling <c>string set = 1;</c> gets
    /// both. Telling a message reference from an enum reference needs the import graph, so the
    /// caller — which holds the resolved symbol — applies that half.
    /// </para>
    /// </remarks>
    public static bool HasExplicitPresence(ProtoField field, ProtoSyntaxLevel syntax) =>
        !field.IsMap
        && field.Label != ProtoFieldLabel.Repeated
        && (field.Oneof is not null
            || field.Label is ProtoFieldLabel.Optional or ProtoFieldLabel.Required
            || syntax == ProtoSyntaxLevel.Edition);

    /// <summary>The fully-qualified property, for hover — <c>Sandbox.Proto.Widgets.Widget.CreatedAt</c>.</summary>
    public static string DisplayName(ProtoFile file, ProtoField field) =>
        field.Parent is ProtoMessage message
            ? DisplayName(file, message) + "." + PropertyName(field)
            : Qualify(Namespace(file), PropertyName(field));

    // ---- Oneofs --------------------------------------------------------------------------------

    /// <summary>The <c>enum ImageOneofCase</c> protoc nests in the message for
    /// <c>oneof image</c>.</summary>
    /// <remarks>
    /// Nested directly in the message class, <b>not</b> in its <c>Types</c> container — the
    /// container holds only what the proto itself declared nested.
    /// </remarks>
    public static string OneofCaseEnumName(ProtoOneof oneof) =>
        UnderscoresToPascalCase(oneof.Name.Value) + "OneofCase";

    /// <summary>The <c>ImageOneofCase ImageCase</c> property that reports which member is set.</summary>
    public static string OneofCasePropertyName(ProtoOneof oneof) =>
        UnderscoresToPascalCase(oneof.Name.Value) + "Case";

    /// <summary>The <c>void ClearImage()</c> method that unsets the whole oneof.</summary>
    public static string ClearMethodName(ProtoOneof oneof) =>
        "Clear" + UnderscoresToPascalCase(oneof.Name.Value);

    /// <summary>
    /// A member of the <c>…OneofCase</c> enum, one per field in the oneof.
    /// </summary>
    /// <remarks>
    /// It is the field's property name, except that a field which would produce <c>None</c> becomes
    /// <c>None_</c> — the enum already declares <c>None = 0</c> for "nothing set", and that member
    /// is not derived from any field. Each other member's value is the field's wire number, not its
    /// position, so the enum is sparse whenever the numbers are.
    /// </remarks>
    public static string OneofCaseName(ProtoField field)
    {
        string name = PropertyName(field);
        return name == OneofNoneCaseName ? OneofNoneCaseName + "_" : name;
    }

    // ---- Enum values ---------------------------------------------------------------------------

    /// <summary>
    /// The C# member protoc generates for an enum value: <c>CHANNEL_ALPHA</c> in <c>enum Channel</c>
    /// becomes <c>Alpha</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// protoc strips the enum's own name off the front, comparing case- and underscore-insensitively
    /// (see <see cref="TryRemovePrefix"/>), then runs <see cref="ShoutyToPascalCase"/> over what is
    /// left. The generated member keeps the proto spelling in a
    /// <c>[pbr::OriginalName("CHANNEL_ALPHA")]</c> attribute, which is the anchor that lets a binder
    /// check this prediction instead of trusting it.
    /// </para>
    /// <para>
    /// A result starting with a digit gets a leading underscore, because <c>SYNTAX_2</c> would
    /// otherwise be the invalid identifier <c>2</c>. An empty result — reachable only from a value
    /// named entirely in underscores — has no name protoc can emit at all; the proto name is
    /// returned instead so that this method is total and hover still shows something.
    /// </para>
    /// </remarks>
    public static string EnumMemberName(string enumName, string valueName)
    {
        string result = ShoutyToPascalCase(TryRemovePrefix(enumName, valueName));

        if (result.Length == 0)
            return valueName;

        return char.IsAsciiDigit(result[0]) ? "_" + result : result;
    }

    /// <inheritdoc cref="EnumMemberName(string, string)"/>
    public static string EnumMemberName(ProtoEnumValue value) =>
        EnumMemberName(value.Parent is ProtoEnum @enum ? @enum.Name.Value : string.Empty, value.Name.Value);

    /// <summary>The fully-qualified enum member, for hover —
    /// <c>Sandbox.Proto.Common.Channel.Alpha</c>.</summary>
    public static string DisplayName(ProtoFile file, ProtoEnumValue value) =>
        value.Parent is ProtoEnum @enum
            ? DisplayName(file, @enum) + "." + EnumMemberName(value)
            : Qualify(Namespace(file), EnumMemberName(value));

    // ---- Services ------------------------------------------------------------------------------

    /// <summary>The outer static class the gRPC plugin generates for a service, which is the proto
    /// name unchanged.</summary>
    /// <remarks>
    /// It holds nothing callable itself — the base class, the client and <c>BindService</c> hang off
    /// it — so navigating a proto <c>service</c> to "the C# implementation" means going to
    /// <see cref="ServiceBaseName"/> and from there to its derived types.
    /// </remarks>
    public static string ServiceClassName(ProtoService service) => service.Name.Value;

    /// <summary>The <c>abstract partial class WidgetServiceBase</c> a server implementation derives
    /// from.</summary>
    public static string ServiceBaseName(ProtoService service) => service.Name.Value + "Base";

    /// <summary>The <c>partial class WidgetServiceClient : grpc::ClientBase&lt;…&gt;</c> callers
    /// construct.</summary>
    public static string ServiceClientName(ProtoService service) => service.Name.Value + "Client";

    /// <summary>The service class, namespace included —
    /// <c>Sandbox.Proto.Widgets.WidgetService</c>.</summary>
    public static string ServiceDisplayName(ProtoFile file, ProtoService service) =>
        Qualify(Namespace(file), ServiceClassName(service));

    /// <summary>
    /// The wire name of the service, which is its fully-qualified proto name.
    /// </summary>
    /// <remarks>
    /// The generated class carries it as
    /// <c>static readonly string __ServiceName = "widgets.WidgetService";</c>. That string is
    /// independent of every C# naming rule in this class, which makes it the one anchor that binds a
    /// proto service to its generated class even if protoc's naming changed underneath.
    /// </remarks>
    public static string GrpcServiceName(ProtoService service) => service.FullName;

    /// <summary>
    /// The method name an rpc generates, which is the rpc name unchanged.
    /// </summary>
    /// <remarks>
    /// One name, three members: the base class's <c>virtual</c> method, the client's blocking
    /// overloads, and the private <c>__Method_…</c> descriptor field. The plugin has a mode that
    /// suffixes the base-class method with <c>Async</c> (guarded so a name already ending in
    /// <c>Async</c> is not doubled); Grpc.Tools does not turn it on, and the sandbox's
    /// <c>WidgetServiceBase.GetWidgetsById</c> confirms the plain form.
    /// </remarks>
    public static string MethodName(ProtoRpc rpc) => rpc.Name.Value;

    /// <summary>
    /// The client method that returns a call object rather than a value.
    /// </summary>
    /// <remarks>
    /// The <c>Async</c> suffix exists only to keep a unary rpc's two client overloads apart — the
    /// blocking <c>GetWidgetsById</c> and the <c>AsyncUnaryCall</c>-returning
    /// <c>GetWidgetsByIdAsync</c>. A streaming rpc has no blocking form to clash with, so its client
    /// method keeps the bare rpc name and looking for <c>…Async</c> would find nothing. The suffix
    /// is appended unconditionally here, so an rpc already called <c>FooAsync</c> becomes
    /// <c>FooAsyncAsync</c> — that is what the plugin does.
    /// </remarks>
    public static string AsyncMethodName(ProtoRpc rpc) =>
        IsUnary(rpc) ? rpc.Name.Value + "Async" : rpc.Name.Value;

    /// <summary>Whether the rpc is unary, which is exactly when the client gets a blocking overload
    /// and an <c>Async</c>-suffixed one.</summary>
    public static bool IsUnary(ProtoRpc rpc) => !rpc.ClientStreaming && !rpc.ServerStreaming;

    /// <summary>The private <c>__Method_GetWidgetsById</c> descriptor field. Not navigable, but
    /// finding it in a class proves that class was generated from this rpc.</summary>
    public static string MethodFieldName(ProtoRpc rpc) => "__Method_" + rpc.Name.Value;

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>
    /// The declaration's name walked out through its enclosing messages, with protoc's <c>Types</c>
    /// container between each pair.
    /// </summary>
    /// <remarks>
    /// The ancestry is walked rather than sliced off <see cref="ProtoDeclaration.FullName"/> because
    /// the two disagree where it matters: a package prefix has to be stripped by length, which a
    /// file with no package or a name that repeats its package turns into an off-by-one. Only a
    /// message can enclose a generated type, so the walk needs no filtering.
    /// </remarks>
    private static string NestedName(ProtoDeclaration declaration)
    {
        if (declaration.Parent is null)
            return declaration.Name.Value;

        var segments = new List<string>();

        for (ProtoDeclaration? current = declaration; current is not null; current = current.Parent)
            segments.Add(current.Name.Value);

        var result = new StringBuilder();

        for (int i = segments.Count - 1; i >= 0; i--)
        {
            if (i < segments.Count - 1)
                result.Append('.').Append(NestedTypesContainerName).Append('.');

            result.Append(segments[i]);
        }

        return result.ToString();
    }

    /// <summary>Prefixes a namespace, or leaves the name alone when the file has none — a proto
    /// with neither <c>csharp_namespace</c> nor <c>package</c> generates into the global
    /// namespace, and a leading dot there would name nothing.</summary>
    private static string Qualify(string @namespace, string name) =>
        @namespace.Length == 0 ? name : @namespace + "." + name;

    /// <summary>
    /// The name protoc compares a property against for the trailing-underscore rule.
    /// </summary>
    /// <remarks>
    /// For an ordinary field and for a <c>oneof</c> member alike this is the enclosing message,
    /// since the parser scopes a oneof's members on the message. For a field in an <c>extend</c>
    /// block it is the <b>extended</b> message — protoc's <c>containing_type()</c> of an extension
    /// is its extendee — and only that message's simple name, so a fully-qualified target has to
    /// lose its package first.
    /// </remarks>
    private static string ContainingTypeName(ProtoField field) => field.Parent switch
    {
        ProtoMessage message => message.Name.Value,
        ProtoExtend extend => SimpleName(extend.Target.Text),
        _ => string.Empty,
    };

    private static string SimpleName(string dottedName)
    {
        int dot = dottedName.LastIndexOf('.');
        return dot < 0 ? dottedName : dottedName[(dot + 1)..];
    }

    private static string LowercaseWithoutUnderscores(string value)
    {
        var result = new StringBuilder(value.Length);

        foreach (char c in value)
        {
            if (c != '_')
                result.Append(AsciiToLower(c));
        }

        return result.ToString();
    }

    private static char AsciiToLower(char c) => char.IsAsciiLetterUpper(c) ? (char)(c + ('a' - 'A')) : c;

    private static char AsciiToUpper(char c) => char.IsAsciiLetterLower(c) ? (char)(c - ('a' - 'A')) : c;
}
