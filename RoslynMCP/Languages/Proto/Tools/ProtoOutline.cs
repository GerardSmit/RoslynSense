using System.Collections.Immutable;
using System.Text;
using RoslynMCP.Languages.Proto.Core;

namespace RoslynMCP.Languages.Proto.Tools;

/// <summary>
/// Produces a structured outline for a <c>.proto</c>: what the file declares, how it nests, and
/// which C# protoc generated for each declaration.
/// </summary>
/// <remarks>
/// <para>
/// The tree is walked through <see cref="ProtoDeclaration.ChildDeclarations"/> rather than through
/// each node's typed collections, because that is the only view in source order and the only one
/// where a <c>oneof</c> still contains its members — the typed <c>Fields</c> collection of a
/// message deliberately excludes them, and printing the two lists one after the other would show a
/// message's fields in an order it does not have.
/// </para>
/// <para>
/// Every entry carries the symbol it bound to when the project has been built. That mapping is the
/// reason the pack exists, and an outline that shows it answers "what is <c>Widget.CreatedAt</c> in
/// the proto" without a second call.
/// </para>
/// </remarks>
internal class ProtoOutline : IOutlineHandler
{
    private const int MaxDocumentationLength = 100;

    public bool CanHandle(string filePath) => ProtoDocumentService.IsProtoFile(filePath);

    public Task<string> GetOutlineAsync(string filePath, CancellationToken cancellationToken) =>
        FormatAsync(filePath, cancellationToken);

    /// <summary>The markdown outline of one <c>.proto</c>.</summary>
    internal static async Task<string> FormatAsync(string filePath, CancellationToken ct)
    {
        var view = await ProtoWorkspace.GetAsync(filePath, ct);
        if (view is null)
        {
            return $"Error: Couldn't load '{Path.GetFileName(filePath)}'. " +
                   "The file must exist and be a readable .proto.";
        }

        var parse = view.Parse;
        var index = view.Index;

        var sb = new StringBuilder();
        sb.AppendLine($"**Proto File: {Path.GetFileName(parse.FilePath)}**");
        sb.AppendLine();

        AppendFileInfo(sb, view);
        AppendImports(sb, parse, view.ProjectDirectory);
        AppendSection(sb, "Services", parse.Services, parse, index);
        AppendSection(sb, "Messages", parse.Messages, parse, index);
        AppendSection(sb, "Enums", parse.Enums, parse, index);
        AppendSection(sb, "Extensions", parse.Extends, parse, index);
        AppendOptions(sb, parse);
        AppendDiagnostics(sb, parse);

        return sb.ToString();
    }

    // ---- The file itself ----------------------------------------------------------------------

    private static void AppendFileInfo(StringBuilder sb, ProtoProjectView view)
    {
        var parse = view.Parse;

        sb.AppendLine($"- **Syntax**: {SyntaxOf(parse)}");
        sb.AppendLine($"- **Package**: {(parse.Package.Length > 0 ? parse.Package : "(none)")}");

        string @namespace = ProtoNaming.Namespace(parse);
        sb.AppendLine($"- **C# Namespace**: {(@namespace.Length > 0 ? @namespace : "(global)")}");

        if (view.Project is { } project)
        {
            sb.AppendLine($"- **Project**: {(project.FilePath is { } path ? Path.GetFileName(path) : project.Name)}");

            var generated = view.Index.DocumentsFor(parse.FilePath);
            sb.AppendLine(generated.IsDefaultOrEmpty
                ? "- **Generated code**: none — the project has not been built since this file was added"
                : $"- **Generated code**: {string.Join(", ", generated.Select(document => document.Name))}");
        }

        sb.AppendLine();
    }

    private static string SyntaxOf(ProtoFile parse) =>
        parse.Edition is { } edition
            ? $"edition {edition}"
            : parse.SyntaxLevel == ProtoSyntaxLevel.Proto3 ? "proto3" : "proto2";

    /// <summary>
    /// The imports, each with the file it resolves to.
    /// </summary>
    /// <remarks>
    /// The resolved path is worth the line: protobuf name lookup only sees what is imported
    /// directly, so an import that resolves to nothing is the reason every unresolved type in the
    /// file is unresolved, and nothing else in an outline would say so.
    /// </remarks>
    private static void AppendImports(StringBuilder sb, ProtoFile parse, string? projectDirectory)
    {
        if (parse.Imports.Length == 0)
            return;

        sb.AppendLine("## Imports");
        foreach (var import in parse.Imports)
        {
            var modifiers = new List<string>();
            if (import.IsPublic) modifiers.Add("public");
            if (import.IsWeak) modifiers.Add("weak");

            string qualifier = modifiers.Count > 0 ? $" ({string.Join(", ", modifiers)})" : string.Empty;
            string resolved = ProtoImportResolver.Resolve(import.Path, parse.FilePath, projectDirectory)
                ?? (ProtoWellKnownTypes.IsWellKnownPath(import.Path)
                    ? "resolved by protoc from its own imports directory"
                    : "**not found**");

            sb.AppendLine(
                $"- `{import.Path}`{qualifier} at line {ProtoMarkup.LineOf(parse.Text, import.Span.Start)} → {resolved}");
        }

        sb.AppendLine();
    }

    private static void AppendOptions(StringBuilder sb, ProtoFile parse)
    {
        if (parse.Options.Length == 0)
            return;

        sb.AppendLine("## File Options");
        foreach (var option in parse.Options)
            sb.AppendLine($"- `{option.Name}` = `{option.Value ?? "(unparsed)"}` at line {ProtoMarkup.LineOf(parse.Text, option.Span.Start)}");

        sb.AppendLine();
    }

    private static void AppendDiagnostics(StringBuilder sb, ProtoFile parse)
    {
        if (parse.Diagnostics.Length == 0)
            return;

        sb.AppendLine("## Parse Diagnostics");
        foreach (var diagnostic in parse.Diagnostics)
        {
            sb.AppendLine(
                $"- **{diagnostic.Severity}** {diagnostic.Id} at line " +
                $"{ProtoMarkup.LineOf(parse.Text, diagnostic.Span.Start)}: {diagnostic.Message}");
        }

        sb.AppendLine();
    }

    // ---- Declarations -------------------------------------------------------------------------

    /// <summary>Generic over the declaration type so each of the file's typed collections passes
    /// straight in, unboxed.</summary>
    private static void AppendSection<TDeclaration>(
        StringBuilder sb, string title, ImmutableArray<TDeclaration> declarations,
        ProtoFile parse, ProtoGeneratedIndex index)
        where TDeclaration : ProtoDeclaration
    {
        if (declarations.Length == 0)
            return;

        sb.AppendLine($"## {title}");
        foreach (var declaration in declarations)
            AppendDeclaration(sb, declaration, parse, index, depth: 0);

        sb.AppendLine();
    }

    private static void AppendDeclaration(
        StringBuilder sb, ProtoDeclaration declaration, ProtoFile parse, ProtoGeneratedIndex index, int depth)
    {
        string indent = new(' ', depth * 2);

        // The declaration's start and not its name's, because a declaration whose name has not been
        // typed yet carries a default name span — which would report every one of them as line 1.
        int line = ProtoMarkup.LineOf(
            parse.Text,
            declaration.Name.Span.IsEmpty ? declaration.Span.Start : declaration.Name.Span.Start);

        string binding = Binding(index, declaration) is { Length: > 0 } bound ? $" → `{bound}`" : string.Empty;

        sb.AppendLine($"{indent}- {Label(declaration)} at line {line}{binding}");

        if (Documentation(declaration) is { Length: > 0 } documentation)
            sb.AppendLine($"{indent}  _{documentation}_");

        foreach (var child in declaration.ChildDeclarations)
            AppendDeclaration(sb, child, parse, index, depth + 1);
    }

    /// <summary>
    /// How one declaration reads in the outline.
    /// </summary>
    /// <remarks>
    /// A declaration whose name has not been typed yet is still listed, under a placeholder, the
    /// way the editor's own outline lists it: everything already written inside it hangs off this
    /// entry, and dropping it would empty the outline of a file from the moment someone types
    /// <c>message</c> at the top of it.
    /// </remarks>
    private static string Label(ProtoDeclaration declaration) => declaration switch
    {
        ProtoMessage message => $"**message {Named(message)}**",
        ProtoEnum @enum => $"**enum {Named(@enum)}**",
        ProtoEnumValue value => $"`{Named(value)} = {value.Number}`",
        ProtoService service => $"**service {Named(service)}**",
        ProtoRpc rpc => $"**rpc** `{Signature(rpc)}`",
        ProtoOneof oneof => $"**oneof {Named(oneof)}**",
        ProtoField field => $"`{Signature(field)}`",
        ProtoExtend extend => $"**extend {extend.Target.Text}**",
        _ => Named(declaration),
    };

    private static string Named(ProtoDeclaration declaration) =>
        declaration.Name.Value is { Length: > 0 } name ? name : "(unnamed)";

    /// <summary>
    /// The rpc as it is written, and the field as it is written.
    /// </summary>
    /// <remarks>
    /// Assembled from <see cref="ProtoDeclarationText"/> rather than taken whole from it, because
    /// the name is <see cref="Named"/> here: an outline lists a declaration whose name has not been
    /// typed yet, and the canonical spelling would leave a blank where the placeholder goes. Which
    /// side of an rpc streams, and a map's key type, are grammar and come from the shared helper.
    /// </remarks>
    private static string Signature(ProtoRpc rpc) =>
        $"{Named(rpc)}{ProtoDeclarationText.Parameters(rpc)}";

    /// <inheritdoc cref="Signature(ProtoRpc)"/>
    private static string Signature(ProtoField field) =>
        $"{ProtoDeclarationText.Label(field)}{ProtoDeclarationText.TypeText(field)} " +
        $"{Named(field)} = {field.Number}";

    /// <summary>
    /// The C# a declaration bound to, or <c>null</c> when nothing generated it.
    /// </summary>
    /// <remarks>
    /// A type gets its fully-qualified name because the namespace and protoc's <c>Types</c>
    /// containers are not guessable from the proto; a member gets its bare name, because the type
    /// it hangs off is on the line above it in the outline.
    /// </remarks>
    private static string? Binding(ProtoGeneratedIndex index, ProtoDeclaration declaration) => declaration switch
    {
        ProtoMessage message => index.TypeFor(message)?.ToDisplayString(),
        ProtoEnum @enum => index.TypeFor(@enum)?.ToDisplayString(),
        ProtoEnumValue value => index.MemberFor(value)?.Name,
        ProtoService service => (index.ServiceTypeFor(service) ?? index.ServiceBaseFor(service))?.ToDisplayString(),
        ProtoRpc rpc => (index.BaseMethodFor(rpc) ?? index.ClientMethodFor(rpc))?.Name,
        ProtoField field => index.PropertyFor(field)?.Name,
        _ => null,
    };

    /// <summary>The declaration's comment block, flattened to one line. An outline that reflowed a
    /// paragraph into its bullet list would stop being an outline.</summary>
    private static string? Documentation(ProtoDeclaration declaration)
    {
        if (declaration.Documentation is not { Length: > 0 } documentation)
            return null;

        string flattened = string.Join(' ', documentation.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()));

        return flattened.Length > MaxDocumentationLength
            ? flattened[..(MaxDocumentationLength - 3)] + "..."
            : flattened;
    }
}
