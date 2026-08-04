using System.Text;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.Proto.Tools;

/// <summary>
/// Resolves a marked snippet in a <c>.proto</c> to whatever it names, by mapping the snippet to an
/// offset and asking the same resolver the editor's go-to-definition uses.
/// </summary>
/// <remarks>
/// <para>
/// A caret in a <c>.proto</c> has two definitions and <see cref="ProtoHit"/> carries both, so the
/// front-end has to choose. This one chooses by what would move the caller: a <b>reference</b>
/// names something written elsewhere, so the <c>message</c> or <c>enum</c> it points at is the
/// answer and the generated class is a footnote; a <b>declaration name</b> is already sitting on
/// the proto definition, so the only answer that goes anywhere is the C# protoc built from it.
/// </para>
/// <para>
/// That second case is handed to <see cref="GoToDefinitionSnippetTool.FormatDefinitionAsync"/>
/// rather than formatted here, so navigating from <c>service WidgetService</c> produces the same
/// report a C# caret on the generated class would — including the member table, which is the list
/// of rpcs an implementer has to override.
/// </para>
/// </remarks>
internal class ProtoGoToDefinition(IOutputFormatter fmt) : IGoToDefinitionHandler
{
    public bool CanHandle(string filePath) => ProtoDocumentService.IsProtoFile(filePath);

    public async Task<string> ResolveAsync(
        string systemPath, string markupSnippet, int contextLines, CancellationToken cancellationToken)
    {
        if (!MarkupString.TryParse(markupSnippet, out var markup, out string? parseError))
            return $"Error: Invalid markup snippet. {parseError}";

        // No project check first, unlike find-usages: a proto-to-proto jump resolves through the
        // import graph alone, so a file belonging to no project still answers most of these.
        var view = await ProtoWorkspace.GetAsync(systemPath, cancellationToken);
        if (view is null)
        {
            return $"Error: Couldn't load '{Path.GetFileName(systemPath)}'. " +
                   "The file must exist and be a readable .proto.";
        }

        var hit = ProtoMarkup.FindMarkedSpan(view.Text, markup!) is { } marked
            ? ProtoSymbolResolver.ResolveAt(view, marked.Start)
            : null;

        if (hit is null)
            return $"No proto declaration or reference found for '{markup!.MarkedText}'.";

        if (hit.TypeRef is null && hit.Symbol is { } symbol && view.Project is { } project)
        {
            return await GoToDefinitionSnippetTool.FormatDefinitionAsync(
                symbol, project, contextLines, fmt, cancellationToken);
        }

        return hit.Kind switch
        {
            ProtoHitKind.Import => FormatImport(hit, markup!),
            _ => FormatProtoDefinition(view, hit, markup!, contextLines),
        };
    }

    // ---- The proto side ---------------------------------------------------------------------

    /// <summary>
    /// The report for a definition that lives in a <c>.proto</c>: the declaration a reference names,
    /// or — when the project has never been built and there is no symbol to hand off — the
    /// declaration the caret is on.
    /// </summary>
    private static string FormatProtoDefinition(
        ProtoProjectView view, ProtoHit hit, MarkupString markup, int contextLines)
    {
        if (hit.Target is not { } target)
            return FormatUnresolved(hit, markup);

        // Set only for a reference that resolved into another file; a declaration the caret is on
        // is in the file the caret is in.
        var file = hit.TargetFile ?? view.Parse;

        var sb = new StringBuilder();
        sb.AppendLine($"# Definition: {target.Name.Value}");
        sb.AppendLine();

        sb.AppendLine($"- **Proto**: {target.FullName}");
        sb.AppendLine($"- **Kind**: {target.Kind}");
        sb.AppendLine($"- **File**: {file.FilePath}");
        AppendLineRange(sb, file.Text, target.Span, target.Name.Span);

        if (target.Documentation is { Length: > 0 } documentation)
        {
            sb.AppendLine();
            sb.AppendLine("## Documentation");
            sb.AppendLine();
            sb.AppendLine(documentation);
        }

        sb.AppendLine();
        AppendProtoContext(sb, file.Text, target.Name.Span, contextLines);
        AppendGeneratedCSharp(sb, hit, view);

        return sb.ToString();
    }

    /// <summary>
    /// The report for a caret on an <c>import</c>: the file it pulls in, and what that file
    /// declares — which is the question someone asking about an import is actually asking.
    /// </summary>
    private static string FormatImport(ProtoHit hit, MarkupString markup)
    {
        string path = hit.Name ?? markup.MarkedText;

        var sb = new StringBuilder();
        sb.AppendLine($"# Definition: {path}");
        sb.AppendLine();
        sb.AppendLine($"- **Import**: {path}");

        if (hit.Import is { } import)
        {
            if (import.IsPublic)
                sb.AppendLine("- **Re-exported**: yes — files importing this one may name what it imports");
            if (import.IsWeak)
                sb.AppendLine("- **Weak**: yes");
        }

        var imported = hit.TargetPath is { Length: > 0 } target
            ? ProtoDocumentService.GetParse(target)
            : null;

        if (imported is null)
        {
            sb.AppendLine("- **File**: not found on disk");
            sb.AppendLine();
            sb.AppendLine(ProtoWellKnownTypes.IsWellKnownPath(path)
                ? "This is one of protoc's own imports. It ships inside the Grpc.Tools package rather " +
                  "than in the project, so the file is often not on this machine even though the build " +
                  "resolves it — `google.protobuf.*` types still resolve from the built-in table."
                : "The import could not be resolved against the project directory or the standard " +
                  "imports directory, so nothing it declares can be named from this file.");
            return sb.ToString();
        }

        sb.AppendLine($"- **File**: {imported.FilePath}");
        if (imported.Package is { Length: > 0 } package)
            sb.AppendLine($"- **Package**: {package}");

        AppendDeclarationList(sb, "Messages", imported.Messages.Select(message => message.Name.Value));
        AppendDeclarationList(sb, "Enums", imported.Enums.Select(@enum => @enum.Name.Value));
        AppendDeclarationList(sb, "Services", imported.Services.Select(service => service.Name.Value));

        return sb.ToString();
    }

    /// <summary>
    /// What is known about a caret whose target could not be found — which is a more useful answer
    /// than a flat failure, because the hit kind says whether the name was unresolvable or the
    /// caret was on something that names nothing at all.
    /// </summary>
    private static string FormatUnresolved(ProtoHit hit, MarkupString markup)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# No definition for: {markup.MarkedText}");
        sb.AppendLine();
        sb.AppendLine($"- **Caret is on**: {hit.Kind}");

        if (hit.WellKnown is { } wellKnown)
        {
            sb.AppendLine($"- **Proto**: {wellKnown.FullName}");
            sb.AppendLine($"- **Generated C#**: {wellKnown.ClrTypeName}");
            sb.AppendLine($"- **Declared in**: {wellKnown.ProtoPath}");
            sb.AppendLine();
            sb.AppendLine(
                "One of protoc's own types. Its C# lives in the Google.Protobuf runtime assembly " +
                "rather than in a generated file, so there is nothing in this solution to open.");
            return sb.ToString();
        }

        if (hit.TypeRef is { IsScalar: true } scalar)
        {
            sb.AppendLine($"- **Scalar**: {scalar.Text}");
            sb.AppendLine();
            sb.AppendLine("A built-in wire type. It has no declaration anywhere to navigate to.");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine(hit.TypeRef is not null
            ? "The name resolves to nothing visible from this file. Protobuf lookup only sees this " +
              "file's own declarations and those of the files it imports directly, plus whatever " +
              "those re-export with `import public` — a missing `import` is the usual cause."
            : "Nothing at this position declares or names a type.");

        return sb.ToString();
    }

    // ---- Shared sections ----------------------------------------------------------------------

    /// <summary>
    /// The generated class, method or property the declaration is bound to.
    /// </summary>
    /// <remarks>
    /// Read off the hit rather than looked up again: the resolver already bound it while working
    /// out what the caret was on, and a second lookup here is a second chance to disagree with the
    /// editor about the same caret.
    /// </remarks>
    private static void AppendGeneratedCSharp(StringBuilder sb, ProtoHit hit, ProtoProjectView view)
    {
        sb.AppendLine("## Generated C#");
        sb.AppendLine();

        if (hit.Symbol is not { } symbol)
        {
            sb.AppendLine(view.Index.IsEmpty
                ? "Not bound — the project has produced no generated code yet. Build it and the " +
                  "declaration will bind to the class protoc writes for it."
                : "Not bound — the generated code does not mention this declaration, which usually " +
                  "means it was added since the last build.");

            if (hit.WellKnown is { } wellKnown)
                sb.AppendLine($"The runtime type for this name is `{wellKnown.ClrTypeName}`.");

            return;
        }

        sb.AppendLine($"- **Symbol**: {symbol.ToDisplayString()}");
        sb.AppendLine($"- **Kind**: {symbol.Kind}");

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false } @namespace)
            sb.AppendLine($"- **Namespace**: {@namespace.ToDisplayString()}");

        if (symbol.Locations.FirstOrDefault(location => location.IsInSource) is { } source)
        {
            var lineSpan = source.GetLineSpan();
            sb.AppendLine($"- **File**: {lineSpan.Path}");
            sb.AppendLine($"- **Line**: {lineSpan.StartLinePosition.Line + 1}");
        }
    }

    /// <summary>The declaration's line, or its line range when it has a body.</summary>
    private static void AppendLineRange(StringBuilder sb, SourceText text, TextSpan span, TextSpan nameSpan)
    {
        int start = ProtoMarkup.LineOf(text, nameSpan.Start);
        int end = ProtoMarkup.LineOf(text, span.End);

        sb.AppendLine(end > start ? $"- **Lines**: {start}–{end}" : $"- **Line**: {start}");
    }

    /// <summary>
    /// The source around the declaration, with the declaring line marked.
    /// </summary>
    /// <remarks>
    /// Fenced as <c>proto</c> and not <c>csharp</c>, and read from the parse's own
    /// <see cref="SourceText"/> rather than from disk — which is what makes the snippet agree with
    /// an unsaved buffer, since that is the text the parse was taken from.
    /// </remarks>
    private static void AppendProtoContext(StringBuilder sb, SourceText text, TextSpan span, int contextLines)
    {
        int target = ProtoMarkup.LineOf(text, span.Start) - 1;
        int start = Math.Max(0, target - contextLines);
        int end = Math.Min(text.Lines.Count - 1, target + contextLines);

        sb.AppendLine("```proto");
        for (int line = start; line <= end; line++)
        {
            string content = text.Lines[line].ToString();
            sb.AppendLine(line == target ? $"{line + 1}: > {content}" : $"{line + 1}:   {content}");
        }

        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void AppendDeclarationList(StringBuilder sb, string title, IEnumerable<string> names)
    {
        var listed = names.ToList();
        if (listed.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        foreach (string name in listed)
            sb.AppendLine($"- {name}");
    }
}
